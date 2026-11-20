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
    public void Same_second_names_get_a_counter()
    {
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        try
        {
            var t = new DateTime(2026, 11, 20, 10, 30, 0);
            var first = Screenshot.UniquePath(temp, t);
            Assert.EndsWith("Screenshot 2026-11-20 10-30-00.png", first);
            File.WriteAllText(first, "");
            var second = Screenshot.UniquePath(temp, t);
            Assert.EndsWith(" (2).png", second);
            File.WriteAllText(second, "");
            Assert.EndsWith(" (3).png", Screenshot.UniquePath(temp, t));
        }
        finally { Directory.Delete(temp, true); }
    }
}
