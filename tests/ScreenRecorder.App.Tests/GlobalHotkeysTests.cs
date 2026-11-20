using System.Runtime.InteropServices;
using ScreenRecorder.App;
using ScreenRecorder.Core;

namespace ScreenRecorder.App.Tests;

public class GlobalHotkeysTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateWindowExW(int exStyle, string cls, string? name, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);

    [Fact]
    public void Same_hotkey_twice_fails()
    {
        var hwnd = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, -3, 0, 0, 0); // -3 is HWND_MESSAGE
        try
        {
            var key = new Hotkey(true, true, true, false, 0x78); // Ctrl+Shift+Alt+F9, unlikely to be taken
            using var hotkeys = new GlobalHotkeys(hwnd);
            Assert.True(hotkeys.Register(key, () => { }));
            Assert.False(hotkeys.Register(key, () => { }));
        }
        finally { DestroyWindow(hwnd); }
    }
}
