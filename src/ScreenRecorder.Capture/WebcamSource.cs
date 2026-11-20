using System.Diagnostics.CodeAnalysis;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace ScreenRecorder.Capture;

public sealed class WebcamSource : IAsyncDisposable
{
    // Frames from TryGetLatestFrame belong to this source. They stay valid until the next
    // TryGetLatestFrame call or Dispose, so callers must not dispose or keep them.
    readonly MediaCapture _capture;
    readonly MediaFrameReader _reader;
    readonly CanvasDevice _device;
    readonly Lock _lock = new();
    CanvasBitmap? _pending, _current;
    bool _disposed;
    int _lost;

    public PixelSize Size { get; }

    public event Action? Lost;

    WebcamSource(MediaCapture capture, MediaFrameReader reader, CanvasDevice device, PixelSize size)
    {
        _capture = capture;
        _reader = reader;
        _device = device;
        Size = size;
        _capture.Failed += OnFailed;
        _reader.FrameArrived += OnFrameArrived;
    }

    void OnFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) => RaiseLost();

    public static async Task<IReadOnlyList<(string Id, string Name)>> ListAsync() =>
        (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).Select(d => (d.Id, d.Name)).ToList();

    public static async Task<WebcamSource> OpenAsync(string deviceId, CanvasDevice device)
    {
        var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.VideoCapture).CurrentStatus;
        if (access is DeviceAccessStatus.DeniedByUser or DeviceAccessStatus.DeniedBySystem)
            throw new InvalidOperationException("Camera access is turned off in the Windows privacy settings.");
        var capture = new MediaCapture();
        WebcamSource? webcam = null;
        try
        {
            // CPU memory, because the camera pipeline uses its own graphics device.
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });
            var colors = capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
            var source = colors.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord) ?? colors.FirstOrDefault()
                ?? throw new InvalidOperationException("This camera has no color video stream.");
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat is not null && f.FrameRate.Denominator != 0)
                .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - 1280) + Math.Abs((int)f.VideoFormat.Height - 720))
                .ThenBy(f => Math.Abs((double)f.FrameRate.Numerator / f.FrameRate.Denominator - 30))
                .FirstOrDefault() ?? throw new InvalidOperationException("This camera has no usable video mode.");
            await source.SetFormatAsync(format);
            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            webcam = new WebcamSource(capture, reader, device, new PixelSize((int)format.VideoFormat.Width, (int)format.VideoFormat.Height));
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException("The camera could not start. Another app may be using it.");
            return webcam;
        }
        catch (Exception e)
        {
            // Every failure releases the camera, because exclusive mode keeps it locked until disposed.
            if (webcam is not null) await webcam.DisposeAsync();
            else capture.Dispose();
            if (e is InvalidOperationException) throw;
            if (e is UnauthorizedAccessException)
                throw new InvalidOperationException("Camera access is turned off in the Windows privacy settings.", e);
            throw new InvalidOperationException($"Windows could not open the camera (0x{e.HResult:X8}).", e);
        }
    }

    public bool TryGetLatestFrame([MaybeNullWhen(false)] out CanvasBitmap frame)
    {
        lock (_lock)
        {
            if (_pending is not null)
            {
                _current?.Dispose();
                _current = _pending;
                _pending = null;
            }
            frame = _current;
            return frame is not null;
        }
    }

    void OnFrameArrived(MediaFrameReader reader, MediaFrameArrivedEventArgs _)
    {
        try
        {
            using var frame = reader.TryAcquireLatestFrame();
            using var software = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (software is null) return;
            var ready = software.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && software.BitmapAlphaMode != BitmapAlphaMode.Straight;
            using var converted = ready ? null : SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var bitmap = CanvasBitmap.CreateFromSoftwareBitmap(_device, converted ?? software);
            lock (_lock)
            {
                if (_disposed)
                {
                    bitmap.Dispose();
                    return;
                }
                _pending?.Dispose();
                _pending = bitmap;
            }
        }
        catch (Exception) { RaiseLost(); }
    }

    // Raised on the thread pool so a handler can dispose the source.
    void RaiseLost()
    {
        lock (_lock) if (_disposed) return;
        if (Interlocked.Exchange(ref _lost, 1) == 0) ThreadPool.QueueUserWorkItem(_ => Lost?.Invoke());
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Dispose();
            _current?.Dispose();
            _pending = _current = null;
        }
        _reader.FrameArrived -= OnFrameArrived;
        _capture.Failed -= OnFailed;
        try { await _reader.StopAsync(); } catch (Exception) { /* camera may be gone */ }
        _reader.Dispose();
        _capture.Dispose();
    }
}
