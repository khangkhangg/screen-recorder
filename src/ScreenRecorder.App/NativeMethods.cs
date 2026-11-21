using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App;

static class NativeMethods
{
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
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
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowRect(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out PointInt32 point);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] public static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    public static bool ExcludeFromCapture(nint hwnd)
    {
#if DEBUG
        // lets a Debug build take pictures of its own windows; a Release build cannot be made visible
        if (Environment.GetEnvironmentVariable("SCREENRECORDER_SHOW_IN_CAPTURE") == "1") return true;
#endif
        return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

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

    public static RectF? MonitorRect(nint hmonitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfoW(hmonitor, ref info) ? ToRectF(info.Monitor) : null;
    }

    // The frame bounds without the invisible resize border, which is what window capture records.
    public static RectF? WindowRect(nint hwnd) =>
        IsWindow(hwnd) && !IsIconic(hwnd) && DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out Rect r, Marshal.SizeOf<Rect>()) == 0
            ? ToRectF(r) : null;

    static RectF ToRectF(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    // Borderless, on top, never focused, not in Alt+Tab, and hidden from capture.
    public static nint MakeOverlay(Window window, string name)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Title = name; // Task Manager lists every window, and the default title is "WinUI Desktop"
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        if (!ExcludeFromCapture(hwnd)) System.Diagnostics.Debug.WriteLine($"{name} could not be excluded from capture");
        return hwnd;
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

    // Needs comctl32 version 6, which app.manifest asks for.
    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)] static extern int TaskDialog(nint hwnd, nint instance, string title, string? instruction, string content, int buttons, nint icon, out int pressed);

    public static bool AskYesCancel(nint hwnd, string text, string caption)
    {
        const int TDCBF_YES_BUTTON = 2, TDCBF_CANCEL_BUTTON = 8, IDYES = 6;
        TaskDialog(hwnd, 0, caption, null, text, TDCBF_YES_BUTTON | TDCBF_CANCEL_BUTTON, 0xFFFF, out var pressed); // MAKEINTRESOURCEW(-1), TD_WARNING_ICON
        return pressed == IDYES;
    }

    public delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
}
