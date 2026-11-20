using System.Diagnostics.CodeAnalysis;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenRecorder.Capture.Tests;

public class RecordingSessionTests
{
    static RecordingOptions Options() => new(new MonitorTarget(CaptureTarget.PrimaryMonitor()),
        Path.Combine(Path.GetTempPath(), $"rs-{Guid.NewGuid():N}"), 60, null, false, null, true, Layout.Default, true);

    static async Task<RecordingSession> Start(RecordingOptions o, TestFactory? f = null)
    {
        f ??= new TestFactory();
        return await RecordingSession.StartAsync(o, await RecordingSession.PreflightAsync(o, f), f);
    }

    static double WavSeconds(string path) => (new FileInfo(path).Length - 44) / (48_000.0 * 8);

    [Fact]
    public async Task Two_second_recording_makes_aligned_files()
    {
        await using var s = await Start(Options());
        await Task.Delay(2000);
        var folder = (await s.StopAsync()).Folder;
        Assert.True(File.Exists(Path.Combine(folder, "final.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "screen.mp4")));
        Assert.False(File.Exists(Path.Combine(folder, "webcam.mp4")));
        var a = MediaInfo.Read(Path.Combine(folder, "final.mp4")).Duration;
        var b = MediaInfo.Read(Path.Combine(folder, "screen.mp4")).Duration;
        Assert.InRange(Math.Abs((a - b).TotalSeconds), 0, 0.05);
        Assert.True(MediaInfo.Read(Path.Combine(folder, "screen.mp4")).ConstantFrameRate);
    }

    [Fact]
    public async Task Pause_is_cut_from_every_file()
    {
        await using var s = await Start(Options());
        await Task.Delay(1000);
        s.Pause();
        await Task.Delay(2000);
        s.Resume();
        await Task.Delay(1000);
        var folder = (await s.StopAsync()).Folder;
        Assert.InRange(MediaInfo.Read(Path.Combine(folder, "screen.mp4")).Duration.TotalSeconds, 1.9, 2.2);
        Assert.InRange(WavSeconds(Path.Combine(folder, "system.wav")), 1.9, 2.2);
        var session = SessionJson.Deserialize(File.ReadAllText(Path.Combine(folder, "session.json")));
        Assert.Single(session.Pauses);
    }

    [Fact]
    public async Task Layout_changes_are_logged()
    {
        await using var s = await Start(Options());
        await Task.Delay(500);
        s.SetLayout(Layout.Default with { Camera = Layout.Default.Camera with { X = 0.5f } });
        await Task.Delay(500);
        var folder = (await s.StopAsync()).Folder;
        var session = SessionJson.Deserialize(File.ReadAllText(Path.Combine(folder, "session.json")));
        Assert.Equal(2, session.LayoutChanges.Count);
    }

    [Fact]
    public async Task Sources_that_are_off_write_no_file()
    {
        await using var s = await Start(Options() with { Mic = false, SystemAudio = false });
        await Task.Delay(1000);
        var folder = (await s.StopAsync()).Folder;
        Assert.Empty(Directory.GetFiles(folder, "*.wav"));
        Assert.True(File.Exists(Path.Combine(folder, "screen.mp4")));
    }

    [Fact]
    public async Task WriteFinal_false_skips_final()
    {
        await using var s = await Start(Options() with { WriteFinal = false });
        await Task.Delay(1000);
        var folder = (await s.StopAsync()).Folder;
        Assert.False(File.Exists(Path.Combine(folder, "final.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "screen.mp4")));
    }

    [Fact]
    public async Task Lost_webcam_keeps_recording()
    {
        var cam = new FakeWebcam();
        await using var s = await Start(Options() with { WebcamId = "fake" }, new TestFactory { Webcam = cam });
        await Task.Delay(1000);
        cam.RaiseLost();
        await Task.Delay(1000);
        var result = await s.StopAsync();
        Assert.InRange(MediaInfo.Read(Path.Combine(result.Folder, "screen.mp4")).Duration.TotalSeconds, 1.9, 2.2);
        Assert.Contains(result.Notes, n => n.StartsWith("webcam lost"));
        Assert.True(MediaInfo.Read(Path.Combine(result.Folder, "webcam.mp4")).FrameCount > 0);
    }

    [Fact]
    public async Task Lost_mic_keeps_recording()
    {
        var mic = new FakeMic();
        await using var s = await Start(Options() with { Mic = true }, new TestFactory { Mic = mic });
        await Task.Delay(1000);
        mic.RaiseLost();
        await Task.Delay(1000);
        var result = await s.StopAsync();
        Assert.InRange(MediaInfo.Read(Path.Combine(result.Folder, "screen.mp4")).Duration.TotalSeconds, 1.9, 2.2);
        Assert.Contains(result.Notes, n => n.StartsWith("mic lost"));
        Assert.True(WavSeconds(Path.Combine(result.Folder, "mic.wav")) > 0);
    }

    [Fact]
    public async Task Disk_full_stops_and_keeps_files_playable()
    {
        var f = new TestFactory { Wrap = (name, w) => name == "screen.mp4" ? new FakeWriter(w, throwAt: 60) : w };
        await using var s = await Start(Options(), f);
        var stoppedByItself = new TaskCompletionSource<RecordingResult>();
        s.StoppedByItself += r => stoppedByItself.TrySetResult(r);
        var result = await stoppedByItself.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(result.Notes, n => n.Contains("disk"));
        Assert.True(MediaInfo.Read(Path.Combine(result.Folder, "screen.mp4")).FrameCount > 0);
        Assert.True(MediaInfo.Read(Path.Combine(result.Folder, "final.mp4")).FrameCount > 0);
        Assert.Same(result, await s.StopAsync());
    }

    [Fact]
    public async Task Source_resize_keeps_frame_size()
    {
        FakeScreen? screen = null;
        await using var s = await Start(Options(), new TestFactory { Screen = () => screen = new FakeScreen() });
        await Task.Delay(1000);
        screen!.SourceSize = new PixelSize(1280, 1024);
        await Task.Delay(1000);
        var folder = (await s.StopAsync()).Folder;
        Assert.Equal(1920, MediaInfo.Read(Path.Combine(folder, "screen.mp4")).Width);
    }

    [Fact]
    public async Task Source_closed_stops_and_saves()
    {
        FakeScreen? screen = null;
        await using var s = await Start(Options(), new TestFactory { Screen = () => screen = new FakeScreen() });
        var stoppedByItself = new TaskCompletionSource<RecordingResult>();
        s.StoppedByItself += r => stoppedByItself.TrySetResult(r);
        await Task.Delay(1000);
        screen!.RaiseClosed();
        var result = await stoppedByItself.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(MediaInfo.Read(Path.Combine(result.Folder, "screen.mp4")).FrameCount > 0);
        Assert.True(MediaInfo.Read(Path.Combine(result.Folder, "final.mp4")).FrameCount > 0);
        Assert.True(File.Exists(Path.Combine(result.Folder, "session.json")));
    }

    [Fact]
    public async Task Slow_final_encoder_drops_final_frames_only()
    {
        var f = new TestFactory { Wrap = (name, w) => name == "final.mp4" ? new FakeWriter(w, behind: 100) : w };
        await using var s = await Start(Options(), f);
        await Task.Delay(2000);
        var result = await s.StopAsync();
        Assert.True(result.DroppedFrames > 0);
        var screen = MediaInfo.Read(Path.Combine(result.Folder, "screen.mp4")).FrameCount;
        Assert.InRange(screen, 110, 125);
        Assert.Equal(screen, MediaInfo.Read(Path.Combine(result.Folder, "final.mp4")).FrameCount);
    }

    sealed class TestFactory : ISourceFactory
    {
        public Func<IScreenSource>? Screen;
        public IWebcamSource? Webcam;
        public IAudioSource? Mic;
        public Func<string, IVideoWriter, IVideoWriter> Wrap = (_, w) => w;

        public IScreenSource OpenScreen(CaptureTarget t, CanvasDevice d) => Screen?.Invoke() ?? SourceFactory.Instance.OpenScreen(t, d);
        public Task<IWebcamSource> OpenWebcamAsync(string id, CanvasDevice d) => Task.FromResult(Webcam!);
        public Task<IAudioSource> OpenMicAsync(string? id) => Mic is null ? SourceFactory.Instance.OpenMicAsync(id) : Task.FromResult(Mic);
        public Task<IAudioSource> OpenSystemAudioAsync() => SourceFactory.Instance.OpenSystemAudioAsync();
        public IVideoWriter OpenWriter(string path, PixelSize size, int frameRate, CanvasDevice d, bool hardware, bool withAudio) =>
            Wrap(Path.GetFileName(path), SourceFactory.Instance.OpenWriter(path, size, frameRate, d, hardware, withAudio));
    }

    sealed class FakeScreen : IScreenSource
    {
        CanvasRenderTarget? _frame;
        public PixelSize SourceSize { get; set; } = new(1920, 1080);
        public bool IsMinimized => false;
        public event Action? Closed;
        public void RaiseClosed() => Closed?.Invoke();

        public bool TryGetLatestFrame([MaybeNullWhen(false)] out CanvasBitmap frame)
        {
            var size = SourceSize;
            if (_frame is null || _frame.SizeInPixels.Width != size.Width || _frame.SizeInPixels.Height != size.Height)
            {
                _frame?.Dispose();
                _frame = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size.Width, size.Height, 96);
                using var ds = _frame.CreateDrawingSession();
                ds.Clear(Windows.UI.Color.FromArgb(255, 200, 0, 0));
            }
            frame = _frame;
            return true;
        }

        public void Dispose() => _frame?.Dispose();
    }

    sealed class FakeWebcam : IWebcamSource
    {
        readonly CanvasRenderTarget _frame = new(CanvasDevice.GetSharedDevice(), 640, 480, 96);
        public PixelSize Size => new(640, 480);
        public event Action? Lost;
        public void RaiseLost() => Lost?.Invoke();

        public bool TryGetLatestFrame([MaybeNullWhen(false)] out CanvasBitmap frame)
        {
            frame = _frame;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            _frame.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakeMic : IAudioSource
    {
        public event Action? Lost;
        public void RaiseLost() => Lost?.Invoke();
        public int Read(Span<float> interleavedStereo48k) => 0;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeWriter(IVideoWriter inner, long behind = -1, long throwAt = -1) : IVideoWriter
    {
        public long FramesBehind => behind >= 0 ? behind : inner.FramesBehind;

        public void WriteFrame(IDirect3DSurface surface, long frameIndex)
        {
            if (throwAt >= 0 && frameIndex >= throwAt) throw new IOException("There is not enough space on the disk.");
            inner.WriteFrame(surface, frameIndex);
        }

        public void WriteAudio(ReadOnlySpan<float> interleavedStereo48k, long firstSampleIndex) => inner.WriteAudio(interleavedStereo48k, firstSampleIndex);
        public void Dispose() => inner.Dispose();
    }
}
