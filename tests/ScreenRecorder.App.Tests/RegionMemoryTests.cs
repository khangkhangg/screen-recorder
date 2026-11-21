using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App.Tests;

public class RegionMemoryTests
{
    static readonly (nint, string, int, int)[] Monitors = [(1, @"\\.\DISPLAY1", 3840, 2160), (2, @"\\.\DISPLAY2", 1920, 1080)];
    static readonly SavedRegion OnSecond = new(@"\\.\DISPLAY2", 100, 50, 1280, 720);

    [Fact]
    public void A_saved_region_comes_back_on_its_monitor() =>
        Assert.Equal(new RegionTarget(2, 100, 50, 1280, 720), TargetPicker.Restore(OnSecond, Monitors));

    [Fact]
    public void A_region_on_a_monitor_that_is_gone_is_ignored() =>
        Assert.Null(TargetPicker.Restore(OnSecond with { Monitor = @"\\.\DISPLAY3" }, Monitors));

    [Theory]
    [InlineData(700, 50, 1280, 720)]   // past the right edge
    [InlineData(100, 400, 1280, 720)]  // past the bottom edge
    [InlineData(-1, 50, 1280, 720)]
    [InlineData(100, 50, 10, 720)]     // smaller than the picker allows
    public void A_region_that_no_longer_fits_is_ignored(int x, int y, int w, int h) =>
        Assert.Null(TargetPicker.Restore(new SavedRegion(@"\\.\DISPLAY2", x, y, w, h), Monitors));

    [Fact]
    public void No_saved_region_opens_the_picker() =>
        Assert.Null(TargetPicker.RegionClick(new MonitorTarget(1), null, Monitors));

    [Fact]
    public void A_saved_region_is_selected_at_once_from_another_source() =>
        Assert.Equal(new RegionTarget(2, 100, 50, 1280, 720), TargetPicker.RegionClick(new MonitorTarget(1), OnSecond, Monitors));

    [Fact]
    public void Clicking_region_again_opens_the_picker() =>
        Assert.Null(TargetPicker.RegionClick(new RegionTarget(2, 100, 50, 1280, 720), OnSecond, Monitors));
}
