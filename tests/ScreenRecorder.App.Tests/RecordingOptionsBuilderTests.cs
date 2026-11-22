using ScreenRecorder.App;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App.Tests;

public class RecordingOptionsBuilderTests
{
    static readonly CaptureTarget Target = new MonitorTarget(1);

    static Settings WithCamera(bool visible, string? webcamId) =>
        Settings.Default with { WebcamId = webcamId, Layout = Layout.Default with { Camera = Layout.Default.Camera with { Visible = visible } } };

    [Theory]
    [InlineData(false, "chosen", "first", null)]
    [InlineData(true, "chosen", "first", "chosen")]
    [InlineData(true, null, "first", "first")]
    [InlineData(true, null, null, null)]
    public void Webcam_follows_toggle_then_setting_then_first_camera(bool visible, string? setting, string? first, string? expected) =>
        Assert.Equal(expected, RecordingOptionsBuilder.Build(WithCamera(visible, setting), Target, first).WebcamId);

    [Fact]
    public void Other_fields_come_from_settings()
    {
        var s = Settings.Default with { OutputFolder = @"C:\out", FrameRate = 30, Mic = false, MicId = "mic", SystemAudio = false, ClickEffect = ClickEffect.Halo };
        Assert.Equal(new RecordingOptions(Target, @"C:\out", 30, null, false, "mic", false, s.Layout, true, ClickEffect.Halo),
            RecordingOptionsBuilder.Build(s, Target, null));
    }
}
