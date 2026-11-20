using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class LayoutTests
{
    static void Near(RectF e, RectF a)
    {
        Assert.Equal(e.X, a.X, 0.01);
        Assert.Equal(e.Y, a.Y, 0.01);
        Assert.Equal(e.Width, a.Width, 0.01);
        Assert.Equal(e.Height, a.Height, 0.01);
    }

    [Fact]
    public void CameraRect_uses_fractions() =>
        Near(new RectF(192, 108, 270, 270), Layout.CameraRect(new CameraItem(true, 0.1f, 0.1f, 0.25f, CameraShape.Circle), new PixelSize(1920, 1080)));

    [Fact]
    public void CameraRect_is_clamped_inside_frame()
    {
        var r = Layout.CameraRect(new CameraItem(true, 0.95f, 0.95f, 0.25f, CameraShape.Square), new PixelSize(1920, 1080));
        Assert.Equal(1920, r.X + r.Width, 0.01);
        Assert.Equal(1080, r.Y + r.Height, 0.01);
    }

    [Fact]
    public void LogoRect_bottom_right_keeps_proportions() =>
        Near(new RectF(1920 - 21.6f - 172.8f, 1080 - 21.6f - 86.4f, 172.8f, 86.4f),
            Layout.LogoRect(new LogoItem(true, Corner.BottomRight, 0.08f, "x.png"), new PixelSize(1920, 1080), new PixelSize(400, 200)));

    [Fact]
    public void CameraRect_larger_than_frame_does_not_throw() =>
        Layout.CameraRect(new CameraItem(true, 0.1f, 0.1f, 0.9f, CameraShape.Square), new PixelSize(400, 1080));

    [Fact]
    public void LogoRect_wider_than_frame_does_not_throw() =>
        Layout.LogoRect(new LogoItem(true, Corner.BottomRight, 0.08f, "x.png"), new PixelSize(200, 1080), new PixelSize(4000, 100));
}
