using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App.Tests;

public class ActionBarTests
{
    static readonly RectInt32 Monitor = new(0, 0, 3840, 2160);
    static readonly SizeInt32 Size = new(600, 66);

    // At 150% the 2 DIP border is 3 pixels and the 8 DIP gap is 12.
    [Fact]
    public void Sits_centered_8_dips_under_the_bottom_border()
    {
        Assert.Equal(new PointInt32(1340, 1335), ActionBar.Place(new(1000, 600, 1280, 720), Monitor, Size, 1.5));
    }

    [Fact]
    public void Goes_inside_the_region_when_there_is_no_room_below()
    {
        Assert.Equal(new PointInt32(1340, 2079), ActionBar.Place(new(1000, 1400, 1280, 760), Monitor, Size, 1.5));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3740, 3240)]
    public void Is_clamped_at_the_left_and_right_edges(int regionX, int barX)
    {
        Assert.Equal(barX, ActionBar.Place(new(regionX, 600, 100, 300), Monitor, Size, 1.5).X);
    }

    [Fact]
    public void Is_wider_than_a_narrow_region_and_still_centered()
    {
        Assert.Equal(new PointInt32(750, 915), ActionBar.Place(new(1000, 600, 100, 300), Monitor, Size, 1.5));
    }

    [Fact]
    public void Stays_on_a_monitor_at_negative_coordinates()
    {
        Assert.Equal(new PointInt32(-1920, 310), ActionBar.Place(new(-1920, 100, 200, 200), new(-1920, 0, 1920, 1080), new(400, 44), 1));
    }

    [Theory]
    [InlineData("region", RecorderState.Idle, false, false, true)]
    [InlineData("desktop", RecorderState.Idle, false, false, false)]
    [InlineData("window", RecorderState.Idle, false, false, false)]
    [InlineData("region", RecorderState.Countdown, false, false, false)]
    [InlineData("region", RecorderState.Recording, false, false, false)]
    [InlineData("region", RecorderState.Paused, false, false, false)]
    [InlineData("region", RecorderState.Idle, true, false, false)]
    [InlineData("region", RecorderState.Idle, false, true, false)]
    public void Shows_only_for_an_idle_region_that_is_not_being_changed(string kind, RecorderState state, bool dragging, bool picking, bool shows)
    {
        CaptureTarget target = kind switch
        {
            "region" => new RegionTarget(1, 0, 0, 100, 100),
            "window" => new WindowTarget(1),
            _ => new MonitorTarget(1),
        };
        Assert.Equal(shows, ActionBar.Shows(target, state, dragging, picking));
    }

    [Fact]
    public void Cancel_forgets_the_region_and_goes_back_to_the_desktop()
    {
        var before = Settings.Default with { Region = new SavedRegion(@"\\.\DISPLAY1", 10, 20, 300, 200) };
        var (after, target) = ActionBar.Cancel(before);
        Assert.Equal(before with { Region = null }, after);
        Assert.Equal(new MonitorTarget(CaptureTarget.PrimaryMonitor()), target);
    }

    [Fact]
    public void Shared_pictures_older_than_a_day_are_deleted()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0);
        (string, DateTime)[] files = [("new.png", now.AddHours(-23)), ("old.png", now.AddHours(-25)), ("older.png", now.AddDays(-30))];
        Assert.Equal(["old.png", "older.png"], ActionBar.Stale(files, now));
    }
}
