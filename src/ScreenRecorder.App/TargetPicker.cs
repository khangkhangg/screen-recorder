using ScreenRecorder.Capture;

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
