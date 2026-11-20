namespace ScreenRecorder.Capture;

public abstract record CaptureTarget
{
    public static nint PrimaryMonitor() => NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
}

public record MonitorTarget(nint Hmonitor) : CaptureTarget;

public record WindowTarget(nint Hwnd) : CaptureTarget;

// X and Y are in that monitor's pixels, relative to its top-left corner.
public record RegionTarget(nint Hmonitor, int X, int Y, int Width, int Height) : CaptureTarget;
