using ScreenRecorder.Capture;
using ScreenRecorder.Core;

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
}
