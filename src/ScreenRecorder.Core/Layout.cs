namespace ScreenRecorder.Core;

public enum CameraShape { Circle, RoundedSquare, Square }
public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

public record CameraItem(bool Visible, float X, float Y, float Size, CameraShape Shape);
public record LogoItem(bool Visible, Corner Corner, float Size, string? Path);

public record Layout(CameraItem Camera, LogoItem Logo)
{
    const float Margin = 0.02f;

    public static Layout Default { get; } = new(
        new CameraItem(true, Margin, 1 - Margin - 0.25f, 0.25f, CameraShape.Circle),
        new LogoItem(false, Corner.BottomRight, 0.08f, null));

    public static RectF CameraRect(CameraItem c, PixelSize frame)
    {
        float side = c.Size * frame.Height;
        return new(Math.Clamp(c.X * frame.Width, 0, frame.Width - side), Math.Clamp(c.Y * frame.Height, 0, frame.Height - side), side, side);
    }

    public static RectF LogoRect(LogoItem l, PixelSize frame, PixelSize logoPixels)
    {
        float h = l.Size * frame.Height, w = h * logoPixels.Width / logoPixels.Height, m = Margin * frame.Height;
        bool right = l.Corner is Corner.TopRight or Corner.BottomRight, bottom = l.Corner is Corner.BottomLeft or Corner.BottomRight;
        return new(Math.Clamp(right ? frame.Width - m - w : m, 0, frame.Width - w), Math.Clamp(bottom ? frame.Height - m - h : m, 0, frame.Height - h), w, h);
    }
}
