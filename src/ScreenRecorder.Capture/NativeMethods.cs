using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace ScreenRecorder.Capture;

static class NativeMethods
{
    public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public readonly record struct Point(int X, int Y);
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(Point pt, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    public static bool Exists(CaptureTarget target)
    {
        if (target is WindowTarget w) return IsWindow(w.Hwnd);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(target is RegionTarget r ? r.Hmonitor : ((MonitorTarget)target).Hmonitor, ref info);
    }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    // The visible window bounds, without the invisible resize border that GetWindowRect includes.
    public static Rect? WindowRect(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Rect>()) == 0 ? r : null;

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    // Windows 11 only, so the 19041 projection does not include it.
    // .NET cannot marshal IInspectable, so its 3 methods are placeholders that keep the vtable slots right.
    [ComImport, Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureSession3
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [return: MarshalAs(UnmanagedType.U1)] bool GetIsBorderRequired();
        void SetIsBorderRequired([MarshalAs(UnmanagedType.U1)] bool value);
    }

    public static GraphicsCaptureItem CreateItem(CaptureTarget target)
    {
        var interop = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        var ptr = target switch
        {
            WindowTarget w => interop.CreateForWindow(w.Hwnd, ref iid),
            MonitorTarget m => interop.CreateForMonitor(m.Hmonitor, ref iid),
            RegionTarget r => interop.CreateForMonitor(r.Hmonitor, ref iid),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public static void HideBorder(GraphicsCaptureSession session) =>
        WinRT.CastExtensions.As<IGraphicsCaptureSession3>(session).SetIsBorderRequired(false);
}
