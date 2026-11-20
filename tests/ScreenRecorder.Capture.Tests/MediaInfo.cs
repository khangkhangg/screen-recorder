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
        bool constant = video.Count > 1 && video.Zip(video.Skip(1), (a, b) => b.Time - a.Time)
            .All(d => Math.Abs(d - (video[1].Time - video[0].Time)) <= 1);
        return new(Length(video), video.Count, (int)(size >> 32), (int)(size & 0xFFFFFFFF), constant, Length(audio));
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
