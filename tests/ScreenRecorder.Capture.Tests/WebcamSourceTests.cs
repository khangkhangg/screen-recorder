using ScreenRecorder.Capture;

namespace ScreenRecorder.Capture.Tests;

public class WebcamSourceTests
{
    [Fact]
    public async Task Webcam_list_does_not_throw() => Assert.NotNull(await WebcamSource.ListAsync());
}
