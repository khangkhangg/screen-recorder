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

    // The captured area in desktop pixels.
    public static Rect? DesktopRect(CaptureTarget target)
    {
        if (target is WindowTarget w) return WindowRect(w.Hwnd);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(target is RegionTarget r ? r.Hmonitor : ((MonitorTarget)target).Hmonitor, ref info)) return null;
        var m = info.Monitor;
        return target is RegionTarget g
            ? new Rect { Left = m.Left + g.X, Top = m.Top + g.Y, Right = m.Left + g.X + g.Width, Bottom = m.Top + g.Y + g.Height }
            : m;
    }

    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    // Pixels per DIP on the captured monitor.
    public static float DipScale(CaptureTarget target)
    {
        var monitor = target switch
        {
            MonitorTarget m => m.Hmonitor,
            RegionTarget r => r.Hmonitor,
            WindowTarget w => MonitorFromWindow(w.Hwnd, MONITOR_DEFAULTTONEAREST),
            _ => 0,
        };
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96f : 1;
    }

    public delegate nint HookProc(int code, nint message, nint info);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint info);
    [DllImport("user32.dll")] public static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint threadId, uint message, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public struct Msg { public nint Hwnd; public uint Message; public nint W, L; public uint Time; public Point Pt; public uint Private; }

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

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(ref Guid iid);
    }

    // Returns an owned pointer to the D3D object behind a WinRT device or surface.
    public static nint DxgiInterface(object winrt, Guid iid) =>
        WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(winrt).GetInterface(ref iid);

    public static void HideBorder(GraphicsCaptureSession session) =>
        WinRT.CastExtensions.As<IGraphicsCaptureSession3>(session).SetIsBorderRequired(false);
}
