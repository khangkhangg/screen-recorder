using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;

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

    // Region in the middle: the 2 pixel border sits just outside it, the label above its top-left corner.
    [Fact]
    public void Outline_sits_outside_the_region()
    {
        var (sides, label) = TargetPicker.Outline(new RectInt32(100, 200, 1280, 720), new RectInt32(0, 0, 1920, 1080), 2, 30);
        Assert.Equal([new(98, 198, 1284, 2), new(98, 920, 1284, 2), new(98, 198, 2, 724), new(1380, 198, 2, 724)], sides);
        Assert.Equal(new PointInt32(98, 168), label);
    }

    // Region in the top-left corner: the border runs just inside the screen edges, the label goes inside.
    [Fact]
    public void Outline_at_top_left_screen_edge_stays_on_screen()
    {
        var (sides, label) = TargetPicker.Outline(new RectInt32(0, 0, 1280, 720), new RectInt32(0, 0, 1920, 1080), 2, 30);
        Assert.Equal([new(0, 0, 1282, 2), new(0, 720, 1282, 2), new(0, 0, 2, 722), new(1280, 0, 2, 722)], sides);
        Assert.Equal(new PointInt32(2, 2), label);
    }

    // Region in the bottom-right corner of a monitor left of the primary one.
    [Fact]
    public void Outline_at_bottom_right_screen_edge_stays_on_screen()
    {
        var (sides, label) = TargetPicker.Outline(new RectInt32(-640, 360, 640, 720), new RectInt32(-1920, 0, 1920, 1080), 2, 30);
        Assert.Equal([new(-642, 358, 642, 2), new(-642, 1078, 642, 2), new(-642, 358, 2, 722), new(-2, 358, 2, 722)], sides);
        Assert.Equal(new PointInt32(-642, 328), label);
    }

    [Fact]
    public void Outline_label_goes_inside_when_there_is_no_room_above()
    {
        var (_, label) = TargetPicker.Outline(new RectInt32(100, 10, 200, 200), new RectInt32(0, 0, 1920, 1080), 2, 30);
        Assert.Equal(new PointInt32(100, 10), label);
    }
}
