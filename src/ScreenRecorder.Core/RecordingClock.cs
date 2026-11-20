namespace ScreenRecorder.Core;

public sealed class RecordingClock(int frameRate, Func<TimeSpan> now)
{
    readonly List<PauseEntry> pauses = [];
    TimeSpan? start, pausedAt;
    TimeSpan pausedTotal;

    public bool IsPaused => pausedAt is not null;
    public TimeSpan MediaTime => start is null ? TimeSpan.Zero : (pausedAt ?? now()) - start.Value - pausedTotal;
    public long FramesDue => (long)Math.Floor(MediaTime.TotalSeconds * frameRate);
    public IReadOnlyList<PauseEntry> Pauses => pauses;

    public long SamplesDue(int sampleRate) => (long)Math.Floor(MediaTime.TotalSeconds * sampleRate);

    public void Start() => start = now();

    public void Pause()
    {
        if (start is null || IsPaused) return;
        pausedAt = now();
    }

    public void Resume()
    {
        if (pausedAt is not { } p) return;
        var d = now() - p;
        pauses.Add(new PauseEntry((p - start!.Value - pausedTotal).TotalSeconds, d.TotalSeconds));
        pausedTotal += d;
        pausedAt = null;
    }
}
