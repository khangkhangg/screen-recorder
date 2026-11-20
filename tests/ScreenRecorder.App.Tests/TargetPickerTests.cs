using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App.Tests;

public class TargetPickerTests
{
    [Theory]
    [InlineData("Notepad", true, false, false, false, false, true)]
    [InlineData("Notepad", false, false, false, false, false, false)] // hidden
    [InlineData("Notepad", true, true, false, false, false, false)]   // minimized
    [InlineData("Notepad", true, false, true, false, false, false)]   // cloaked
    [InlineData("Notepad", true, false, false, true, false, false)]   // tool window
    [InlineData("Notepad", true, false, false, false, true, false)]   // this app
    [InlineData("  ", true, false, false, false, false, false)]       // no title
    public void Window_filter(string title, bool visible, bool minimized, bool cloaked, bool tool, bool own, bool expected) =>
        Assert.Equal(expected, TargetPicker.IsPickable(title, visible, minimized, cloaked, tool, own));

    [Fact]
    public void Region_is_scaled_normalized_and_clamped()
    {
        // dragged up-left at 150% scale, past the right edge of a 1920 x 1080 monitor
        Assert.Equal(new RegionTarget(7, 150, 300, 1770, 150), TargetPicker.ToRegion(7, 1400, 300, 100, 200, 1.5, 1920, 1080));
    }

    [Theory]
    [InlineData(10, 10, 25, 100)] // 15 pixels wide
    [InlineData(10, 10, 100, 25)] // 15 pixels high
    public void Region_under_16_pixels_is_null(double x1, double y1, double x2, double y2) =>
        Assert.Null(TargetPicker.ToRegion(1, x1, y1, x2, y2, 1, 1920, 1080));

    [Fact]
    public void Area_on_screen_per_target()
    {
        RectF? Monitor(nint h) => h == 1 ? new RectF(-1920, 0, 1920, 1080) : null;
        RectF? Window(nint h) => h == 5 ? new RectF(10, 20, 300, 200) : null;
        Assert.Equal(new RectF(-1920, 0, 1920, 1080), TargetPicker.AreaOnScreen(new MonitorTarget(1), Monitor, Window));
        Assert.Equal(new RectF(-1820, 50, 640, 480), TargetPicker.AreaOnScreen(new RegionTarget(1, 100, 50, 640, 480), Monitor, Window));
        Assert.Equal(new RectF(10, 20, 300, 200), TargetPicker.AreaOnScreen(new WindowTarget(5), Monitor, Window));
        Assert.Null(TargetPicker.AreaOnScreen(new WindowTarget(6), Monitor, Window));
        Assert.Null(TargetPicker.AreaOnScreen(new RegionTarget(2, 0, 0, 16, 16), Monitor, Window));
    }

    [Fact]
    public void Region_of_exactly_16_pixels_is_kept() =>
        Assert.Equal(new RegionTarget(1, 0, 0, 16, 16), TargetPicker.ToRegion(1, 0, 0, 16, 16, 1, 1920, 1080));
}
