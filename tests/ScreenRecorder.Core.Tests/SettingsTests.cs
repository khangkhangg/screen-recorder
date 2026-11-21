using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Round_trips_and_creates_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Default with { FrameRate = 30, Mic = false, Region = new SavedRegion(@"\\.\DISPLAY1", 10, 20, 300, 200) };
        s.Save(path);
        Assert.Equivalent(s, Settings.Load(path));
    }

    [Fact]
    public void Corrupt_or_missing_gives_default()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{{{");
        Assert.Equivalent(Settings.Default, Settings.Load(path));
        Assert.Equivalent(Settings.Default, Settings.Load(path + ".none"));
    }

    [Fact]
    public void Empty_object_gives_default()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{}");
        Assert.Equivalent(Settings.Default, Settings.Load(path));
    }

    [Fact]
    public void Old_file_without_a_region_still_loads()
    {
        var path = Path.GetTempFileName();
        var old = Settings.Default with { FrameRate = 30 };
        old.Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("region");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equivalent(old, Settings.Load(path));
        Assert.Null(Settings.Load(path).Region);
    }

    [Fact]
    public void Empty_layout_gives_default()
    {
        var path = Path.GetTempFileName();
        Settings.Default.Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["layout"] = new System.Text.Json.Nodes.JsonObject();
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equivalent(Settings.Default, Settings.Load(path));
    }
}
