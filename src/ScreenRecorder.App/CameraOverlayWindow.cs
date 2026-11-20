using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;
using Windows.UI;

namespace ScreenRecorder.App;

// Live camera over the capture area, drawn like the recorder draws it, so the user places it before recording.
public sealed class CameraOverlayWindow : Window
{
    readonly nint hwnd;
    readonly CanvasControl canvas = new() { ClearColor = Color.FromArgb(255, 96, 96, 96), CustomDevice = CanvasDevice.GetSharedDevice() };
    readonly DispatcherQueueTimer redraw;
    RectF area;
    CameraItem camera = Layout.Default.Camera;
    bool shown, opening, pressed, dragging, resizing;
    WebcamSource? webcam;
    PointInt32 grab;
    RectF grabRect;

    public event Action<CameraItem>? Changed;

    // null means the first camera
    public string? WebcamId
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (!shown || opening) return;
            CloseCamera();
            OpenCamera();
        }
    }

    public CameraOverlayWindow()
    {
        Content = canvas;
        hwnd = NativeMethods.MakeOverlay(this, "Camera overlay");
        canvas.Draw += Draw;
        canvas.PointerPressed += Pressed;
        canvas.PointerMoved += Moved;
        canvas.PointerReleased += (_, _) => Release();
        canvas.PointerCaptureLost += (_, _) => Release();
        canvas.PointerWheelChanged += Wheel;
        redraw = DispatcherQueue.CreateTimer();
        redraw.Interval = TimeSpan.FromMilliseconds(33);
        redraw.Tick += (_, _) => canvas.Invalidate();
        // shown turns false so a camera that finishes opening after close is disposed
        Closed += (_, _) => { shown = false; CloseCamera(); };
    }

    PixelSize Frame => new((int)area.Width, (int)area.Height);
    RectF OnScreen => OverlayMath.ToScreen(Layout.CameraRect(camera, Frame), area, Frame);

    public void Show(RectF captureAreaOnScreen, Layout layout)
    {
        area = captureAreaOnScreen;
        camera = layout.Camera;
        Place();
        if (shown) return;
        shown = true;
        AppWindow.Show(false);
        if (webcam is null && !opening) OpenCamera();
    }

    public void Hide()
    {
        shown = pressed = false;
        AppWindow.Hide();
        CloseCamera();
    }

    public void MoveCaptureArea(RectF captureAreaOnScreen)
    {
        area = captureAreaOnScreen;
        if (shown) Place();
    }

    void Place()
    {
        var r = OnScreen;
        int side = (int)Math.Round(r.Width);
        AppWindow.MoveAndResize(new RectInt32((int)Math.Round(r.X), (int)Math.Round(r.Y), side, side));
        // corner diameter is 36% of the side, so the radius matches the compositor's 18%
        int corner = (int)(side * 0.36f);
        NativeMethods.SetWindowRgn(hwnd, camera.Shape switch
        {
            CameraShape.Circle => NativeMethods.CreateEllipticRgn(0, 0, side + 1, side + 1),
            CameraShape.RoundedSquare => NativeMethods.CreateRoundRectRgn(0, 0, side + 1, side + 1, corner, corner),
            _ => NativeMethods.CreateRectRgn(0, 0, side, side),
        }, true);
        canvas.Invalidate();
    }

    async void OpenCamera()
    {
        opening = true;
        try
        {
            var id = WebcamId ?? (await WebcamSource.ListAsync()).FirstOrDefault().Id;
            if (id is null) return;
            var source = await WebcamSource.OpenAsync(id, CanvasDevice.GetSharedDevice(), sharedReadOnly: true);
            if (!shown) { await source.DisposeAsync(); return; }
            webcam = source;
            redraw.Start();
            source.Lost += () => DispatcherQueue.TryEnqueue(() => { if (webcam == source) CloseCamera(); });
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } // the gray placeholder stays, so the user can still place it
        finally { opening = false; }
    }

    async void CloseCamera()
    {
        var source = webcam;
        webcam = null;
        redraw.Stop();
        canvas.Invalidate();
        if (source is not null) await source.DisposeAsync();
    }

    void Draw(CanvasControl sender, CanvasDrawEventArgs e)
    {
        var r = new RectF(0, 0, (float)sender.ActualWidth, (float)sender.ActualHeight);
        if (webcam is not null && webcam.TryGetLatestFrame(out var frame))
        {
            Capture.Compositor.DrawCamera(e.DrawingSession, frame, r, camera.Shape);
            return;
        }
        using var icon = new CanvasTextFormat
        {
            FontFamily = "Segoe Fluent Icons", FontSize = Math.Max(1, r.Width / 4),
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        e.DrawingSession.DrawText("", new Windows.Foundation.Rect(0, 0, r.Width, r.Height), Colors.White, icon);
    }

    // Distance from the overlay center in the shape's own measure, so an edge grab follows the pointer 1 to 1.
    float FromCenter(RectF r, PointInt32 p)
    {
        float dx = p.X - (r.X + r.Width / 2), dy = p.Y - (r.Y + r.Height / 2);
        return camera.Shape == CameraShape.Circle ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(Math.Abs(dx), Math.Abs(dy));
    }

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        NativeMethods.GetCursorPos(out grab);
        grabRect = OnScreen;
        resizing = FromCenter(grabRect, grab) > grabRect.Width / 2 - Math.Max(8, grabRect.Width * 0.12f);
        pressed = true;
        dragging = false;
        canvas.CapturePointer(e.Pointer);
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        NativeMethods.GetCursorPos(out var now);
        int dx = now.X - grab.X, dy = now.Y - grab.Y;
        if (!dragging && dx * dx + dy * dy < 16) return;
        dragging = true;
        var r = grabRect;
        if (resizing)
        {
            float side = r.Width + 2 * (FromCenter(r, now) - FromCenter(r, grab));
            r = new(r.X + (r.Width - side) / 2, r.Y + (r.Height - side) / 2, side, side);
        }
        else r = r with { X = r.X + dx, Y = r.Y + dy };
        camera = OverlayMath.FromScreen(r, area, camera);
        Place();
    }

    void Release()
    {
        if (!pressed) return;
        pressed = false;
        canvas.ReleasePointerCaptures();
        if (dragging) Changed?.Invoke(camera);
    }

    void Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (pressed) return; // the drag rebuilds from its grab rect, so a wheel change would be lost
        var r = OnScreen;
        float side = r.Width * MathF.Pow(1.1f, e.GetCurrentPoint(canvas).Properties.MouseWheelDelta / 120f);
        camera = OverlayMath.FromScreen(new RectF(r.X + (r.Width - side) / 2, r.Y + (r.Height - side) / 2, side, side), area, camera);
        Place();
        Changed?.Invoke(camera);
    }
}
