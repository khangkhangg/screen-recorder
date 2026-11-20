using Microsoft.Graphics.Canvas;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ScreenRecorder.Capture;

public static class Screenshot
{
    public static async Task<string> CaptureAsync(CaptureTarget target, string outputRoot, bool copyToClipboard = true)
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
        var stem = Path.Combine(outputRoot, $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        var path = stem + ".png";
        for (var n = 2; File.Exists(path); n++) path = $"{stem} ({n}).png";

        using (var stream = new FileStream(path, FileMode.CreateNew).AsRandomAccessStream())
            await frame!.SaveAsync(stream, CanvasBitmapFileFormat.Png);

        if (copyToClipboard)
        {
            try
            {
                var data = new DataPackage();
                data.SetBitmap(RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(path)));
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }
            catch (Exception) { } // the saved file is the result, so a clipboard failure must not lose it
        }
        return path;
    }
}
