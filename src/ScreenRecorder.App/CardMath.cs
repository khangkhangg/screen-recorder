using Windows.Graphics;

namespace ScreenRecorder.App;

// Pure layout and naming for the screenshot card and the crop window.
public static class CardMath
{
    public static RectInt32 CardRect(RectInt32 work, int width, int height, int margin) =>
        new(work.X + work.Width - width - margin, work.Y + work.Height - height - margin, width, height);

    // The line on the card after a recording. Dropped frames are told here, not in a message box.
    public static string RecordingStatus(TimeSpan length, long dropped) =>
        $"Recording saved · {length.ToString(length.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")}" + (dropped > 0 ? $" · {dropped} frames dropped" : "");

    public static string StatusText(bool copied, int width, int height) =>
        $"{(copied ? "Saved and copied" : "Saved")} · {width} x {height}";

    public static (double Width, double Height) Fit(double width, double height, double boxWidth, double boxHeight)
    {
        var scale = Math.Min(1, Math.Min(boxWidth / width, boxHeight / height));
        return (width * scale, height * scale);
    }

    // The share panel takes focus as it opens, so only a deactivation a second later ends the share pause.
    public static bool SharePauseEnds(bool active, bool deactivated, long msSincePanel) => active || deactivated && msSincePanel >= 1000;

    public static string CroppedName(IEnumerable<string> names, string original)
    {
        var taken = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(original) + " (cropped)";
        var name = stem + ".png";
        for (var n = 2; taken.Contains(name); n++) name = $"{stem} ({n}).png";
        return name;
    }
}
