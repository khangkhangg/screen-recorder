using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App;

public static class TargetPicker
{
    public static IReadOnlyList<(nint Hmonitor, string Name)> Monitors() =>
        NativeMethods.Monitors().Select((m, i) =>
            (m.Hmonitor, $"Display {i + 1} ({m.Bounds.Right - m.Bounds.Left} x {m.Bounds.Bottom - m.Bounds.Top})")).ToList();

    public static IReadOnlyList<(nint Hwnd, string Title)> Windows() =>
        NativeMethods.TopLevelWindows()
            .Where(w => IsPickable(w.Title, w.Visible, w.Minimized, w.Cloaked, w.Tool, w.Own))
            .Select(w => (w.Hwnd, w.Title)).ToList();

    public static bool IsPickable(string title, bool visible, bool minimized, bool cloaked, bool toolWindow, bool ownProcess) =>
        visible && !minimized && !cloaked && !toolWindow && !ownProcess && !string.IsNullOrWhiteSpace(title);

    // Capture area in physical screen pixels, or null when its monitor or window is gone.
    public static RectF? AreaOnScreen(CaptureTarget target, Func<nint, RectF?> monitorRect, Func<nint, RectF?> windowRect) => target switch
    {
        MonitorTarget m => monitorRect(m.Hmonitor),
        RegionTarget r => monitorRect(r.Hmonitor) is { } b ? new RectF(b.X + r.X, b.Y + r.Y, r.Width, r.Height) : null,
        WindowTarget w => windowRect(w.Hwnd),
        _ => null,
    };

    // Two drag points in window units (DIPs) on a window that covers the whole monitor.
    public static RegionTarget? ToRegion(nint hmonitor, double x1, double y1, double x2, double y2,
        double scale, int monitorWidth, int monitorHeight)
    {
        int Px(double v, int max) => Math.Clamp((int)Math.Round(v * scale), 0, max);
        int left = Px(Math.Min(x1, x2), monitorWidth), right = Px(Math.Max(x1, x2), monitorWidth);
        int top = Px(Math.Min(y1, y2), monitorHeight), bottom = Px(Math.Max(y1, y2), monitorHeight);
        return right - left < 16 || bottom - top < 16 ? null : new RegionTarget(hmonitor, left, top, right - left, bottom - top);
    }

    // Top, bottom, left and right border strips just outside a region on screen, clamped into its monitor,
    // and the label's top-left corner: above the border, or inside the region when there is no room above.
    public static (RectInt32[] Sides, PointInt32 Label) Outline(RectInt32 region, RectInt32 monitor, int thickness, int labelHeight)
    {
        int left = Math.Max(region.X - thickness, monitor.X), top = Math.Max(region.Y - thickness, monitor.Y);
        int right = Math.Min(region.X + region.Width + thickness, monitor.X + monitor.Width);
        int bottom = Math.Min(region.Y + region.Height + thickness, monitor.Y + monitor.Height);
        RectInt32[] sides = [new(left, top, right - left, thickness), new(left, bottom - thickness, right - left, thickness),
            new(left, top, thickness, bottom - top), new(right - thickness, top, thickness, bottom - top)];
        return (sides, top - labelHeight >= monitor.Y ? new(left, top - labelHeight) : new(left + thickness, top + thickness));
    }
}
