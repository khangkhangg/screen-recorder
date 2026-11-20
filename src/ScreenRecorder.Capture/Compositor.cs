using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ScreenRecorder.Core;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;

namespace ScreenRecorder.Capture;

// Returned surfaces stay valid until the next call of the same method, because each output reuses one render target.
public sealed class Compositor : IDisposable
{
    const long MaxLogoBytes = 20 * 1024 * 1024;
    const int MaxLogoSide = 8192;

    readonly CanvasDevice _device;
    readonly PixelSize _frameSize;
    readonly CanvasRenderTarget _screen, _final;
    readonly Lock _lock = new();
    CanvasBitmap? _logo;

    public Compositor(CanvasDevice device, PixelSize frameSize)
    {
        _device = device;
        _frameSize = frameSize;
        _screen = new CanvasRenderTarget(device, frameSize.Width, frameSize.Height, 96);
        _final = new CanvasRenderTarget(device, frameSize.Width, frameSize.Height, 96);
    }

    public IDirect3DSurface DrawScreen(CanvasBitmap screen)
    {
        using var ds = _screen.CreateDrawingSession();
        DrawLetterboxed(ds, screen);
        return _screen;
    }

    public IDirect3DSurface DrawFinal(CanvasBitmap screen, CanvasBitmap? camera, Layout layout)
    {
        using var ds = _final.CreateDrawingSession();
        DrawLetterboxed(ds, screen);
        lock (_lock)
        {
            if (layout.Logo.Visible && _logo is not null)
            {
                var r = Layout.LogoRect(layout.Logo, _frameSize, new PixelSize((int)_logo.SizeInPixels.Width, (int)_logo.SizeInPixels.Height));
                ds.DrawImage(_logo, new Rect(r.X, r.Y, r.Width, r.Height));
            }
        }
        if (layout.Camera.Visible && camera is not null) DrawCamera(ds, camera, Layout.CameraRect(layout.Camera, _frameSize), layout.Camera.Shape);
        return _final;
    }

    // Returns null on success, or a plain error message. A null path or any error clears the logo.
    public string? SetLogo(string? path)
    {
        CanvasBitmap? logo = null;
        string? error = null;
        if (path is not null)
        {
            try
            {
                if (!File.Exists(path)) error = "The logo file was not found.";
                else if (new FileInfo(path).Length > MaxLogoBytes) error = "The logo file is larger than 20 MB.";
                else
                {
                    logo = CanvasBitmap.LoadAsync(_device, path, 96).AsTask().GetAwaiter().GetResult();
                    if (logo.SizeInPixels.Width > MaxLogoSide || logo.SizeInPixels.Height > MaxLogoSide)
                    {
                        logo.Dispose();
                        logo = null;
                        error = "The logo is larger than 8192 pixels on a side.";
                    }
                }
            }
            catch (Exception) { error = "The logo file could not be read as an image."; }
        }
        lock (_lock)
        {
            _logo?.Dispose();
            _logo = logo;
        }
        return error;
    }

    void DrawLetterboxed(CanvasDrawingSession ds, CanvasBitmap screen)
    {
        ds.Clear(Color.FromArgb(255, 0, 0, 0));
        var r = FrameMath.Letterbox(new PixelSize((int)screen.SizeInPixels.Width, (int)screen.SizeInPixels.Height), _frameSize);
        ds.DrawImage(screen, new Rect(r.X, r.Y, r.Width, r.Height), screen.Bounds);
    }

    static void DrawCamera(CanvasDrawingSession ds, CanvasBitmap camera, RectF r, CameraShape shape)
    {
        // DrawImage reads source rectangles in DIPs.
        float w = (float)camera.Size.Width, h = (float)camera.Size.Height, side = Math.Min(w, h);
        var crop = new Rect((w - side) / 2, (h - side) / 2, side, side);
        using var mask = shape switch
        {
            CameraShape.Circle => CanvasGeometry.CreateEllipse(ds, r.X + r.Width / 2, r.Y + r.Height / 2, r.Width / 2, r.Height / 2),
            CameraShape.RoundedSquare => CanvasGeometry.CreateRoundedRectangle(ds, r.X, r.Y, r.Width, r.Height, r.Width * 0.18f, r.Height * 0.18f),
            _ => CanvasGeometry.CreateRectangle(ds, r.X, r.Y, r.Width, r.Height),
        };
        using (ds.CreateLayer(1f, mask))
        {
            ds.Transform = Matrix3x2.CreateScale(-1, 1, new Vector2(r.X + r.Width / 2, 0));
            ds.DrawImage(camera, new Rect(r.X, r.Y, r.Width, r.Height), crop);
            ds.Transform = Matrix3x2.Identity;
        }
    }

    public void Dispose()
    {
        _screen.Dispose();
        _final.Dispose();
        lock (_lock)
        {
            _logo?.Dispose();
            _logo = null;
        }
    }
}
