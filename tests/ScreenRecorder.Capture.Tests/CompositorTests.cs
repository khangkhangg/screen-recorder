using Microsoft.Graphics.Canvas;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;

namespace ScreenRecorder.Capture.Tests;

public sealed class CompositorTests : IDisposable
{
    static readonly Color Red = Color.FromArgb(255, 255, 0, 0), Green = Color.FromArgb(255, 0, 255, 0),
        Blue = Color.FromArgb(255, 0, 0, 255), Black = Color.FromArgb(255, 0, 0, 0), White = Color.FromArgb(255, 255, 255, 255);

    readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    readonly Compositor _c;
    readonly string _dir = Directory.CreateTempSubdirectory("compositor-tests").FullName;
    readonly CanvasRenderTarget _blue, _red;

    public CompositorTests()
    {
        _c = new Compositor(_device, new PixelSize(1920, 1080));
        _blue = Solid(1920, 1080, Blue);
        _red = Solid(1280, 720, Red);
    }

    public void Dispose()
    {
        _c.Dispose();
        _blue.Dispose();
        _red.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Circle_camera_covers_center_not_corner()
    {
        var layout = Layout.Default with { Camera = new(true, 0.1f, 0.1f, 0.25f, CameraShape.Circle) }; // rect 192,108,270,270
        var s = _c.DrawFinal(_blue, _red, layout);
        AssertColor(Red, Pixel(s, 327, 243));
        AssertColor(Blue, Pixel(s, 194, 110));
    }

    [Fact]
    public void Square_camera_covers_corner()
    {
        var layout = Layout.Default with { Camera = new(true, 0.1f, 0.1f, 0.25f, CameraShape.Square) };
        AssertColor(Red, Pixel(_c.DrawFinal(_blue, _red, layout), 194, 110));
    }

    [Fact]
    public void Hidden_camera_is_not_drawn()
    {
        var layout = Layout.Default with { Camera = new(false, 0.1f, 0.1f, 0.25f, CameraShape.Square) };
        AssertColor(Blue, Pixel(_c.DrawFinal(_blue, _red, layout), 327, 243));
    }

    [Fact]
    public void Null_camera_frame_is_not_drawn()
    {
        var layout = Layout.Default with { Camera = new(true, 0.1f, 0.1f, 0.25f, CameraShape.Square) };
        AssertColor(Blue, Pixel(_c.DrawFinal(_blue, null, layout), 327, 243));
    }

    [Fact]
    public void Camera_is_mirrored()
    {
        using var cam = Solid(1280, 720, Red);
        using (var ds = cam.CreateDrawingSession()) ds.FillRectangle(640, 0, 640, 720, Green);
        var layout = Layout.Default with { Camera = new(true, 0.1f, 0.1f, 0.25f, CameraShape.Square) };
        var s = _c.DrawFinal(_blue, cam, layout);
        AssertColor(Green, Pixel(s, 192 + 270 / 4, 243));
        AssertColor(Red, Pixel(s, 192 + 270 * 3 / 4, 243));
    }

    [Fact]
    public void DrawScreen_letterboxes_small_source()
    {
        using var green = Solid(500, 500, Green);
        var s = _c.DrawScreen(green);
        AssertColor(Black, Pixel(s, 10, 540));
        AssertColor(Green, Pixel(s, 960, 540));
    }

    [Fact]
    public void DrawScreen_has_no_overlays()
    {
        Assert.Null(_c.SetLogo(Png(400, 200)));
        AssertColor(Blue, Pixel(_c.DrawScreen(_blue), 1900, 1060));
    }

    [Fact]
    public void Logo_is_drawn_in_its_corner()
    {
        var path = Png(400, 200);
        Assert.Null(_c.SetLogo(path));
        var layout = Layout.Default with { Logo = new(true, Corner.BottomRight, 0.08f, path) };
        var r = Layout.LogoRect(layout.Logo, new PixelSize(1920, 1080), new PixelSize(400, 200));
        AssertColor(White, Pixel(_c.DrawFinal(_blue, null, layout), (int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)));
    }

    [Fact]
    public void Missing_logo_returns_message()
    {
        Assert.NotNull(_c.SetLogo(@"C:\nope.png"));
        var layout = Layout.Default with { Logo = new(true, Corner.BottomRight, 0.08f, null) };
        AssertColor(Blue, Pixel(_c.DrawFinal(_blue, null, layout), 1900, 1060));
    }

    [Fact]
    public void Corrupt_logo_returns_message()
    {
        var path = Path.Combine(_dir, "bad.png");
        File.WriteAllText(path, "hello");
        Assert.NotNull(_c.SetLogo(path));
    }

    [Fact]
    public void Oversized_logo_returns_message() => Assert.NotNull(_c.SetLogo(Png(9000, 100)));

    CanvasRenderTarget Solid(int w, int h, Color color)
    {
        var target = new CanvasRenderTarget(_device, w, h, 96);
        using (var ds = target.CreateDrawingSession()) ds.Clear(color);
        return target;
    }

    string Png(int w, int h)
    {
        var path = Path.Combine(_dir, $"logo-{w}x{h}.png");
        using var target = Solid(w, h, White);
        target.SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return path;
    }

    Color Pixel(IDirect3DSurface surface, int x, int y)
    {
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, surface);
        return bitmap.GetPixelColors(x, y, 1, 1)[0];
    }

    static void AssertColor(Color expected, Color actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= 2 && Math.Abs(expected.G - actual.G) <= 2 && Math.Abs(expected.B - actual.B) <= 2,
            $"Expected {expected}, got {actual}");
    }
}
