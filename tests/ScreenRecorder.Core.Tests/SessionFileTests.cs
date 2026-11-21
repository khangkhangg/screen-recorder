using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class SessionFileTests
{
    static SessionFile Sample() => new(1, DateTimeOffset.Parse("2026-11-20T10:00:00+07:00"), new PixelSize(1920, 1080), 60, "monitor",
        [new("screen.mp4", "screen", 0, 1920, 1080)], [new(0, Layout.Default)], [new(5.0, 2.5)], 0, []);

    [Fact]
    public void Session_round_trips()
    {
        var f = Sample();
        Assert.Equivalent(f, SessionJson.Deserialize(SessionJson.Serialize(f)));
    }

    [Fact]
    public void Session_uses_plain_names()
    {
        var json = SessionJson.Serialize(Sample());
        Assert.Contains("\"shape\": \"Circle\"", json);
        Assert.Contains("\"version\": 1", json);
    }

    [Fact]
    public void Newer_version_is_refused() =>
        Assert.Throws<NotSupportedException>(() => SessionJson.Deserialize("{\"version\": 2}"));
}
