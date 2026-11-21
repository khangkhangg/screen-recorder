using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App.Tests;

public class ToolbarTests
{
    [Fact]
    public void Segment_per_target()
    {
        Assert.Equal(0, BarWindow.SegmentOf(new MonitorTarget(1)));
        Assert.Equal(1, BarWindow.SegmentOf(new WindowTarget(1)));
        Assert.Equal(2, BarWindow.SegmentOf(new RegionTarget(1, 0, 0, 100, 100)));
    }

    [Theory]
    [InlineData(null, false, LogoClick.Pick)]
    [InlineData(null, true, LogoClick.Pick)]
    [InlineData("logo.png", false, LogoClick.Show)]
    [InlineData("logo.png", true, LogoClick.Hide)]
    public void Logo_toggle_per_state(string? path, bool visible, LogoClick expected) =>
        Assert.Equal(expected, BarWindow.LogoAction(path, visible));

    [Theory]
    [InlineData(true, "Screenshot saved and copied")]
    [InlineData(false, "Screenshot saved")]
    public void Confirmation_text(bool copied, string text) => Assert.Equal(text, BarWindow.ConfirmationText(copied));

    [Theory]
    [InlineData(true, "Camera, on")]
    [InlineData(false, "Camera, off")]
    public void Name_with_state(bool on, string name) => Assert.Equal(name, BarWindow.NameWithState("Camera", on));
}
