using ScreenRecorder.App;
using Windows.Graphics;

namespace ScreenRecorder.App.Tests;

public class ScreenshotCardTests
{
    [Fact]
    public void Card_sits_at_the_bottom_right_of_the_work_area()
    {
        var work = new RectInt32(0, 0, 3840, 2088);
        Assert.Equal(new RectInt32(3346, 1657, 450, 387), CardMath.CardRect(work, 450, 387, 44));
    }

    [Fact]
    public void Card_follows_a_work_area_that_does_not_start_at_zero()
    {
        var work = new RectInt32(-1920, 40, 1920, 1040);
        Assert.Equal(new RectInt32(-316, 764, 300, 300), CardMath.CardRect(work, 300, 300, 16));
    }

    [Theory]
    [InlineData(true, "Saved and copied · 1280 x 720")]
    [InlineData(false, "Saved · 1280 x 720")]
    public void Status_text(bool copied, string text) => Assert.Equal(text, CardMath.StatusText(copied, 1280, 720));

    [Theory]
    [InlineData(13, 0, "Recording saved · 0:13")]
    [InlineData(13, 42, "Recording saved · 0:13 · 42 frames dropped")]
    [InlineData(3725, 0, "Recording saved · 1:02:05")]
    public void Recording_status(int seconds, long dropped, string text) =>
        Assert.Equal(text, CardMath.RecordingStatus(TimeSpan.FromSeconds(seconds), dropped));

    [Theory]
    [InlineData(1920, 1080, 266.67, 150)]
    [InlineData(800, 200, 280, 70)]
    [InlineData(100, 50, 100, 50)]
    [InlineData(560, 150, 280, 75)]
    public void Thumbnail_fits_the_box_and_is_never_enlarged(double w, double h, double fitW, double fitH)
    {
        var (width, height) = CardMath.Fit(w, h, 280, 150);
        Assert.Equal(fitW, width, 2);
        Assert.Equal(fitH, height, 2);
    }

    [Theory]
    [InlineData(true, false, 0, true)]          // the card is activated
    [InlineData(false, true, 200, false)]       // the panel takes focus as it opens
    [InlineData(false, true, 5000, true)]       // the card loses focus later
    [InlineData(false, false, 5000, false)]     // activated without focus
    public void Share_pause_ends(bool active, bool deactivated, long ms, bool ends) =>
        Assert.Equal(ends, CardMath.SharePauseEnds(active, deactivated, ms));

    [Fact]
    public void Cropped_name_without_a_clash()
    {
        Assert.Equal("Shot (cropped).png", CardMath.CroppedName(["Shot.png"], "Shot.png"));
    }

    [Fact]
    public void Cropped_name_counts_up_past_taken_names()
    {
        string[] names = ["Shot.png", "shot (cropped).png", "Shot (cropped) (2).png"];
        Assert.Equal("Shot (cropped) (3).png", CardMath.CroppedName(names, "Shot.png"));
    }
}
