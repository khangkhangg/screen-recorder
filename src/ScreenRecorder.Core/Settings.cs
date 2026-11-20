using System.Text.Json;

namespace ScreenRecorder.Core;

public record Settings(string OutputFolder, Hotkey RecordHotkey, Hotkey ScreenshotHotkey, bool Countdown, int FrameRate,
    string? WebcamId, string? MicId, bool Mic, bool SystemAudio, Layout Layout)
{
    public static Settings Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder"),
        new(true, true, false, false, 0x52), new(true, true, false, false, 0x53),
        true, 60, null, null, true, true, Layout.Default);

    public static Settings Load(string path)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), SessionJson.Options) ?? Default; }
        catch { return Default; } // missing or corrupt file both fall back to defaults
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SessionJson.Options));
    }
}
