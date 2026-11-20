namespace ScreenRecorder.Core;

// Maps between frame pixels and screen pixels, so the overlays sit where the recorder draws.
public static class OverlayMath
{
    const float MinSize = 0.1f, MaxSize = 0.6f;

    public static RectF ToScreen(RectF rectInFrame, RectF captureAreaOnScreen, PixelSize frame)
    {
        float sx = captureAreaOnScreen.Width / frame.Width, sy = captureAreaOnScreen.Height / frame.Height;
        return new(captureAreaOnScreen.X + rectInFrame.X * sx, captureAreaOnScreen.Y + rectInFrame.Y * sy,
            rectInFrame.Width * sx, rectInFrame.Height * sy);
    }

    public static CameraItem FromScreen(RectF cameraOnScreen, RectF captureAreaOnScreen, CameraItem current)
    {
        var a = captureAreaOnScreen;
        float size = Math.Clamp(cameraOnScreen.Height / a.Height, MinSize, MaxSize);
        float maxX = Math.Max(0, 1 - size * a.Height / a.Width), maxY = 1 - size;
        return current with
        {
            X = Math.Clamp((cameraOnScreen.X - a.X) / a.Width, 0, maxX),
            Y = Math.Clamp((cameraOnScreen.Y - a.Y) / a.Height, 0, maxY),
            Size = size,
        };
    }
}
