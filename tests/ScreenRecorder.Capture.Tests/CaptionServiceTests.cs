using System.Diagnostics;
using System.Net;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture.Tests;

public class CaptionServiceTests
{
    // The helper, models and jfk.wav downloaded once for the design research. No test downloads anything.
    internal static readonly string? Research = FindResearch();

    static string? FindResearch()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".superpowers", "research", "whisper-test", "bin")))
                return Path.Combine(d.FullName, ".superpowers", "research", "whisper-test");
        return null;
    }

    // Answers from bytes in memory or from files on disk, never from the network.
    sealed class FakeHttp(Func<string, byte[]> body) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(request.RequestUri.ToString())) });
        }
    }

    static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "captions-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task A_file_with_the_wrong_checksum_is_deleted()
    {
        var folder = TempFolder();
        try
        {
            var http = new FakeHttp(_ => [1, 2, 3]);
            var service = new CaptionService(folder, http);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(CaptionChoice.English, new Progress<double>(), default));
            Assert.Equal([CaptionMath.Helper.Url], http.Urls);
            Assert.Empty(Directory.GetFiles(folder));
            Assert.False(service.IsReady(CaptionChoice.English));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_file_larger_than_listed_is_deleted()
    {
        var folder = TempFolder();
        try
        {
            var service = new CaptionService(folder, new FakeHttp(_ => new byte[CaptionMath.Helper.Size + 1]));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(CaptionChoice.English, new Progress<double>(), default));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [ResearchFact]
    public async Task Matching_files_are_kept_and_the_helper_is_unpacked()
    {
        var folder = TempFolder();
        try
        {
            var http = new FakeHttp(url => File.ReadAllBytes(url.EndsWith(".zip")
                ? Path.Combine(Research!, "bin", "whisper-bin-x64.zip")
                : Path.Combine(Research!, "models", url[(url.LastIndexOf('/') + 1)..])));
            var service = new CaptionService(folder, http);
            double last = 0;
            await service.DownloadAsync(CaptionChoice.English, new SyncProgress<double>(p => last = p), default);
            Assert.Equal(1, last);
            Assert.True(service.IsReady(CaptionChoice.English));
            Assert.False(service.IsReady(CaptionChoice.Any));
            var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToHashSet();
            Assert.Contains("whisper.dll", names);
            Assert.Contains("LICENSE.txt", names);
            Assert.DoesNotContain("parakeet.dll", names);
            Assert.DoesNotContain(names, n => n!.EndsWith(".tmp") || n.EndsWith(".zip"));

            // The second model needs only its own download.
            http.Urls.Clear();
            await service.DownloadAsync(CaptionChoice.Any, new Progress<double>(), default);
            Assert.Equal([CaptionMath.AnyModel.Url], http.Urls);
        }
        finally { Directory.Delete(folder, true); }
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // A ready caption folder made from the research copy, without the network.
    static string ReadyFolder()
    {
        var folder = TempFolder();
        foreach (var f in Directory.GetFiles(Path.Combine(Research!, "bin", "Release")).Where(f => CaptionMath.Keep(Path.GetFileName(f))))
            File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
        File.Copy(Path.Combine(Research!, "models", CaptionMath.EnglishModel.Name), Path.Combine(folder, CaptionMath.EnglishModel.Name));
        return folder;
    }

    // jfk.wav is 16 kHz mono 16-bit. The recorder writes 48 kHz stereo float.
    static void WriteRecordedCopy(string path)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Research!, "audio", "jfk.wav"));
        var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(44));
        var stereo = new float[pcm.Length * 6];
        for (int i = 0; i < stereo.Length; i++) stereo[i] = pcm[i / 6] / 32768f;
        using var w = new WavWriter(path);
        w.Write(stereo);
    }

    [ResearchFact]
    public async Task Captions_are_made_from_a_recorded_track()
    {
        var data = ReadyFolder();
        var recording = TempFolder();
        try
        {
            WriteRecordedCopy(Path.Combine(recording, "mic.wav"));
            var service = new CaptionService(data);
            Assert.True(service.IsReady(CaptionChoice.English));
            var percents = new List<int>();
            var clock = Stopwatch.StartNew();
            await service.RunAsync(recording, CaptionChoice.English, new SyncProgress<int>(percents.Add), default);
            Console.WriteLine($"caption job took {clock.Elapsed.TotalSeconds:0.0} s");
            Console.WriteLine(File.ReadAllText(Path.Combine(recording, "screen.srt")));
            Assert.Contains("your country", File.ReadAllText(Path.Combine(recording, "screen.srt")));
            Assert.Contains("your country", File.ReadAllText(Path.Combine(recording, "screen.vtt")));
            Assert.Equal(100, percents.Last());
            Assert.Equal(["mic.wav", "screen.srt", "screen.vtt"], Directory.GetFiles(recording).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(data, true);
            Directory.Delete(recording, true);
        }
    }

    [ResearchFact]
    public async Task A_stopped_job_leaves_no_files()
    {
        var data = ReadyFolder();
        var recording = TempFolder();
        try
        {
            WriteRecordedCopy(Path.Combine(recording, "system.wav"));
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CaptionService(data).RunAsync(recording, CaptionChoice.English, new Progress<int>(), stop.Token));
            Assert.Equal(["system.wav"], Directory.GetFiles(recording).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(data, true);
            Directory.Delete(recording, true);
        }
    }

    [Fact]
    public void A_failure_note_is_added_to_the_session_file()
    {
        var recording = TempFolder();
        try
        {
            var path = Path.Combine(recording, "session.json");
            var file = new SessionFile(1, DateTimeOffset.Now, new(1280, 720), 60, "monitor", [], [], [], 0, ["mic lost at 1.0 s"]);
            File.WriteAllText(path, SessionJson.Serialize(file));
            CaptionService.AddNote(recording, "captions failed: test");
            Assert.Equal(["mic lost at 1.0 s", "captions failed: test"], SessionJson.Deserialize(File.ReadAllText(path)).Notes);
        }
        finally { Directory.Delete(recording, true); }
    }

    [Fact]
    public void Only_a_track_with_sound_counts()
    {
        var recording = TempFolder();
        try
        {
            Assert.Null(CaptionService.TrackIn(recording));
            File.WriteAllBytes(Path.Combine(recording, "mic.wav"), new byte[44]);
            File.WriteAllBytes(Path.Combine(recording, "system.wav"), new byte[100]);
            Assert.Equal("system.wav", CaptionService.TrackIn(recording));
        }
        finally { Directory.Delete(recording, true); }
    }
}

sealed class ResearchFactAttribute : FactAttribute
{
    public ResearchFactAttribute()
    {
        if (CaptionServiceTests.Research is null) Skip = "The whisper test folder .superpowers/research/whisper-test is missing.";
    }
}
