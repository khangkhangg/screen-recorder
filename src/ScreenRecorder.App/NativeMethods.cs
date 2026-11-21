using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App;

static class NativeMethods
{
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
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
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
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

    delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    static WinEventProc? popupHook; // the hook holds no reference of its own

    // Tooltips, menus and flyouts are top-level windows of class Microsoft.UI.Content.PopupWindowSiteBridge,
    // so every top-level window this process creates or shows is hidden from capture too. Call once on the UI thread.
    public static void ExcludePopupsFromCapture()
    {
        popupHook = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject == 0 && idChild == 0 && GetAncestor(hwnd, 2) == hwnd) ExcludeFromCapture(hwnd); // OBJID_WINDOW, GA_ROOT
        };
        SetWinEventHook(0x8000, 0x8002, 0, popupHook, (uint)Environment.ProcessId, 0, 0); // EVENT_OBJECT_CREATE to EVENT_OBJECT_SHOW, out of context
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
    [DllImport("gdi32.dll")] static extern int CombineRgn(nint dest, nint a, nint b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint hmonitor, int type, out uint x, out uint y);

    // Shapes a window to these rectangles in window pixels, so clicks and pixels outside them are not the window's.
    public static void SetRegion(nint hwnd, IEnumerable<RectInt32> rects) => SetRoundRegion(hwnd, rects.Select(r => (r, 0)));

    // Round is the diameter of the rounded corners, 0 for square ones.
    public static void SetRoundRegion(nint hwnd, IEnumerable<(RectInt32 Rect, int Round)> parts)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var (r, round) in parts)
        {
            var part = round > 0
                ? CreateRoundRectRgn(r.X, r.Y, r.X + r.Width + 1, r.Y + r.Height + 1, round, round)
                : CreateRectRgn(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
            CombineRgn(region, region, part, 2); // RGN_OR
            DeleteObject(part);
        }
        SetWindowRgn(hwnd, region, true); // the window owns the region now
    }

    // A borderless WinUI window keeps a frame about 3 pixels wide around the client area where XAML draws. This puts
    // the client area on the given screen pixels and shapes the window to the parts, which are in screen pixels too.
    public static void PlaceOverlay(AppWindow window, nint hwnd, RectInt32 client, IEnumerable<(RectInt32 Rect, int Round)> parts)
    {
        int Frame() => (window.Size.Width - window.ClientSize.Width) / 2;
        void Move(int f) => window.MoveAndResize(new RectInt32(client.X - f, client.Y - f, client.Width + 2 * f, client.Height + 2 * f));
        var f = Frame();
        Move(f);
        // a window that was never placed can report no frame yet
        if (Frame() != f) Move(f = Frame());
        SetRoundRegion(hwnd, parts.Select(p => (new RectInt32(p.Rect.X - client.X + f, p.Rect.Y - client.Y + f, p.Rect.Width, p.Rect.Height), p.Round)));
    }

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);

    public static double MonitorScale(nint hmonitor) => GetDpiForMonitor(hmonitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;

    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    // Back on top of the topmost windows, without moving, sizing or activating it.
    public static void BringToTop(nint hwnd) => SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x13); // HWND_TOPMOST, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

    // Layered and transparent to the mouse, so clicks pass to whatever is under the window.
    public static void ClickThrough(nint hwnd)
    {
        SetOpacity(hwnd, 255);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
    }

    public static void SetOpacity(nint hwnd, byte alpha)
    {
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }

    // Slots this code does not call are listed by name only, to keep the vtable order.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(); void SetProgressDialog(); void SetProperties(); void SetOwnerWindow();
        void ApplyPropertiesToItem(); void ApplyPropertiesToItems(); void RenameItem(); void RenameItems();
        void MoveItem(); void MoveItems(); void CopyItem(); void CopyItems();
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems(); void NewItem();
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint newName);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint newName, int hr, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint folder, nint newName);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint folder, nint newName, int hr, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint folder, nint newName);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint folder, nint newName, int hr, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int hr, nint created);
        [PreserveSig] int PreNewItem(uint flags, nint folder, nint newName);
        [PreserveSig] int PostNewItem(uint flags, nint folder, nint newName, nint template, uint attributes, int hr, nint item);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    // Records whether the shell put the item in the Recycle Bin.
    [ComVisible(true)]
    sealed class DeleteSink : IFileOperationProgressSink
    {
        public bool Recycled;
        public int PostDeleteItem(uint flags, nint item, int hr, nint created) { Recycled = hr >= 0 && created != 0; return 0; } // created is the item in the bin
        public int PreDeleteItem(uint flags, nint item) => 0;
        public int StartOperations() => 0;
        public int FinishOperations(int hr) => 0;
        public int PreRenameItem(uint flags, nint item, nint newName) => 0;
        public int PostRenameItem(uint flags, nint item, nint newName, int hr, nint created) => 0;
        public int PreMoveItem(uint flags, nint item, nint folder, nint newName) => 0;
        public int PostMoveItem(uint flags, nint item, nint folder, nint newName, int hr, nint created) => 0;
        public int PreCopyItem(uint flags, nint item, nint folder, nint newName) => 0;
        public int PostCopyItem(uint flags, nint item, nint folder, nint newName, int hr, nint created) => 0;
        public int PreNewItem(uint flags, nint folder, nint newName) => 0;
        public int PostNewItem(uint flags, nint folder, nint newName, nint template, uint attributes, int hr, nint item) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern IShellItem SHCreateItemFromParsingName(string path, nint bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool CreateHardLinkW(string link, string existing, nint security);

    // Moves the file to the Recycle Bin, or leaves it where it is and returns false, with no dialog.
    // The shell deletes a file for good when it cannot recycle it, even with the recycle flags, so a second
    // name (a hard link) holds the data until the file is in the bin, and puts it back otherwise.
    // Needs a single-threaded apartment, which the UI thread is.
    public static bool Recycle(string path)
    {
        var keep = $"{path}.{Guid.NewGuid():N}.keep";
        if (!CreateHardLinkW(keep, path, 0)) return false; // FAT drives and some shares, which have no Recycle Bin either
        bool recycled = false;
        try { recycled = ShellRecycle(path); }
        finally
        {
            try
            {
                if (File.Exists(path)) File.Delete(keep); // untouched: drop only the second name
                else if (recycled) File.Delete(keep); // the data stays in the bin under the other name
                else File.Move(keep, path); // deleted for good: put it back
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { recycled = false; } // the data is still under one of the names
        }
        return recycled && !File.Exists(path);
    }

    static bool ShellRecycle(string path)
    {
        const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOFX_RECYCLEONDELETE = 0x80000;
        try
        {
            var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!)!;
            try
            {
                op.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI | FOFX_RECYCLEONDELETE);
                var sink = new DeleteSink();
                op.DeleteItem(SHCreateItemFromParsingName(path, 0, typeof(IShellItem).GUID), sink);
                op.PerformOperations();
                return sink.Recycled && !op.GetAnyOperationsAborted();
            }
            finally { Marshal.ReleaseComObject(op); }
        }
        catch (Exception e) // COM errors arrive as many exception types
        {
            System.Diagnostics.Debug.WriteLine(e);
            return false;
        }
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
