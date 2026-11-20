using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace ScreenRecorder.Capture;

public sealed class AudioSource : IAsyncDisposable
{
    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMemoryBufferByteAccess { void GetBuffer(out IntPtr buffer, out uint capacity); }

    const int Rate = 48000, Capacity = 2 * Rate * 2; // 2 seconds of stereo floats

    readonly Action _close;
    AudioFrameOutputNode? _out;
    string _deviceId = "";
    readonly float[] _ring = new float[Capacity];
    readonly object _lock = new();
    int _head, _count, _lost;

    public event Action? Lost;

    AudioSource(Action close) => _close = close;

    AudioSource(AudioGraph graph, AudioFrameOutputNode output, DeviceWatcher watcher, string deviceId)
    {
        _out = output;
        _deviceId = deviceId;
        _close = () => { watcher.Stop(); graph.Stop(); graph.Dispose(); };
        graph.QuantumStarted += (_, _) => Pull();
        graph.UnrecoverableErrorOccurred += (_, _) => RaiseLost();
        watcher.Removed += (_, info) => { if (info.Id == _deviceId) RaiseLost(); };
        watcher.Start();
        graph.Start();
    }

    public static async Task<IReadOnlyList<(string Id, string Name)>> ListMicsAsync() =>
        (await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector()))
            .Select(d => (d.Id, d.Name)).ToList();

    public static async Task<AudioSource> OpenMicAsync(string? deviceId)
    {
        var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture).CurrentStatus;
        if (access is DeviceAccessStatus.DeniedByUser or DeviceAccessStatus.DeniedBySystem)
            throw new InvalidOperationException("Microphone access is turned off in the Windows privacy settings.");
        deviceId = string.IsNullOrEmpty(deviceId) ? MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default) : deviceId;
        if (string.IsNullOrEmpty(deviceId))
            throw new InvalidOperationException("No microphone found on this PC.");
        return await OpenAsync(deviceId, MediaCategory.Other, DeviceClass.AudioCapture);
    }

    // The default output device's sound, through WASAPI loopback (AudioGraph cannot capture a render device).
    public static Task<AudioSource> OpenLoopbackAsync() => Task.Run(() =>
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var fmt = Marshal.AllocHGlobal(18);
        try
        {
            enumerator.GetDefaultAudioEndpoint(0, 0, out device); // eRender, eConsole
            var iid = typeof(IAudioClient).GUID;
            device.Activate(ref iid, 1, IntPtr.Zero, out var o);
            client = (IAudioClient)o;
            // WAVEFORMATEX: IEEE float, 2 channels, 48000 Hz, 8 bytes per frame, 32 bits.
            Marshal.WriteInt16(fmt, 0, 3); Marshal.WriteInt16(fmt, 2, 2); Marshal.WriteInt32(fmt, 4, Rate);
            Marshal.WriteInt32(fmt, 8, Rate * 8); Marshal.WriteInt16(fmt, 12, 8); Marshal.WriteInt16(fmt, 14, 32); Marshal.WriteInt16(fmt, 16, 0);
            // LOOPBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, shared mode, 100 ms buffer
            client.Initialize(0, unchecked((int)(0x00020000 | 0x80000000 | 0x08000000)), 1_000_000, 0, fmt, IntPtr.Zero);
            var cid = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref cid, out var c);
            capture = (IAudioCaptureClient)c;
            client.Start();
        }
        catch (Exception e)
        {
            Release(capture, client, device, enumerator);
            throw new InvalidOperationException($"Windows could not capture the computer's sound (0x{e.HResult:X8}).", e);
        }
        finally { Marshal.FreeHGlobal(fmt); }

        AudioSource? src = null;
        var stop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    capture.GetNextPacketSize(out var packet);
                    while (packet > 0)
                    {
                        capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                        var samples = new float[(int)frames * 2];
                        if ((flags & 1) == 0) Marshal.Copy(data, samples, 0, samples.Length); // 1 = SILENT: leave zeros
                        capture.ReleaseBuffer(frames);
                        src!.Add(samples);
                        capture.GetNextPacketSize(out packet);
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception) { src!.RaiseLost(); } // includes AUDCLNT_E_DEVICE_INVALIDATED
        }) { IsBackground = true, Name = "Loopback capture" };
        src = new AudioSource(() =>
        {
            stop.Cancel();
            thread.Join();
            try { client.Stop(); } catch (Exception) { /* device may be gone */ }
            Release(capture, client, device, enumerator);
        });
        thread.Start();
        return src;
    });

    static void Release(params object?[] coms)
    {
        foreach (var c in coms) if (c != null) Marshal.ReleaseComObject(c);
    }

    static async Task<AudioSource> OpenAsync(string deviceId, MediaCategory category, DeviceClass deviceClass)
    {
        var format = AudioEncodingProperties.CreatePcm(Rate, 2, 32);
        format.Subtype = MediaEncodingSubtypes.Float;
        var made = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media) { EncodingProperties = format });
        if (made.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"Windows could not start the audio engine ({made.Status}).");
        var graph = made.Graph;
        try
        {
            var device = (await DeviceInformation.FindAllAsync(deviceClass == DeviceClass.AudioRender ? MediaDevice.GetAudioRenderSelector() : MediaDevice.GetAudioCaptureSelector())).First(d => d.Id == deviceId);
            var input = await graph.CreateDeviceInputNodeAsync(category, graph.EncodingProperties, device);
            if (input.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException(input.Status == AudioDeviceNodeCreationStatus.AccessDenied
                    ? "Microphone access is turned off in the Windows privacy settings."
                    : $"Windows could not open the sound device ({input.Status}).");
            var output = graph.CreateFrameOutputNode(format);
            input.DeviceInputNode.AddOutgoingConnection(output);
            return new AudioSource(graph, output, DeviceInformation.CreateWatcher(deviceClass), deviceId);
        }
        catch { graph.Dispose(); throw; }
    }

    void Pull()
    {
        using var frame = _out!.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        ((IMemoryBufferByteAccess)(object)reference).GetBuffer(out var ptr, out _);
        var samples = new float[(int)buffer.Length / 4];
        Marshal.Copy(ptr, samples, 0, samples.Length);
        Add(samples);
    }

    void Add(float[] samples)
    {
        lock (_lock)
        {
            var src = samples.AsSpan();
            if (src.Length > Capacity) src = src[^Capacity..];
            var tail = (_head + _count) % Capacity;
            var first = Math.Min(src.Length, Capacity - tail);
            src[..first].CopyTo(_ring.AsSpan(tail));
            src[first..].CopyTo(_ring);
            _count += src.Length;
            if (_count > Capacity) { _head = (_head + _count - Capacity) % Capacity; _count = Capacity; }
        }
    }

    // Returns a count of floats, always even so left and right stay paired.
    public int Read(Span<float> interleavedStereo48k)
    {
        lock (_lock)
        {
            var n = Math.Min(interleavedStereo48k.Length, _count) & ~1;
            var first = Math.Min(n, Capacity - _head);
            _ring.AsSpan(_head, first).CopyTo(interleavedStereo48k);
            _ring.AsSpan(0, n - first).CopyTo(interleavedStereo48k[first..]);
            _head = (_head + n) % Capacity;
            _count -= n;
            return n;
        }
    }

    void RaiseLost()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0) Lost?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        _close();
        return ValueTask.CompletedTask;
    }
}
