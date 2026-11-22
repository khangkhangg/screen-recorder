using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class ClickMathTests
{
    const uint A = ClickMath.Amber, B = ClickMath.Blue;

    static ClickShape[] Shapes(ClickEffect e, double age, ClickButton b = ClickButton.Left)
    {
        var into = new ClickShape[2];
        return into[..ClickMath.Shapes(e, b, age, into)];
    }

    static void Same(ClickShape expected, ClickShape actual)
    {
        Assert.Equal(expected.Radius, actual.Radius, 0.01f);
        Assert.Equal(expected.Stroke, actual.Stroke);
        Assert.Equal(expected.Opacity, actual.Opacity, 0.01f);
        Assert.Equal(expected.Color, actual.Color);
    }

    // The middle of an ease-out is 1 - 0.5^2 = 0.75 of the way.
    [Theory]
    [InlineData(ClickEffect.Ring, 0, 3, 3, 1)]
    [InlineData(ClickEffect.Ring, 200, 21.75, 3, 0.25)]
    [InlineData(ClickEffect.Ripple, 0, 3, 3, 1)]
    [InlineData(ClickEffect.Ripple, 225, 21.75, 3, 0.25)]
    [InlineData(ClickEffect.Dot, 0, 0, 0, 0.75)]
    [InlineData(ClickEffect.Dot, 60, 7.5, 0, 0.75)]
    [InlineData(ClickEffect.Dot, 120, 15, 0, 0.75)]
    [InlineData(ClickEffect.Dot, 260, 16.5, 0, 0.375)]
    [InlineData(ClickEffect.Halo, 0, 22, 0, 0.25)]
    [InlineData(ClickEffect.Halo, 120, 22, 0, 0.6)]
    [InlineData(ClickEffect.Halo, 210, 22, 0, 0.425)]
    public void First_shape_follows_the_timeline(ClickEffect e, double age, float radius, float stroke, float opacity) =>
        Same(new(radius, stroke, opacity, A), Shapes(e, age)[0]);

    [Theory]
    [InlineData(ClickEffect.Ring, 400)]
    [InlineData(ClickEffect.Ripple, 590)]
    [InlineData(ClickEffect.Dot, 400)]
    [InlineData(ClickEffect.Off, 0)]
    public void Nothing_after_the_end(ClickEffect e, double age) => Assert.Empty(Shapes(e, age));

    [Fact]
    public void Ripple_second_ring_starts_140_later_and_is_thinner()
    {
        Assert.Single(Shapes(ClickEffect.Ripple, 139));
        var s = Shapes(ClickEffect.Ripple, 365);
        Same(new(21.75f, 2, 0.25f, A), s[1]);
        Same(new(3, 2, 1, A), Shapes(ClickEffect.Ripple, 140)[1]);
        Assert.Equal(2, Shapes(ClickEffect.Ripple, 450).Single().Stroke);
        Assert.Single(Shapes(ClickEffect.Ripple, 589));
    }

    [Fact]
    public void Halo_rests_amber_after_300()
    {
        Same(new(22, 0, 0.6f, B), Shapes(ClickEffect.Halo, 120, ClickButton.Right)[0]);
        Same(new(22, 0, 0.25f, A), Shapes(ClickEffect.Halo, 300, ClickButton.Right)[0]);
    }

    [Theory]
    [InlineData(ClickButton.Left, A)]
    [InlineData(ClickButton.Right, B)]
    [InlineData(ClickButton.Middle, B)]
    public void Color_follows_the_button(ClickButton b, uint color) => Assert.Equal(color, Shapes(ClickEffect.Ring, 0, b)[0].Color);
}
