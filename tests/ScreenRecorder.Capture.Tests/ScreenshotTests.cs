using ScreenRecorder.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ScreenRecorder.Capture.Tests;

public class ScreenshotTests
{
    static async Task<(uint, uint)> Size(string path)
    {
        using var s = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
        var d = await BitmapDecoder.CreateAsync(s);
        return (d.PixelWidth, d.PixelHeight);
    }

    [Fact]
    public async Task Region_screenshot_has_region_size()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var path = await Screenshot.CaptureAsync(new RegionTarget(CaptureTarget.PrimaryMonitor(), 0, 0, 320, 200), temp, copyToClipboard: false);
            Assert.Equal((320u, 200u), await Size(path));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Missing_folder_is_created()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var path = await Screenshot.CaptureAsync(new RegionTarget(CaptureTarget.PrimaryMonitor(), 0, 0, 320, 200), Path.Combine(temp, "new", "sub"), copyToClipboard: false);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Same_second_screenshots_get_separate_files()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var target = new RegionTarget(CaptureTarget.PrimaryMonitor(), 0, 0, 320, 200);
            var a = await Screenshot.CaptureAsync(target, temp, copyToClipboard: false);
            var b = await Screenshot.CaptureAsync(target, temp, copyToClipboard: false);
            Assert.NotEqual(a, b);
            Assert.True(File.Exists(a) && File.Exists(b));
        }
        finally { Directory.Delete(temp, true); }
    }
}
