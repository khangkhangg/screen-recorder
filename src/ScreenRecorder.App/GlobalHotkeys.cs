using ScreenRecorder.Core;

namespace ScreenRecorder.App;

// Hotkeys that work while another app has focus. WM_HOTKEY arrives through a subclass of the given window.
public sealed class GlobalHotkeys : IDisposable
{
    const uint WM_HOTKEY = 0x0312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    readonly nint _hwnd;
    readonly NativeMethods.SubclassProc _proc; // the field keeps the delegate alive while Windows holds it
    readonly Dictionary<int, Action> _actions = [];

    public GlobalHotkeys(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        NativeMethods.SetWindowSubclass(hwnd, _proc, 1, 0);
    }

    public bool Register(Hotkey h, Action onPressed)
    {
        var mods = MOD_NOREPEAT | (h.Ctrl ? MOD_CONTROL : 0) | (h.Shift ? MOD_SHIFT : 0) | (h.Alt ? MOD_ALT : 0) | (h.Win ? MOD_WIN : 0);
        var id = _actions.Count + 1;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, mods, h.VirtualKey)) return false;
        _actions[id] = onPressed;
        return true;
    }

    nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            action();
            return 0;
        }
        return NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        NativeMethods.RemoveWindowSubclass(_hwnd, _proc, 1);
    }
}
