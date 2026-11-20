using System.Runtime.InteropServices;

namespace ScreenRecorder.App;

static class NativeMethods
{
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, DWMWA_CLOAKED = 14;
    const uint MONITORINFOF_PRIMARY = 1, LWA_ALPHA = 2, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, MB_ICONWARNING = 0x30;

    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    delegate bool MonitorEnumProc(nint hmonitor, nint hdc, nint rect, nint lParam);

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(nint hmonitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint hwnd, char[] text, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    public static void ExcludeFromCapture(nint hwnd) => SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    // Monitor rectangles in physical pixels, primary first.
    public static List<(nint Hmonitor, Rect Bounds, bool Primary)> Monitors()
    {
        var list = new List<(nint, Rect, bool)>();
        EnumDisplayMonitors(0, 0, (h, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(h, ref info)) list.Add((h, info.Monitor, (info.Flags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, 0);
        return list.OrderByDescending(m => m.Item3).ToList();
    }

    // Top-level windows in z-order with what the window filter needs.
    public static List<(nint Hwnd, string Title, bool Visible, bool Minimized, bool Cloaked, bool Tool, bool Own)> TopLevelWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<(nint, string, bool, bool, bool, bool, bool)>();
        EnumWindows((h, _) =>
        {
            var text = new char[GetWindowTextLengthW(h) + 1];
            var title = new string(text, 0, GetWindowTextW(h, text, text.Length));
            DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int));
            GetWindowThreadProcessId(h, out var pid);
            list.Add((h, title, IsWindowVisible(h), IsIconic(h), cloaked != 0,
                (GetWindowLongPtrW(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0, pid == own));
            return true;
        }, 0);
        return list;
    }

    // Makes the whole window see-through, which is how the region picker dims the screen.
    public static void SetOpacity(nint hwnd, byte alpha)
    {
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    // Hands the drag to Windows, and returns when the user lets go.
    public static void DragWindow(nint hwnd)
    {
        ReleaseCapture();
        SendMessageW(hwnd, WM_NCLBUTTONDOWN, (nint)HTCAPTION, 0);
    }

    public static void ShowWarning(nint hwnd, string text, string caption) => MessageBoxW(hwnd, text, caption, MB_ICONWARNING);
}
