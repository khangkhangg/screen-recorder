using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App;

// Where the action bar under the selected region goes, when it shows, and what Cancel and Share leave behind.
public static class ActionBar
{
    public static readonly string ShareFolder = Path.Combine(Path.GetTempPath(), "ScreenRecorder", "share");

    // In screen pixels: centered 8 DIPs under the frame's 2 DIP bottom border, or 8 DIPs above it inside the
    // region when the monitor has no room below, and never off the monitor.
    public static PointInt32 Place(RectInt32 region, RectInt32 monitor, SizeInt32 size, double scale)
    {
        int Px(double dips) => (int)Math.Round(dips * scale);
        int bottom = region.Y + region.Height, monitorBottom = monitor.Y + monitor.Height;
        int y = bottom + Px(2) + Px(8);
        // the frame draws its border inside the region where the region touches the monitor's bottom
        if (y + size.Height > monitorBottom) y = Math.Min(bottom, monitorBottom - Px(2)) - Px(8) - size.Height;
        int x = region.X + (region.Width - size.Width) / 2;
        return new(Math.Max(monitor.X, Math.Min(x, monitor.X + monitor.Width - size.Width)), Math.Max(monitor.Y, y));
    }

    public static bool Shows(CaptureTarget target, RecorderState state, bool dragging, bool picking) =>
        target is RegionTarget && state == RecorderState.Idle && !dragging && !picking;

    public static (Settings, CaptureTarget) Cancel(Settings s) =>
        (s with { Region = null }, new MonitorTarget(CaptureTarget.PrimaryMonitor()));

    public static IEnumerable<string> Stale(IEnumerable<(string Path, DateTime Written)> files, DateTime now) =>
        files.Where(f => now - f.Written > TimeSpan.FromDays(1)).Select(f => f.Path);
}
