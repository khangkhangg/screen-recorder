using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+R", 0x52u)]
    [InlineData("ctrl + alt + 5", 0x35u)]
    [InlineData("Win+F12", 0x7Bu)]
    public void Parses(string text, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(key, h.VirtualKey);
    }

    [Fact]
    public void Round_trips()
    {
        Assert.True(Hotkey.TryParse("Win+Alt+Shift+Ctrl+F1", out var h));
        Assert.Equal("Ctrl+Shift+Alt+Win+F1", h.ToString());
    }

    [Theory]
    [InlineData("R")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+RR")]
    public void Rejects(string text) => Assert.False(Hotkey.TryParse(text, out _));
}
