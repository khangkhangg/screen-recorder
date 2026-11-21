using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture.Tests;

public class VideoFileWriterTests
{
    // shortcut: uses the software encoder on PCs without hardware H.264, replace with a skip when xunit 3 is in.
    public static bool Hardware { get; } = EncoderProbe.Check(1) == EncoderSupport.Hardware;

    static string TempMp4() => Path.Combine(Path.GetTempPath(), $"vfw-{Guid.NewGuid():N}.mp4");

    // Returns whether the writer used a hardware encoder.
    static bool WriteSolidFrames(string path, int count, bool hardware, bool withAudio = false)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 1280, 720, 96);
        using var writer = new VideoFileWriter(path, new PixelSize(1280, 720), 60, device, hardware, withAudio);
        for (int i = 0; i < count; i++)
        {
            using (var ds = target.CreateDrawingSession()) ds.Clear(Windows.UI.Color.FromArgb(255, 0, (byte)(i * 2), 255));
            writer.WriteFrame(target, i);
        }
        if (withAudio) writer.WriteAudio(new float[96_000 * 2], 0);
        return writer.UsesHardwareEncoder;
    }

    [Fact]
    public void Writes_constant_60fps()
    {
        var path = TempMp4();
        Assert.Equal(Hardware, WriteSolidFrames(path, 120, Hardware));
        var info = MediaInfo.Read(path);
        Assert.Equal(120, info.FrameCount);
        Assert.InRange(info.Duration.TotalSeconds, 1.98, 2.02);
        Assert.Equal((1280, 720), (info.Width, info.Height));
        Assert.True(info.ConstantFrameRate);
    }

    [Fact]
    public void Cut_off_file_still_plays()
    {
        var path = TempMp4();
        var exe = Path.Combine(AppContext.BaseDirectory, "ScreenRecorder.Capture.Tests.exe");
        using var child = Process.Start(new ProcessStartInfo(exe, ["write-mp4", path]) { RedirectStandardOutput = true })!;
        try
        {
            while (child.StandardOutput.ReadLine() is { } line && line != "200") { }
        }
        finally { child.Kill(); child.WaitForExit(); }
        Assert.True(MediaInfo.Read(path).FrameCount >= 60);
    }

    [Fact]
    public void Audio_and_video_have_equal_length()
    {
        var path = TempMp4();
        WriteSolidFrames(path, 120, Hardware, withAudio: true);
        var info = MediaInfo.Read(path);
        Assert.InRange(info.AudioDuration.TotalSeconds, info.Duration.TotalSeconds - 0.05, info.Duration.TotalSeconds + 0.05);
    }

    [Fact]
    public void Audio_written_ahead_of_video_is_kept()
    {
        var path = TempMp4();
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 1280, 720, 96);
        var writer = new VideoFileWriter(path, new PixelSize(1280, 720), 60, device, Hardware, true);
        for (int i = 0; i < 200; i++) writer.WriteAudio(new float[480 * 2], i * 480);
        for (int i = 0; i < 120; i++)
        {
            using (var ds = target.CreateDrawingSession()) ds.Clear(Windows.UI.Color.FromArgb(255, 0, (byte)(i * 2), 255));
            writer.WriteFrame(target, i);
        }
        var watch = Stopwatch.StartNew();
        writer.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {watch.Elapsed}");
        var info = MediaInfo.Read(path);
        Assert.InRange(info.Duration.TotalSeconds, 1.95, 2.05);
        Assert.InRange(info.AudioDuration.TotalSeconds, 1.95, 2.05);
    }

    [Fact]
    public void Probe_reports_a_value() => Assert.True(Enum.IsDefined(EncoderProbe.Check(3)));

    [Fact]
    public void Software_encoder_works()
    {
        var path = TempMp4();
        Assert.False(WriteSolidFrames(path, 30, hardware: false));
        Assert.Equal(30, MediaInfo.Read(path).FrameCount);
    }

    [Fact]
    public void Audio_on_a_video_only_writer_throws()
    {
        using var writer = new VideoFileWriter(TempMp4(), new PixelSize(1280, 720), 60, CanvasDevice.GetSharedDevice(), false, false);
        Assert.Throws<InvalidOperationException>(() => writer.WriteAudio(new float[2], 0));
    }
}
