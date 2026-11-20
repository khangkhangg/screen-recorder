using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class OverlayMathTests
{
    static readonly RectF Area = new(100, 50, 960, 540);
    static readonly CameraItem Current = new(true, 0.5f, 0.5f, 0.25f, CameraShape.Square);

    [Fact]
    public void ToScreen_offsets_and_scales() =>
        Assert.Equal(new RectF(100 + 96, 50 + 54, 135, 135),
            OverlayMath.ToScreen(new RectF(192, 108, 270, 270), Area, new PixelSize(1920, 1080)));

    [Fact]
    public void FromScreen_round_trips()
    {
        var c = OverlayMath.FromScreen(new RectF(100 + 96, 50 + 54, 135, 135), Area, Current);
        Assert.Equal(0.1f, c.X, 0.001f);
        Assert.Equal(0.1f, c.Y, 0.001f);
        Assert.Equal(0.25f, c.Size, 0.001f);
        Assert.Equal(Current with { X = c.X, Y = c.Y, Size = c.Size }, c);
    }

    [Fact]
    public void FromScreen_clamps_inside_area()
    {
        var c = OverlayMath.FromScreen(new RectF(1000, -40, 135, 135), Area, Current);
        Assert.True(c.X + c.Size * 540 / 960 <= 1.0001f);
        Assert.Equal(0, c.Y);
        Assert.Equal(1 - 0.25f * 540 / 960, c.X, 0.001f);
    }

    [Theory]
    [InlineData(5, 0.1f)]
    [InlineData(1000, 0.6f)]
    public void FromScreen_limits_size(float side, float expected) =>
        Assert.Equal(expected, OverlayMath.FromScreen(new RectF(100, 50, side, side), Area, Current).Size, 0.001f);
}
