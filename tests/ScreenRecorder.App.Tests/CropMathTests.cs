using ScreenRecorder.App;
using Windows.Foundation;
using Windows.Graphics;

namespace ScreenRecorder.App.Tests;

public class CropMathTests
{
    static readonly RectInt32 Start = new(100, 100, 200, 100);

    [Fact]
    public void Move_keeps_the_size_and_converts_view_pixels()
    {
        Assert.Equal(new RectInt32(120, 90, 200, 100), CropMath.Drag(Start, Grip.Move, 10, -5, 0.5, 1000, 500));
    }

    [Fact]
    public void Move_stays_inside_the_picture()
    {
        Assert.Equal(new RectInt32(800, 0, 200, 100), CropMath.Drag(Start, Grip.Move, 5000, -5000, 1, 1000, 500));
    }

    [Fact]
    public void Corner_resize_moves_two_edges()
    {
        Assert.Equal(new RectInt32(100, 100, 250, 130), CropMath.Drag(Start, Grip.Right | Grip.Bottom, 50, 30, 1, 1000, 500));
    }

    [Fact]
    public void Resize_never_goes_under_16_pixels()
    {
        Assert.Equal(new RectInt32(284, 100, 16, 100), CropMath.Drag(Start, Grip.Left, 500, 0, 1, 1000, 500));
        Assert.Equal(new RectInt32(100, 100, 200, 16), CropMath.Drag(Start, Grip.Bottom, 0, -500, 1, 1000, 500));
    }

    [Fact]
    public void Resize_stays_inside_the_picture()
    {
        Assert.Equal(new RectInt32(0, 0, 300, 200), CropMath.Drag(Start, Grip.Left | Grip.Top, -500, -500, 1, 1000, 500));
    }

    [Fact]
    public void Tiny_picture_does_not_throw()
    {
        Assert.Equal(new RectInt32(0, 0, 10, 10), CropMath.Drag(new RectInt32(0, 0, 10, 10), Grip.Right, -5, 0, 1, 10, 10));
    }

    [Theory]
    [InlineData(100, 100, Grip.Left | Grip.Top)]
    [InlineData(89, 111, Grip.Left | Grip.Top)]
    [InlineData(300, 200, Grip.Right | Grip.Bottom)]
    [InlineData(200, 95, Grip.Top)]
    [InlineData(310, 150, Grip.Right)]
    [InlineData(200, 150, Grip.Move)]
    [InlineData(200, 50, Grip.None)]
    [InlineData(87, 150, Grip.None)]
    public void Hit_test_uses_24_pixel_grab_areas(double x, double y, Grip grip)
    {
        Assert.Equal(grip, CropMath.HitTest(new Rect(100, 100, 200, 100), new Point(x, y)));
    }

    [Fact]
    public void Narrow_rectangle_picks_the_nearer_edge()
    {
        var rect = new Rect(100, 100, 10, 100);
        Assert.Equal(Grip.Left, CropMath.HitTest(rect, new Point(104, 150)));
        Assert.Equal(Grip.Right, CropMath.HitTest(rect, new Point(106, 150)));
    }
}
