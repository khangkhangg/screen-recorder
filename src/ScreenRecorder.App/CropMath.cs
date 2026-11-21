using Windows.Foundation;
using Windows.Graphics;

namespace ScreenRecorder.App;

[Flags]
public enum Grip { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

// The crop rectangle lives in picture pixels; the pointer moves in view pixels.
public static class CropMath
{
    const int MinSize = 16;

    public static RectInt32 Drag(RectInt32 start, Grip grip, double dx, double dy, double scale, int width, int height)
    {
        int mx = (int)Math.Round(dx / scale), my = (int)Math.Round(dy / scale);
        if (grip == Grip.Move)
            return new(Math.Clamp(start.X + mx, 0, width - start.Width), Math.Clamp(start.Y + my, 0, height - start.Height), start.Width, start.Height);
        int minW = Math.Min(MinSize, width), minH = Math.Min(MinSize, height);
        int l = start.X, t = start.Y, r = start.X + start.Width, b = start.Y + start.Height;
        if (grip.HasFlag(Grip.Left)) l = Math.Clamp(l + mx, 0, r - minW);
        if (grip.HasFlag(Grip.Right)) r = Math.Clamp(r + mx, l + minW, width);
        if (grip.HasFlag(Grip.Top)) t = Math.Clamp(t + my, 0, b - minH);
        if (grip.HasFlag(Grip.Bottom)) b = Math.Clamp(b + my, t + minH, height);
        return new(l, t, r - l, b - t);
    }

    // Grab areas are 24 view pixels wide, centred on the edges; corners win over edges.
    public static Grip HitTest(Rect view, Point p, double grab = 24)
    {
        var h = grab / 2;
        if (p.X < view.Left - h || p.X > view.Right + h || p.Y < view.Top - h || p.Y > view.Bottom + h) return Grip.None;
        double dl = Math.Abs(p.X - view.Left), dr = Math.Abs(p.X - view.Right), dt = Math.Abs(p.Y - view.Top), db = Math.Abs(p.Y - view.Bottom);
        var grip = Grip.None;
        if (Math.Min(dl, dr) <= h) grip |= dl <= dr ? Grip.Left : Grip.Right;
        if (Math.Min(dt, db) <= h) grip |= dt <= db ? Grip.Top : Grip.Bottom;
        if (grip != Grip.None) return grip;
        return view.Contains(p) ? Grip.Move : Grip.None;
    }
}
