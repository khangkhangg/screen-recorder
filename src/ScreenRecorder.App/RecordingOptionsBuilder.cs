using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

public static class RecordingOptionsBuilder
{
    public static RecordingOptions Build(Settings settings, CaptureTarget target, string? firstWebcamId) =>
        new(target, settings.OutputFolder, settings.FrameRate,
            settings.Layout.Camera.Visible ? settings.WebcamId ?? firstWebcamId : null,
            settings.Mic, settings.MicId, settings.SystemAudio, settings.Layout, WriteFinal: true, settings.ClickEffect);
}
