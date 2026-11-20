namespace ScreenRecorder.Core;

public record struct PixelSize(int Width, int Height);

public record struct RectF(float X, float Y, float Width, float Height);

public static class FrameMath
{
    public static PixelSize ToEven(PixelSize s) => new(s.Width & ~1, s.Height & ~1);

    public static PixelSize FitWithin(PixelSize source, PixelSize max)
    {
        long w = source.Width, h = source.Height;
        if (w > max.Width || h > max.Height)
        {
            if ((long)max.Width * h <= (long)max.Height * w) { h = h * max.Width / w; w = max.Width; }
            else { w = w * max.Height / h; h = max.Height; }
        }
        return ToEven(new((int)w, (int)h));
    }

    public static RectF Letterbox(PixelSize source, PixelSize frame)
    {
        var scale = Math.Min((float)frame.Width / source.Width, (float)frame.Height / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        return new((frame.Width - w) / 2, (frame.Height - h) / 2, w, h);
    }

    public static int Bitrate(PixelSize size, int frameRate) => size.Width * size.Height * frameRate / 10;
}
