using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class FrameMathTests
{
    [Fact] public void ToEven_rounds_down() =>
        Assert.Equal(new PixelSize(1920, 1080), FrameMath.ToEven(new PixelSize(1921, 1081)));

    [Fact] public void FitWithin_scales_ultrawide_down() =>
        Assert.Equal(new PixelSize(4096, 1728), FrameMath.FitWithin(new PixelSize(5120, 2160), new PixelSize(4096, 2304)));

    [Fact] public void FitWithin_exact_for_awkward_ratio() => Assert.Equal(new PixelSize(1720, 720), FrameMath.FitWithin(new PixelSize(3440, 1440), new PixelSize(1720, 2304)));

    [Fact] public void FitWithin_never_scales_up() =>
        Assert.Equal(new PixelSize(1280, 720), FrameMath.FitWithin(new PixelSize(1280, 720), new PixelSize(4096, 2304)));

    [Fact] public void Letterbox_adds_side_bars() =>
        Assert.Equal(new RectF(420, 0, 1080, 1080), FrameMath.Letterbox(new PixelSize(500, 500), new PixelSize(1920, 1080)));

    [Fact] public void Bitrate_1080p60() =>
        Assert.Equal(12_441_600, FrameMath.Bitrate(new PixelSize(1920, 1080), 60));
}
