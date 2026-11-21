using Microsoft.Graphics.Canvas;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ScreenRecorder.Capture;

public record ScreenshotResult(string Path, bool Copied);

public static class Screenshot
{
    public static Task<ScreenshotResult> CaptureAsync(CaptureTarget target, string outputRoot, bool copyToClipboard = true) =>
        CaptureAsync(target, outputRoot, copyToClipboard ? CopyToClipboard : null);

    public static async Task CopyToClipboard(string path)
    {
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(path)));
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    // The copy function is a seam so a test can make the copy fail.
    internal static async Task<ScreenshotResult> CaptureAsync(CaptureTarget target, string outputRoot, Func<string, Task>? copy)
    {
        using var source = ScreenSource.Create(target, CanvasDevice.GetSharedDevice());
        var deadline = DateTime.UtcNow.AddSeconds(2);
        CanvasBitmap? frame;
        while (!source.TryGetLatestFrame(out frame))
        {
            if (DateTime.UtcNow > deadline) throw new CaptureUnavailableException("No frame arrived within 2 seconds.");
            await Task.Delay(20);
        }

        Directory.CreateDirectory(outputRoot);
        var path = UniquePath(outputRoot, DateTime.Now);

        using (var stream = new FileStream(path, FileMode.CreateNew).AsRandomAccessStream())
            await frame!.SaveAsync(stream, CanvasBitmapFileFormat.Png);

        var copied = false;
        if (copy is not null)
            try
            {
                await copy(path);
                copied = true;
            }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } // the saved file is the result, so a clipboard failure must not lose it
        return new(path, copied);
    }

    // Writes a new PNG; the source file is only read.
    public static Task CropAsync(string source, string target, int x, int y, int width, int height) =>
        CropAsync(source, target, x, y, width, height, (crop, output) => crop.SaveAsync(output, CanvasBitmapFileFormat.Png).AsTask());

    // The save function is a seam so a test can make the encoding fail.
    internal static async Task CropAsync(string source, string target, int x, int y, int width, int height, Func<CanvasBitmap, IRandomAccessStream, Task> save)
    {
        var device = CanvasDevice.GetSharedDevice();
        CanvasBitmap bitmap;
        using (var input = File.OpenRead(source).AsRandomAccessStream()) bitmap = await CanvasBitmap.LoadAsync(device, input);
        using (bitmap)
        using (var crop = CanvasBitmap.CreateFromBytes(device, bitmap.GetPixelBytes(x, y, width, height), width, height, bitmap.Format))
        {
            var file = new FileStream(target, FileMode.CreateNew);
            try
            {
                using var output = file.AsRandomAccessStream();
                await save(crop, output);
            }
            catch
            {
                file.Dispose();
                File.Delete(target); // an empty or partial picture is worse than none
                throw;
            }
            finally { file.Dispose(); }
        }
    }

    public static string UniquePath(string folder, DateTime time)
    {
        var stem = Path.Combine(folder, $"Screenshot {time:yyyy-MM-dd HH-mm-ss}");
        var path = stem + ".png";
        for (var n = 2; File.Exists(path); n++) path = $"{stem} ({n}).png";
        return path;
    }
}
