using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture.Tests;

public class ScreenSourceTests
{
    [Fact]
    public async Task Primary_monitor_gives_a_frame()
    {
        using var src = ScreenSource.Create(new MonitorTarget(CaptureTarget.PrimaryMonitor()), CanvasDevice.GetSharedDevice());
        await WaitUntil(() => src.TryGetLatestFrame(out _), TimeSpan.FromSeconds(2));
        Assert.True(src.SourceSize.Width > 0);
    }

    [Fact]
    public async Task Region_is_cropped()
    {
        using var src = ScreenSource.Create(new RegionTarget(CaptureTarget.PrimaryMonitor(), 100, 100, 640, 360), CanvasDevice.GetSharedDevice());
        await WaitUntil(() => src.TryGetLatestFrame(out _), TimeSpan.FromSeconds(2));
        Assert.True(src.TryGetLatestFrame(out var f));
        Assert.Equal(new PixelSize(640, 360), src.SourceSize);
        Assert.Equal(640u, f.SizeInPixels.Width);
    }

    [Fact]
    public async Task Closing_the_window_raises_Closed()
    {
        // Notepad on Windows 11 hands its window to another process, so the test opens its own form instead.
        using var app = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Application]::Run((New-Object Windows.Forms.Form))")
        { CreateNoWindow = true })!;
        try
        {
            await WaitUntil(() => { app.Refresh(); return app.MainWindowHandle != 0; }, TimeSpan.FromSeconds(15));
            using var src = ScreenSource.Create(new WindowTarget(app.MainWindowHandle), CanvasDevice.GetSharedDevice());
            var closed = new TaskCompletionSource();
            src.Closed += () => closed.TrySetResult();
            app.Kill();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void Invalid_window_throws_plain_error() =>
        Assert.Throws<CaptureUnavailableException>(() => ScreenSource.Create(new WindowTarget(0x1), CanvasDevice.GetSharedDevice()));

    [Fact]
    public void Offscreen_region_throws_plain_error() =>
        Assert.Throws<CaptureUnavailableException>(() => ScreenSource.Create(new RegionTarget(CaptureTarget.PrimaryMonitor(), 100_000, 0, 640, 360), CanvasDevice.GetSharedDevice()));

    [Fact]
    public void LooksBlack_detects_black_frame()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var black = Cleared(device, Windows.UI.Color.FromArgb(255, 0, 0, 0));
        using var red = Cleared(device, Windows.UI.Color.FromArgb(255, 255, 0, 0));
        Assert.True(ScreenSource.LooksBlack(black));
        Assert.False(ScreenSource.LooksBlack(red));
    }

    static CanvasRenderTarget Cleared(CanvasDevice device, Windows.UI.Color color)
    {
        var target = new CanvasRenderTarget(device, 320, 200, 96);
        using (var ds = target.CreateDrawingSession()) ds.Clear(color);
        return target;
    }

    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
