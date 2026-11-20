using System.Diagnostics.CodeAnalysis;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using ScreenRecorder.Core;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenRecorder.Capture;

public class CaptureUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// Frames from TryGetLatestFrame belong to this source. They stay valid until the next
// TryGetLatestFrame call or Dispose, so callers must not dispose or keep them.
public sealed class ScreenSource : IScreenSource
{
    // shortcut: assumes the Windows "SDR content brightness" is 200 nits and clips HDR highlights, read DISPLAYCONFIG_SDR_WHITE_LEVEL if HDR captures look too bright or dark.
    const float SdrWhiteNits = 200;
    const string RegionError = "The selected area is empty or outside the screen.";

    readonly CaptureTarget _target;
    readonly CanvasDevice _device;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly GraphicsCaptureItem _item;
    readonly Timer _watch;
    bool _closed;
    PixelSize _sourceSize;
    readonly bool _hdr;
    readonly Lock _lock = new();
    SizeInt32 _poolSize;
    CanvasBitmap? _pending, _current;
    bool _disposed;

    public PixelSize SourceSize { get { lock (_lock) return _sourceSize; } }
    public event Action? Closed;

    public Windows.Foundation.Rect? WindowBounds =>
        _target is WindowTarget w && NativeMethods.WindowRect(w.Hwnd) is { } r ? new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    public bool IsMinimized => _target is WindowTarget w && NativeMethods.IsIconic(w.Hwnd);

    ScreenSource(CaptureTarget target, CanvasDevice device, GraphicsCaptureItem item)
    {
        _target = target;
        _device = device;
        _item = item;
        _hdr = IsHdr(target switch
        {
            MonitorTarget m => m.Hmonitor,
            RegionTarget r => r.Hmonitor,
            WindowTarget w => NativeMethods.MonitorFromWindow(w.Hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST),
            _ => 0,
        });
        _poolSize = item.Size;
        _sourceSize = target is RegionTarget region ? new(region.Width, region.Height) : new(_poolSize.Width, _poolSize.Height);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, Format, 2, _poolSize);
        try
        {
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = true;
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                NativeMethods.HideBorder(_session);
            item.Closed += OnItemClosed;
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
            // item.Closed did not fire in tests when the window's process ended, so also check every 500 ms.
            _watch = new Timer(_ => { if (!NativeMethods.Exists(target)) RaiseClosed(); }, null, 500, 500);
        }
        catch
        {
            item.Closed -= OnItemClosed;
            _pool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _pool.Dispose();
            throw;
        }
    }

    void OnItemClosed(GraphicsCaptureItem sender, object args) => RaiseClosed();

    // Raises Closed at most once, and never after Dispose.
    void RaiseClosed()
    {
        lock (_lock)
        {
            if (_disposed || _closed) return;
            _closed = true;
        }
        Closed?.Invoke();
    }

    DirectXPixelFormat Format => _hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;

    public static ScreenSource Create(CaptureTarget target, CanvasDevice device)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new CaptureUnavailableException("Screen capture is not supported on this PC.");
        if (target is RegionTarget { X: < 0 } or RegionTarget { Y: < 0 } or RegionTarget { Width: <= 0 } or RegionTarget { Height: <= 0 })
            throw new CaptureUnavailableException(RegionError);
        GraphicsCaptureItem item;
        try { item = NativeMethods.CreateItem(target); }
        catch (Exception e)
        {
            throw new CaptureUnavailableException(target is WindowTarget ? "This window cannot be recorded." : "This screen cannot be recorded.", e);
        }
        if (target is RegionTarget r && (r.X >= item.Size.Width || r.Y >= item.Size.Height))
            throw new CaptureUnavailableException(RegionError);
        return new ScreenSource(target, device, item);
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

    // Runs on a thread-pool thread.
    void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        // WinRT swallows exceptions from this handler, so report them as Closed to stop the recording instead of freezing it.
        try { HandleFrame(pool); }
        catch (Exception) { RaiseClosed(); }
    }

    void HandleFrame(Direct3D11CaptureFramePool pool)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
            {
                // This frame still has the old size, so drop it and wait for one at the new size.
                _poolSize = frame.ContentSize;
                _pool.Recreate(_device, Format, 2, _poolSize);
                if (_target is not RegionTarget) _sourceSize = new(_poolSize.Width, _poolSize.Height);
                return;
            }
        }
        var copy = Copy(frame.Surface);
        lock (_lock)
        {
            if (_disposed) { copy.Dispose(); return; }
            _pending?.Dispose();
            _pending = copy;
        }
    }

    // GPU copy, because the pool reuses the frame's surface once the frame is disposed.
    CanvasBitmap Copy(IDirect3DSurface surface)
    {
        using var source = CanvasBitmap.CreateFromDirect3D11Surface(_device, surface);
        var (x, y, w, h) = _target is RegionTarget r ? (r.X, r.Y, r.Width, r.Height) : (0, 0, _poolSize.Width, _poolSize.Height);
        var result = new CanvasRenderTarget(_device, w, h, 96);
        int cw = Math.Min(w, (int)source.SizeInPixels.Width - x), ch = Math.Min(h, (int)source.SizeInPixels.Height - y);
        if (cw <= 0 || ch <= 0) return result;
        if (!_hdr)
        {
            result.CopyPixelsFromBitmap(source, 0, 0, x, y, cw, ch);
            return result;
        }
        // HDR frames are linear scRGB where 1.0 is 80 nits. Scale SDR white to 1.0, then encode as sRGB.
        var s = 80 / SdrWhiteNits;
        using var scale = new ColorMatrixEffect
        {
            Source = source,
            ColorMatrix = new Matrix5x4 { M11 = s, M22 = s, M33 = s, M44 = 1 },
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
        };
        using var toSrgb = new ColorManagementEffect
        {
            Source = scale,
            SourceColorProfile = new ColorManagementProfile(CanvasColorSpace.ScRgb),
            OutputColorProfile = new ColorManagementProfile(CanvasColorSpace.Srgb),
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
        };
        using (var ds = result.CreateDrawingSession())
            ds.DrawImage(toSrgb, 0, 0, new Windows.Foundation.Rect(x, y, cw, ch));
        return result;
    }

    static bool IsHdr(nint monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            using (adapter)
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    using (output)
                    using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                        if (output6 is not null && output6.Description1.Monitor == monitor)
                            return output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        return false;
    }

    // Reads back a 16 by 16 pixel grid only, so it is cheap. Use before recording starts, never per frame.
    public static bool LooksBlack(CanvasBitmap frame)
    {
        using var grid = new CanvasRenderTarget(frame.Device, 16, 16, 96);
        using (var ds = grid.CreateDrawingSession())
        {
            ds.Clear(default(Windows.UI.Color));
            ds.DrawImage(frame, new Windows.Foundation.Rect(0, 0, 16, 16), frame.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
        }
        return grid.GetPixelColors().All(c => c.R == 0 && c.G == 0 && c.B == 0);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _watch.Dispose();
        _item.Closed -= OnItemClosed;
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        lock (_lock)
        {
            _pending?.Dispose();
            _current?.Dispose();
            _pending = _current = null;
        }
    }
}
