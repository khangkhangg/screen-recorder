namespace ScreenRecorder.Core;

public sealed class RecordingClock(int frameRate, Func<TimeSpan> now)
{
    readonly object gate = new();
    readonly List<PauseEntry> pauses = [];
    TimeSpan? start, pausedAt;
    TimeSpan pausedTotal;

    public bool IsPaused { get { lock (gate) return pausedAt is not null; } }
    public TimeSpan MediaTime { get { lock (gate) return Media(); } }
    public long FramesDue { get { lock (gate) return Media().Ticks * frameRate / TimeSpan.TicksPerSecond; } }
    public IReadOnlyList<PauseEntry> Pauses { get { lock (gate) return [.. pauses]; } }

    public long SamplesDue(int sampleRate) { lock (gate) return Media().Ticks * sampleRate / TimeSpan.TicksPerSecond; }

    public void Start() { lock (gate) start ??= now(); }

    public void Pause() { lock (gate) { if (start is null || pausedAt is not null) return; pausedAt = now(); } }

    public void Resume()
    {
        lock (gate)
        {
            if (pausedAt is not { } p) return;
            var d = now() - p;
            pauses.Add(new PauseEntry((p - start!.Value - pausedTotal).TotalSeconds, d.TotalSeconds));
            pausedTotal += d;
            pausedAt = null;
        }
    }

    TimeSpan Media() => start is null ? TimeSpan.Zero : (pausedAt ?? now()) - start.Value - pausedTotal;
}
