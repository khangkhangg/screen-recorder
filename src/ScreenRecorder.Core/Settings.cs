using System.Text.Json;

namespace ScreenRecorder.Core;

// The last region picked: the monitor's device name and the rectangle in that monitor's pixels.
public record SavedRegion(string Monitor, int X, int Y, int Width, int Height);

public record Settings(string OutputFolder, Hotkey RecordHotkey, Hotkey ScreenshotHotkey, bool Countdown, int FrameRate,
    string? WebcamId, string? MicId, bool Mic, bool SystemAudio, Layout Layout,
    SavedRegion? Region = null, bool Mini = false)
{
    public static Settings Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder"),
        new(true, true, false, false, 0x52), new(true, true, false, false, 0x53),
        true, 60, null, null, true, true, Layout.Default);

    public static Settings Load(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), SessionJson.Options);
            // missing fields deserialize to null or 0, which the app cannot use
            return s is null || s.FrameRate <= 0 || s.OutputFolder is null || s.RecordHotkey is null
                || s.ScreenshotHotkey is null || s.Layout?.Camera is null || s.Layout.Logo is null ? Default : s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return Default; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SessionJson.Options));
    }
}
