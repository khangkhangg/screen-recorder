using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRecorder.Core;

public record SourceEntry(string File, string Kind, double OffsetSeconds);
public record LayoutChange(double AtSeconds, Layout Layout);
public record PauseEntry(double AtSeconds, double DurationSeconds);

public record SessionFile(int Version, DateTimeOffset StartedAt, PixelSize FrameSize, int FrameRate, string TargetKind,
    IReadOnlyList<SourceEntry> Sources, IReadOnlyList<LayoutChange> LayoutChanges, IReadOnlyList<PauseEntry> Pauses,
    long DroppedFrames, IReadOnlyList<string> Notes);

public static class SessionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(SessionFile f) => JsonSerializer.Serialize(f, Options);

    public static SessionFile Deserialize(string json)
    {
        if (JsonDocument.Parse(json).RootElement.TryGetProperty("version", out var v) && v.GetInt32() > 1)
            throw new NotSupportedException($"Session version {v} is newer than supported.");
        return JsonSerializer.Deserialize<SessionFile>(json, Options)!;
    }
}
