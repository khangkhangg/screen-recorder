using System.Runtime.InteropServices;
using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App.Tests;

public class RecordingErrorTests
{
    public static TheoryData<Exception, bool> Cases => new()
    {
        { new CaptureUnavailableException("x"), true },
        { new IOException(), true },
        { new UnauthorizedAccessException(), true },
        { new InvalidOperationException(), true },
        { new NotSupportedException(), true },
        { new COMException("Media Foundation failed", unchecked((int)0xC00D36B4)), true },
        { new NullReferenceException(), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Shown_errors(Exception e, bool shown) => Assert.Equal(shown, BarWindow.IsRecordingError(e));

    [Theory]
    [InlineData("webcam lost at 3.0 s", true)]
    [InlineData("mic lost at 1.0 s", true)]
    [InlineData("system sound lost at 1.0 s", true)]
    [InlineData("disk error while saving: full", true)]
    [InlineData("The logo file was not found.", true)]
    [InlineData("encoder fell behind", false)]
    [InlineData("screen.mp4 scaled to 1920x1080", false)]
    public void Stop_notes_shown(string note, bool shown) => Assert.Equal(shown, BarWindow.IsShownNote(note));

    [Fact]
    public void Black_first_frame_blocks_only_windows()
    {
        Assert.True(BarWindow.BlockedByBlack(new WindowTarget(1), true));
        Assert.False(BarWindow.BlockedByBlack(new WindowTarget(1), false));
        Assert.False(BarWindow.BlockedByBlack(new MonitorTarget(1), true));
        Assert.False(BarWindow.BlockedByBlack(new RegionTarget(1, 0, 0, 100, 100), true));
    }

    [Theory]
    [InlineData(0x52, true, true, false, false, "Ctrl+Shift+R")]
    [InlineData(0x7B, false, false, true, true, "Alt+Win+F12")]
    [InlineData(0x35, true, false, false, false, "Ctrl+5")]
    public void Hotkey_text_parses_back(int vk, bool ctrl, bool shift, bool alt, bool win, string expected)
    {
        var text = SettingsWindow.HotkeyText(vk, ctrl, shift, alt, win);
        Assert.Equal(expected, text);
        Assert.True(Hotkey.TryParse(text!, out var h));
        Assert.Equal(new Hotkey(ctrl, shift, alt, win, (uint)vk), h);
    }

    [Fact]
    public void Hotkey_text_without_modifier_is_refused() =>
        Assert.False(Hotkey.TryParse(SettingsWindow.HotkeyText(0x52, false, false, false, false)!, out _));

    [Theory]
    [InlineData(0x10)] // Shift alone
    [InlineData(0x09)] // Tab
    public void Other_keys_give_no_text(int vk) => Assert.Null(SettingsWindow.HotkeyText(vk, true, false, false, false));
}
