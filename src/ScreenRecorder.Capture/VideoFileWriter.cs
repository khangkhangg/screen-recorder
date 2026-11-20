using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenRecorder.Capture;

// Writes H.264 video and AAC audio into a fragmented MP4, so a file cut off by a crash still plays.
public sealed class VideoFileWriter : IVideoWriter
{
    const int AudioRate = 48000;
    const long TicksPerSecond = 10_000_000;
    const int SinkNoSamplesProcessed = unchecked((int)0xC00D4A44);

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly IMFDXGIDeviceManager? _manager;
    readonly IMFSinkWriter _writer;
    readonly int _frameRate, _videoStream, _audioStream = -1;
    readonly BlockingCollection<(int Stream, IMFSample Sample)> _queue = new();
    readonly Thread _thread;
    long _behind;
    Exception? _error;
    bool _disposed;

    static VideoFileWriter() => MediaFactory.MFStartup();

    public VideoFileWriter(string path, PixelSize size, int frameRate, CanvasDevice device, bool hardware, bool withAudio)
    {
        _frameRate = frameRate;
        _device = new ID3D11Device(NativeMethods.DxgiInterface(device, typeof(ID3D11Device).GUID));
        _context = _device.ImmediateContext;
        // The encoder uses the device from its own threads while Win2D draws on it.
        using (var mt = _device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(3);
            attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Fmpeg4);
            if (hardware)
            {
                _manager = MediaFactory.MFCreateDXGIDeviceManager();
                _manager.ResetDevice(_device);
                attributes.Set(SinkWriterAttributeKeys.D3DManager, _manager);
                attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            }
            _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

            using (var output = VideoType(VideoFormatGuids.H264, size, frameRate))
            {
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)FrameMath.Bitrate(size, frameRate));
                _videoStream = _writer.AddStream(output);
            }
            using (var input = VideoType(VideoFormatGuids.Rgb32, size, frameRate))
            {
                input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(size.Width * 4));
                _writer.SetInputMediaType(_videoStream, input, null);
            }
            if (withAudio)
            {
                using (var output = AudioType(AudioFormatGuids.Aac))
                {
                    output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 192_000u / 8);
                    _audioStream = _writer.AddStream(output);
                }
                using var input = AudioType(AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AudioRate * 4u);
                _writer.SetInputMediaType(_audioStream, input, null);
            }
            _writer.BeginWriting();
            UsesHardwareEncoder = HardwareEncoderLoaded();
            // The software encoder reads every frame back to the CPU, so the caller must choose it on purpose.
            if (hardware && !UsesHardwareEncoder)
                throw new NotSupportedException("The hardware H.264 encoder did not load. Open the writer with hardware: false.");
        }
        catch (Exception e)
        {
            _writer?.Dispose();
            _manager?.Dispose();
            _context.Dispose();
            _device.Dispose();
            _queue.Dispose();
            if (_writer is not null) File.Delete(path);
            if (e is SharpGenException s) throw Translate(s);
            throw;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = nameof(VideoFileWriter) };
        _thread.Start();
    }

    public long FramesBehind => Interlocked.Read(ref _behind);

    public bool UsesHardwareEncoder { get; }

    // Hardware MFTs set MFT_ENUM_HARDWARE_URL_Attribute, and software ones do not.
    bool HardwareEncoderLoaded()
    {
        using var writerEx = _writer.QueryInterface<IMFSinkWriterEx>();
        for (int i = 0; ; i++)
        {
            Guid category;
            IMFTransform transform;
            try { writerEx.GetTransformForStream(_videoStream, i, out category, out transform); }
            catch (SharpGenException) { return false; }
            using (transform)
            {
                if (category != TransformCategoryGuids.VideoEncoder) continue;
                using var attributes = transform.Attributes;
                try { return attributes.GetAllocatedString(TransformAttributeKeys.MftEnumHardwareUrlAttribute).Length > 0; }
                catch (SharpGenException) { return false; }
            }
        }
    }

    // Copies the frame on the GPU, because the caller draws the next frame into the same render target.
    public void WriteFrame(IDirect3DSurface surface, long frameIndex)
    {
        ThrowIfFailed();
        using var source = new ID3D11Texture2D(NativeMethods.DxgiInterface(surface, typeof(ID3D11Texture2D).GUID));
        var desc = source.Description;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var copy = _device.CreateTexture2D(desc);
        _context.CopyResource(copy, source);
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, copy, 0, false);
        using (var buffer2D = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = buffer2D.ContiguousLength;
        Interlocked.Increment(ref _behind);
        Enqueue(_videoStream, buffer, frameIndex * TicksPerSecond / _frameRate, TicksPerSecond / _frameRate);
    }

    public void WriteAudio(ReadOnlySpan<float> interleavedStereo48k, long firstSampleIndex)
    {
        ThrowIfFailed();
        if (_audioStream < 0) throw new InvalidOperationException("This writer was opened without audio.");
        // The AAC encoder takes 16-bit PCM only.
        var pcm = new short[interleavedStereo48k.Length];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(interleavedStereo48k[i] * 32767f, -32768f, 32767f);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length * 2);
        buffer.Lock(out var data, out _, out _);
        Marshal.Copy(pcm, 0, data, pcm.Length);
        buffer.Unlock();
        buffer.CurrentLength = pcm.Length * 2;
        long frames = pcm.Length / 2;
        Enqueue(_audioStream, buffer, firstSampleIndex * TicksPerSecond / AudioRate, frames * TicksPerSecond / AudioRate);
    }

    void Enqueue(int stream, IMFMediaBuffer buffer, long time, long duration)
    {
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        _queue.Add((stream, sample));
    }

    void Run()
    {
        foreach (var (stream, sample) in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (_error is null) _writer.WriteSample(stream, sample);
            }
            catch (SharpGenException e) { _error = Translate(e); }
            finally
            {
                sample.Dispose();
                if (stream == _videoStream) Interlocked.Decrement(ref _behind);
            }
        }
    }

    void ThrowIfFailed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_error is { } e) ExceptionDispatchInfo.Throw(e);
    }

    // Win32 errors such as a full disk become IOException, so callers handle them like other file errors.
    static Exception Translate(SharpGenException e) =>
        (e.HResult & 0xFFFF0000) == 0x80070000 ? new IOException(e.Message, e.HResult) : e;

    static IMFMediaType VideoType(Guid subtype, PixelSize size, int frameRate)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.FrameSize, EncoderProbe.Pack(size.Width, size.Height));
        type.Set(MediaTypeAttributeKeys.FrameRate, EncoderProbe.Pack(frameRate, 1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, EncoderProbe.Pack(1, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        return type;
    }

    static IMFMediaType AudioType(Guid subtype)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioRate);
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        return type;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join();
        try
        {
            if (_error is null) _writer.Finalize();
        }
        // A writer closed before its first sample has nothing to finalize.
        catch (SharpGenException e) when (e.HResult != SinkNoSamplesProcessed) { _error = Translate(e); }
        catch (SharpGenException) { }
        finally
        {
            _writer.Dispose();
            _manager?.Dispose();
            _context.Dispose();
            _device.Dispose();
            _queue.Dispose();
        }
        if (_error is { } error) ExceptionDispatchInfo.Throw(error);
    }
}
