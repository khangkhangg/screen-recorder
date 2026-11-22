using Vortice.MediaFoundation;

namespace ScreenRecorder.Capture.Tests;

public record MediaInfo(TimeSpan Duration, int FrameCount, int Width, int Height, bool ConstantFrameRate, TimeSpan AudioDuration)
{
    static MediaInfo() => MediaFactory.MFStartup();

    // Reads the compressed samples without decoding them, so it is fast on long files.
    public static MediaInfo Read(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        using var videoType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
        ulong size = videoType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        var video = Samples(reader, SourceReaderIndex.FirstVideoStream);
        var audio = HasAudio(reader) ? Samples(reader, SourceReaderIndex.FirstAudioStream) : [];
        video.Sort();
        // Repeated frames are one longer sample, so frames are counted along the timeline and starts are checked against the frame grid.
        ulong rate = videoType.GetUInt64(MediaTypeAttributeKeys.FrameRate);
        double frame = 10_000_000.0 * (uint)rate / (rate >> 32);
        // The MP4 stores times in units of about 167 ticks at 60 fps, so a 1 tick tolerance fails on good files.
        for (int i = 1; i < video.Count; i++)
            if (Math.Abs(video[i - 1].Time + video[i - 1].Duration - video[i].Time) > frame / 100)
                throw new InvalidDataException($"{path}: video sample {i} starts at {video[i].Time}, not where sample {i - 1} ends.");
        bool constant = video.Count > 1 && video.All(s =>
        {
            double k = (s.Time - video[0].Time) / frame;
            return Math.Abs(k - Math.Round(k)) * frame <= 1;
        });
        int frames = (int)Math.Round(Length(video).Ticks / frame);
        return new(Length(video), frames, (int)(size >> 32), (int)(size & 0xFFFFFFFF), constant, Length(audio));
    }

    static bool HasAudio(IMFSourceReader reader)
    {
        try { reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0).Dispose(); return true; }
        catch (SharpGen.Runtime.SharpGenException) { return false; }
    }

    static List<(long Time, long Duration)> Samples(IMFSourceReader reader, SourceReaderIndex stream)
    {
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(stream, true);
        reader.SetCurrentPosition(0);
        var list = new List<(long, long)>();
        while (true)
        {
            using var sample = reader.ReadSample(stream, 0, out _, out var flags, out _);
            if (sample is not null) list.Add((sample.SampleTime, sample.SampleDuration));
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) return list;
        }
    }

    static TimeSpan Length(List<(long Time, long Duration)> s) =>
        s.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(s.Max(x => x.Time + x.Duration) - s.Min(x => x.Time));
}
