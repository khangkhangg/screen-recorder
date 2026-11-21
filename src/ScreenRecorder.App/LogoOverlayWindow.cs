using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App;

// The logo in its corner of the capture area, where the recorder draws it.
public sealed class LogoOverlayWindow : Window
{
    // shortcut: transparent logo pixels show the canvas background, replace when the overlay can be per-pixel transparent
    readonly CanvasControl canvas = new() { CustomDevice = CanvasDevice.GetSharedDevice() };
    readonly nint hwnd;
    RectF area;
    LogoItem logo = Layout.Default.Logo;
    string? loadedPath;
    CanvasBitmap? bitmap;
    bool shown, loading;

    public LogoOverlayWindow()
    {
        Content = canvas;
        hwnd = NativeMethods.MakeOverlay(this, "Logo overlay");
        canvas.Draw += (s, e) => { if (bitmap is not null) e.DrawingSession.DrawImage(bitmap, new Windows.Foundation.Rect(0, 0, s.ActualWidth, s.ActualHeight)); };
    }

    PixelSize Frame => new((int)area.Width, (int)area.Height);

    public async void Show(RectF captureAreaOnScreen, Layout layout)
    {
        area = captureAreaOnScreen;
        logo = layout.Logo;
        shown = true;
        if (logo.Path != loadedPath)
        {
            var path = loadedPath = logo.Path;
            bitmap?.Dispose();
            bitmap = null;
            loading = true;
            CanvasBitmap? loaded = null;
            try { loaded = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path); }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } // a logo that cannot be decoded is not shown
            if (loadedPath != path) { loaded?.Dispose(); return; }
            bitmap = loaded;
            loading = false;
        }
        else if (loading) return; // the load in flight places the window with the newest area and layout
        if (!shown) return;
        if (bitmap is null) { Hide(); return; }
        Place();
        AppWindow.Show(false);
    }

    public void Hide()
    {
        shown = false;
        AppWindow.Hide();
    }

    public void MoveCaptureArea(RectF captureAreaOnScreen)
    {
        area = captureAreaOnScreen;
        if (shown && bitmap is not null) Place();
    }

    void Place()
    {
        var size = new PixelSize((int)bitmap!.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
        var r = OverlayMath.ToScreen(Layout.LogoRect(logo, Frame, size), area, Frame);
        var rect = new RectInt32((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        NativeMethods.PlaceOverlay(AppWindow, hwnd, rect, [(rect, 0)]);
        canvas.Invalidate();
    }
}
