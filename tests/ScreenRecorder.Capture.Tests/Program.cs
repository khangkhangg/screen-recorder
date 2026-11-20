using Microsoft.Graphics.Canvas;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture.Tests;

// The crash test starts this exe with "write-mp4 <path>" and kills it mid-recording.
public static class Program
{
    public static void Main(string[] args)
    {
        if (args is not ["write-mp4", var path]) return;
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 1280, 720, 96);
        var writer = new VideoFileWriter(path, new PixelSize(1280, 720), 60, device, VideoFileWriterTests.Hardware, false);
        for (int i = 0; i < 300; i++)
        {
            using (var ds = target.CreateDrawingSession()) ds.Clear(Windows.UI.Color.FromArgb(255, (byte)i, 0, 0));
            writer.WriteFrame(target, i);
            Console.WriteLine(i);
            Thread.Sleep(16);
        }
        writer.Dispose();
    }
}
