namespace ScreenRecorder.Core;

public enum ClickEffect { Off, Ring, Ripple, Dot, Halo }

public enum ClickButton { Left, Right, Middle }

// Radius and Stroke are in DIP, Stroke 0 means filled. Color is 0xRRGGBB.
public readonly record struct ClickShape(float Radius, float Stroke, float Opacity, uint Color);

public static class ClickMath
{
    public const uint Amber = 0xEF9F27, Blue = 0x378ADD;

    public static uint ColorOf(ClickButton b) => b == ClickButton.Left ? Amber : Blue;

    // Writes the shapes of one click at this age into `into` (room for 2) and returns how many.
    // Halo is the pointer's circle: it always returns one shape, at rest once the click is over.
    public static int Shapes(ClickEffect effect, ClickButton button, double ageMs, Span<ClickShape> into)
    {
        var color = ColorOf(button);
        switch (effect)
        {
            case ClickEffect.Ring:
                return Ring(ageMs, 400, 3, color, into);
            case ClickEffect.Ripple:
                int n = Ring(ageMs, 450, 3, color, into);
                return n + Ring(ageMs - 140, 450, 2, color, into[n..]);
            case ClickEffect.Dot:
                if (ageMs < 0 || ageMs >= 400) return 0;
                into[0] = ageMs < 120
                    ? new(Lerp(0, 15, ageMs / 120), 0, 0.75f, color)
                    : new(Lerp(15, 18, (ageMs - 120) / 280), 0, Lerp(0.75f, 0, (ageMs - 120) / 280), color);
                return 1;
            case ClickEffect.Halo:
                into[0] = ageMs < 0 || ageMs >= 300 ? new(22, 0, 0.25f, Amber)
                    : new(22, 0, ageMs < 120 ? Lerp(0.25f, 0.6f, ageMs / 120) : Lerp(0.6f, 0.25f, (ageMs - 120) / 180), color);
                return 1;
            default:
                return 0;
        }
    }

    static int Ring(double ageMs, double life, float stroke, uint color, Span<ClickShape> into)
    {
        if (ageMs < 0 || ageMs >= life) return 0;
        var t = 1 - Math.Pow(1 - ageMs / life, 2);
        into[0] = new(Lerp(3, 28, t), stroke, Lerp(1, 0, t), color);
        return 1;
    }

    static float Lerp(float a, float b, double t) => (float)(a + (b - a) * t);
}
