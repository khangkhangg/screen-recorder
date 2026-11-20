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
        var r = cameraOnScreen;
        // the side also fits the width, so a tall narrow area still holds the whole camera
        float size = Math.Min(Math.Clamp(r.Height / a.Height, MinSize, MaxSize), a.Width / a.Height);
        float half = size * a.Height / 2, maxX = Math.Max(0, 1 - size * a.Height / a.Width), maxY = 1 - size;
        // position comes from the center, so a clamped size does not walk the camera across the area
        return current with
        {
            X = Math.Clamp((r.X + r.Width / 2 - half - a.X) / a.Width, 0, maxX),
            Y = Math.Clamp((r.Y + r.Height / 2 - half - a.Y) / a.Height, 0, maxY),
            Size = size,
        };
    }
}
