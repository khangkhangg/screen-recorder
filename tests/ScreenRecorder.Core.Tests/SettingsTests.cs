using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Round_trips_and_creates_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Default with { FrameRate = 30, Mic = false };
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
}
