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
    [InlineData(true, "Camera, on")]
    [InlineData(false, "Camera, off")]
    public void Name_with_state(bool on, string name) => Assert.Equal(name, BarWindow.NameWithState("Camera", on));

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 2, false)]
    [InlineData(6, 0, true)]
    [InlineData(-4, -3, true)]
    public void A_press_becomes_a_drag_after_6_pixels(double dx, double dy, bool drag) =>
        Assert.Equal(drag, BarWindow.IsDrag(dx, dy));
}
