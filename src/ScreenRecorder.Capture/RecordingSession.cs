using System.Diagnostics;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Core;
using Windows.Graphics.DirectX.Direct3D11;
using static System.FormattableString;

namespace ScreenRecorder.Capture;

public record RecordingOptions(CaptureTarget Target, string OutputRoot, int FrameRate, string? WebcamId, bool Mic, string? MicId, bool SystemAudio, Layout Layout, bool WriteFinal);

public record RecordingResult(string Folder, long DroppedFrames, IReadOnlyList<string> Notes);

public record PreflightResult(EncoderSupport Encoders, PixelSize FrameSize, bool ScaledDown, bool LooksBlack, int FrameRate);

// One recording thread draws and writes every file from one RecordingClock, so all files share frame and sample indexes.
public sealed class RecordingSession : IAsyncDisposable
{
    const int LargeFrame = 2560 * 1440, AudioRate = 48_000, CameraRate = 30, SoftwareRate = 30, FinalMaxBehind = 30, MaxBehind = 60;
    // The mix trails the clock by 100 ms, so a sound packet that arrives a little late is not replaced by silence.
    const long AudioLag = AudioRate / 10;

    sealed class Track(string name, string file)
    {
        public readonly string Name = name, File = file;
        public IAudioSource? Source;
        public WavWriter? Wav;
        public volatile bool Lost;
    }

    readonly RecordingOptions _o;
    readonly PixelSize _size;
    readonly int _frameRate;
    readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    readonly string _folder;
    readonly object _gate = new();
    readonly List<string> _notes = [];
    readonly List<LayoutChange> _layoutChanges = [];
    readonly List<SourceEntry> _sources = [];
    readonly List<Track> _tracks = [];
    readonly float[] _drain = new float[AudioRate];
    readonly TaskCompletionSource<RecordingResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    RecordingClock _clock = null!;
    int _rate;
    IScreenSource? _screen;
    IWebcamSource? _cam;
    Compositor? _compositor;
    IVideoWriter? _screenW, _finalW, _camW;
    CanvasRenderTarget? _black, _camTarget;
    CanvasBitmap? _lastScreen, _lastCam;
    IDirect3DSurface? _lastFinal;
    volatile Layout _layout;
    volatile bool _stopRequested, _closed, _camLost;
    bool _behindNoted, _diskNoted, _ended;
    long _written, _finalNext, _camWritten, _samples, _dropped;

    public event Action<RecordingResult>? StoppedByItself;

    RecordingSession(RecordingOptions o, PreflightResult p)
    {
        _o = o;
        _size = p.FrameSize;
        _frameRate = p.FrameRate;
        _layout = o.Layout;
        _folder = Path.Combine(o.OutputRoot, _startedAt.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
    }

    public static Task<PreflightResult> PreflightAsync(RecordingOptions o) => PreflightAsync(o, SourceFactory.Instance);

    internal static async Task<PreflightResult> PreflightAsync(RecordingOptions o, ISourceFactory f)
    {
        int files = 1 + (o.WriteFinal ? 1 : 0) + (o.WebcamId is null ? 0 : 1);
        var (encoders, max) = await Task.Run(() => (EncoderProbe.Check(files), EncoderProbe.MaxSize()));
        using var screen = f.OpenScreen(o.Target, CanvasDevice.GetSharedDevice());
        CanvasBitmap? frame = null;
        for (int i = 0; i < 100 && !screen.TryGetLatestFrame(out frame); i++) await Task.Delay(20);
        var size = FrameMath.FitWithin(screen.SourceSize, max);
        // The encoders keep up with 4K at 30 frames per second and 1080p at 60.
        int rate = encoders == EncoderSupport.SoftwareOnly ? SoftwareRate
            : (long)size.Width * size.Height > LargeFrame ? Math.Min(o.FrameRate, 30) : o.FrameRate;
        return new(encoders, size, size != FrameMath.ToEven(screen.SourceSize), frame is null || ScreenSource.LooksBlack(frame), rate);
    }

    public static Task<RecordingSession> StartAsync(RecordingOptions o, PreflightResult p) => StartAsync(o, p, SourceFactory.Instance);

    internal static async Task<RecordingSession> StartAsync(RecordingOptions o, PreflightResult p, ISourceFactory f)
    {
        var s = new RecordingSession(o, p);
        try { await s.OpenAsync(f, p.Encoders); }
        catch { s.DisposeAll(); throw; }
        s._clock.Start();
        new Thread(s.Run) { IsBackground = true, Name = "Recording" }.Start();
        return s;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_ended) return;
            _clock.Pause();
            WriteSession();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_ended || _stopRequested || _closed) return;
            _clock.Resume();
            WriteSession();
        }
    }

    public void SetLayout(Layout l)
    {
        lock (_gate)
        {
            if (_ended) return;
            if (l.Logo.Path != _layout.Logo.Path && _compositor!.SetLogo(l.Logo.Path) is { } error) _notes.Add(error);
            _layout = l;
            _layoutChanges.Add(new(_clock.MediaTime.TotalSeconds, l));
            WriteSession();
        }
    }

    public Task<RecordingResult> StopAsync()
    {
        // Freezing the clock now gives every file the stop time, even while the loop is still busy.
        lock (_gate)
        {
            _stopRequested = true;
            _clock.Pause();
        }
        return _done.Task;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    async Task OpenAsync(ISourceFactory f, EncoderSupport encoders)
    {
        Directory.CreateDirectory(_folder);
        var device = CanvasDevice.GetSharedDevice();
        _screen = f.OpenScreen(_o.Target, device);
        _screen.Closed += () =>
        {
            _closed = true;
            _clock?.Pause();
        };
        if (_o.WebcamId is { } id)
        {
            _cam = await f.OpenWebcamAsync(id, device);
            _cam.Lost += () => _camLost = true;
        }
        if (_o.Mic) await AddTrack("mic", "mic.wav", f.OpenMicAsync(_o.MicId));
        if (_o.SystemAudio) await AddTrack("system sound", "system.wav", f.OpenSystemAudioAsync());

        _compositor = new Compositor(device, _size);
        if (_compositor.SetLogo(_o.Layout.Logo.Path) is { } error) _notes.Add(error);
        _black = new CanvasRenderTarget(device, _size.Width, _size.Height, 96);
        using (var ds = _black.CreateDrawingSession()) ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        _lastScreen = _black;

        bool hardware = encoders != EncoderSupport.SoftwareOnly;
        try { OpenWriters(f, device, hardware); }
        catch (NotSupportedException)
        {
            CloseWriters();
            hardware = false;
            OpenWriters(f, device, false);
        }
        if (!hardware) _notes.Add("no hardware encoder, recording at 30 frames per second");
        foreach (var t in _tracks) t.Wav = new WavWriter(Path.Combine(_folder, t.File));

        if (_finalW != null) _sources.Add(new("final.mp4", "final", 0));
        _sources.Add(new("screen.mp4", "screen", 0));
        if (_camW != null) _sources.Add(new("webcam.mp4", "webcam", 0));
        foreach (var t in _tracks) _sources.Add(new(t.File, Path.GetFileNameWithoutExtension(t.File), 0));

        // Wait for the first real frame, so the files do not open on black.
        for (int i = 0; i < 50; i++)
        {
            if (_screen.TryGetLatestFrame(out var frame)) { _lastScreen = frame; break; }
            await Task.Delay(20);
        }
        var watch = Stopwatch.StartNew();
        _clock = new RecordingClock(_rate, () => watch.Elapsed);
        _layoutChanges.Add(new(0, _o.Layout));
        WriteSession();
    }

    async Task AddTrack(string name, string file, Task<IAudioSource> open)
    {
        var t = new Track(name, file) { Source = await open };
        t.Source.Lost += () => t.Lost = true;
        _tracks.Add(t);
    }

    void OpenWriters(ISourceFactory f, CanvasDevice device, bool hardware)
    {
        _rate = hardware ? _frameRate : SoftwareRate;
        _screenW = f.OpenWriter(Path.Combine(_folder, "screen.mp4"), _size, _rate, device, hardware, false);
        if (_o.WriteFinal) _finalW = f.OpenWriter(Path.Combine(_folder, "final.mp4"), _size, _rate, device, hardware, _tracks.Count > 0);
        if (_cam != null)
        {
            var size = FrameMath.ToEven(_cam.Size);
            _camTarget ??= new CanvasRenderTarget(device, size.Width, size.Height, 96);
            _camW = f.OpenWriter(Path.Combine(_folder, "webcam.mp4"), size, CameraRate, device, hardware, false);
        }
    }

    void CloseWriters()
    {
        foreach (var w in new[] { _screenW, _finalW, _camW }) Quietly(() => w?.Dispose());
        _screenW = _finalW = _camW = null;
        foreach (var name in new[] { "screen.mp4", "final.mp4", "webcam.mp4" }) File.Delete(Path.Combine(_folder, name));
    }

    void Run()
    {
        bool self = false;
        try
        {
            while (true)
            {
                bool stop = _stopRequested || _closed;
                if (stop)
                {
                    if (!_stopRequested)
                    {
                        self = true;
                        Note(_o.Target is WindowTarget ? "the window was closed, recording stopped" : "the screen went away, recording stopped");
                    }
                    // Freezing the clock lets the last tick bring every file to the same end time.
                    _clock.Pause();
                }
                Tick(stop);
                if (stop) break;
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            self = !_stopRequested;
            _diskNoted |= e is IOException;
            Note(e is IOException ? $"disk error, recording stopped: {e.Message}" : $"recording stopped: {e.Message}");
        }

        lock (_gate) _ended = true;
        Quietly(() =>
        {
            if (_finalW != null && _lastFinal != null)
                for (; _finalNext < _written; _finalNext++) _finalW.WriteFrame(_lastFinal, _finalNext);
        });
        DisposeAll();
        RecordingResult result;
        lock (_gate)
        {
            result = new(_folder, _dropped, [.. _notes]);
            WriteSession();
        }
        _done.SetResult(result);
        if (self) ThreadPool.QueueUserWorkItem(_ => StoppedByItself?.Invoke(result));
    }

    void Tick(bool flush)
    {
        if (_camLost && _cam is { } cam)
        {
            Note(Invariant($"webcam lost at {_clock.MediaTime.TotalSeconds:0.0} s"));
            var writer = _camW;
            (_cam, _camW, _lastCam) = (null, null, null);
            try { writer?.Dispose(); }
            finally { cam.DisposeAsync().AsTask().Wait(); }
        }
        foreach (var t in _tracks)
        {
            if (!t.Lost || t.Source is not { } source) continue;
            Note(Invariant($"{t.Name} lost at {_clock.MediaTime.TotalSeconds:0.0} s"));
            var wav = t.Wav;
            (t.Source, t.Wav) = (null, null);
            try { wav?.Dispose(); }
            finally { source.DisposeAsync().AsTask().Wait(); }
        }

        if (_written < _clock.FramesDue) WriteVideo();
        long camDue = _clock.MediaTime.Ticks * CameraRate / TimeSpan.TicksPerSecond;
        if (_camW != null && _camWritten < camDue)
        {
            if (_lastCam != null)
                using (var ds = _camTarget!.CreateDrawingSession()) ds.DrawImage(_lastCam);
            for (; _camWritten < camDue; _camWritten++) _camW.WriteFrame(_camTarget!, _camWritten);
        }
        if (_tracks.Count > 0) WriteSound(flush);
    }

    void WriteVideo()
    {
        WaitWhileBehind(_screenW!);
        WaitWhileBehind(_camW);
        // Read the clock again, so time spent waiting is caught up by repeating the newest frame.
        long due = _clock.FramesDue, first = _written;
        if (!_screen!.IsMinimized && _screen.TryGetLatestFrame(out var frame)) _lastScreen = frame;
        if (_cam != null && _cam.TryGetLatestFrame(out var camera)) _lastCam = camera;

        var screen = _compositor!.DrawScreen(_lastScreen!);
        for (; _written < due; _written++) _screenW!.WriteFrame(screen, _written);
        if (_finalW is null) return;
        // A hardware encoder holds the shared device lock, so WriteFrame itself can be slow before FramesBehind grows.
        // A late loop skips final.mp4 too, which keeps screen.mp4 on time.
        bool late = due - first > 2;
        long skipped = due - first;
        if (_lastFinal is null || (!late && _finalW.FramesBehind <= FinalMaxBehind))
        {
            // Skipped indexes get the previous final frame, at most 2 per tick, so final.mp4 keeps a constant frame rate.
            for (int n = 0; n < 2 && _finalNext < first; n++, _finalNext++) _finalW.WriteFrame(_lastFinal!, _finalNext);
            if (_finalNext == first)
            {
                _lastFinal = _compositor.DrawFinal(_lastScreen!, _lastCam, _layout);
                for (; _finalNext < due; _finalNext++) _finalW.WriteFrame(_lastFinal, _finalNext);
                skipped = 0;
            }
        }
        _dropped += skipped;
    }

    void WaitWhileBehind(IVideoWriter? w)
    {
        if (w is null || w.FramesBehind <= MaxBehind) return;
        if (!_behindNoted)
        {
            _behindNoted = true;
            Note("encoder fell behind");
        }
        while (w.FramesBehind >= MaxBehind && !_stopRequested && !_closed) Thread.Sleep(5);
    }

    void WriteSound(bool flush)
    {
        bool paused = _clock.IsPaused;
        long target = _clock.SamplesDue(AudioRate) - (flush || paused ? 0 : AudioLag);
        if (target > _samples)
        {
            int n = (int)(target - _samples) * 2;
            float[] mix = new float[n], buffer = new float[n];
            foreach (var t in _tracks)
            {
                Array.Clear(buffer);
                // Whatever the source has not delivered stays zero, which pads the file with silence.
                t.Source?.Read(buffer);
                t.Wav?.Write(buffer);
                for (int i = 0; i < n; i++) mix[i] += buffer[i];
            }
            for (int i = 0; i < n; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
            _finalW?.WriteAudio(mix, _samples);
            _samples = target;
        }
        // Sound made while paused is thrown away, so it does not play after Resume.
        if (paused)
            foreach (var t in _tracks)
                while (t.Source != null && t.Source.Read(_drain) > 0) { }
    }

    void Note(string text)
    {
        lock (_gate)
        {
            _notes.Add(text);
            WriteSession();
        }
    }

    void WriteSession()
    {
        var kind = _o.Target switch { WindowTarget => "window", RegionTarget => "region", _ => "monitor" };
        var file = new SessionFile(1, _startedAt, _size, _rate, kind, _sources, [.. _layoutChanges], _clock.Pauses, _dropped, [.. _notes]);
        var path = Path.Combine(_folder, "session.json");
        try
        {
            File.WriteAllText(path + ".tmp", SessionJson.Serialize(file));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { } // The writers report a full disk, and the last good session.json stays in place.
    }

    // Every writer and source is closed, even when another one fails, so each file keeps what it has written.
    void DisposeAll()
    {
        Quietly(() => _screenW?.Dispose());
        Quietly(() => _finalW?.Dispose());
        Quietly(() => _camW?.Dispose());
        foreach (var t in _tracks)
        {
            Quietly(() => t.Wav?.Dispose());
            Quietly(() => t.Source?.DisposeAsync().AsTask().Wait());
        }
        Quietly(() => _cam?.DisposeAsync().AsTask().Wait());
        Quietly(() => _screen?.Dispose());
        Quietly(() => _compositor?.Dispose());
        _black?.Dispose();
        _camTarget?.Dispose();
    }

    void Quietly(Action a)
    {
        try { a(); }
        catch (IOException e)
        {
            lock (_gate)
                if (!_diskNoted)
                {
                    _diskNoted = true;
                    _notes.Add($"disk error while saving: {e.Message}");
                }
        }
        catch (Exception) { }
    }
}
