using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class RecordingClockTests
{
    TimeSpan t = TimeSpan.Zero;
    RecordingClock Make(int fps = 60) => new(fps, () => t);
    void Advance(double s) => t += TimeSpan.FromSeconds(s);

    [Fact]
    public void Frames_follow_time()
    {
        var c = Make(); c.Start(); Advance(1.5);
        Assert.Equal(90, c.FramesDue);
    }

    [Fact]
    public void Pause_cuts_time()
    {
        var c = Make(); c.Start(); Advance(2); c.Pause(); Advance(3); c.Resume(); Advance(1);
        Assert.Equal(TimeSpan.FromSeconds(3), c.MediaTime);
        Assert.Equal(new[] { new PauseEntry(2.0, 3.0) }, c.Pauses);
    }

    [Fact]
    public void Frames_do_not_grow_while_paused()
    {
        var c = Make(); c.Start(); Advance(1); c.Pause(); Advance(5);
        Assert.Equal(60, c.FramesDue);
        Assert.Empty(c.Pauses);
    }

    [Fact]
    public void Samples_follow_time()
    {
        var c = Make(); c.Start(); Advance(0.5);
        Assert.Equal(24_000, c.SamplesDue(48_000));
    }

    [Fact]
    public void Pause_twice_is_ignored()
    {
        var c = Make(); c.Start(); Advance(1); c.Pause(); Advance(1); c.Pause(); Advance(1); c.Resume();
        Assert.Single(c.Pauses);
        Assert.Equal(2.0, c.Pauses[0].DurationSeconds);
    }

    [Fact]
    public void Pause_before_start_and_resume_when_running_do_nothing()
    {
        var c = Make(); c.Pause(); Assert.False(c.IsPaused);
        c.Start(); Advance(1); c.Resume();
        Assert.Equal(TimeSpan.FromSeconds(1), c.MediaTime);
        Assert.Empty(c.Pauses);
    }
}
