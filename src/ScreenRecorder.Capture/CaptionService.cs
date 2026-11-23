using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

// Downloads whisper.cpp and a model into `folder` once, then writes captions next to recordings.
public sealed class CaptionService(string folder, HttpMessageHandler? handler = null)
{
    public string Folder => folder;

    bool Have(CaptionFile f) => File.Exists(Path.Combine(folder, f == CaptionMath.Helper ? CaptionMath.Cli : f.Name));

    public bool IsReady(CaptionChoice c) => CaptionMath.Downloads(c).All(Have);

    public CaptionFile[] Missing(CaptionChoice c) => [.. CaptionMath.Downloads(c).Where(f => !Have(f))];

    // Progress goes from 0 to 1 over all missing files. A file that does not match its SHA-256 is deleted.
    public async Task DownloadAsync(CaptionChoice c, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var files = Missing(c);
        long total = files.Sum(f => f.Size), done = 0;
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        var buffer = new byte[81920];
        foreach (var f in files)
        {
            var tmp = Path.Combine(folder, f.Name + ".tmp");
            try
            {
                using (var response = await http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var target = File.Create(tmp);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long written = 0;
                    for (int n; (n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0;)
                    {
                        if ((written += n) > f.Size) throw new InvalidDataException($"{f.Name} is larger than expected, so it was deleted.");
                        sha.AppendData(buffer, 0, n);
                        await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        progress.Report((double)(done += n) / total);
                    }
                    if (Convert.ToHexStringLower(sha.GetHashAndReset()) != f.Sha256)
                        throw new InvalidDataException($"{f.Name} did not match its checksum, so it was deleted.");
                }
                if (f == CaptionMath.Helper) Unpack(tmp);
                else File.Move(tmp, Path.Combine(folder, f.Name), true);
            }
            finally { File.Delete(tmp); }
        }
        File.WriteAllText(Path.Combine(folder, "LICENSE.txt"), CaptionMath.License);
    }

    // whisper-cli.exe goes last, so its presence means the helper is complete.
    void Unpack(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries.Where(e => CaptionMath.Keep(e.Name)).OrderBy(e => e.Name == CaptionMath.Cli))
            e.ExtractToFile(Path.Combine(folder, e.Name), true);
    }

    public static string? TrackIn(string recording) => CaptionMath.Track(
        [.. new[] { "mic.wav", "system.wav" }.Where(n => new FileInfo(Path.Combine(recording, n)) is { Exists: true, Length: > 44 })]);

    // Writes final.srt and final.vtt (or screen.*) into the recording folder. Stopping kills the helper.
    public async Task RunAsync(string recording, CaptionChoice c, IProgress<int> progress, CancellationToken ct)
    {
        var track = TrackIn(recording) ?? throw new FileNotFoundException("The recording has no sound track.");
        var wav = Path.Combine(recording, "captions.tmp.wav");
        var output = Path.Combine(recording, "captions.tmp");
        var name = CaptionMath.OutputName(File.Exists(Path.Combine(recording, "final.mp4")));
        try
        {
            await Task.Run(() => Resample(Path.Combine(recording, track), wav, ct), ct).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(folder, CaptionMath.Cli))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var a in CaptionMath.Arguments(Path.Combine(folder, CaptionMath.Model(c).Name), wav, output, c)) start.ArgumentList.Add(a);
            string? last = null;
            using var p = new Process { StartInfo = start };
            p.ErrorDataReceived += (_, e) => { if (CaptionMath.Progress(e.Data) is { } n) progress.Report(n); else if (e.Data is { Length: > 0 }) last = e.Data; };
            p.OutputDataReceived += (_, _) => { };
            p.Start();
            using (ct.Register(() => { try { p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { } }))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0) throw new IOException($"The caption helper stopped with code {p.ExitCode}: {last}");
            foreach (var ext in new[] { ".srt", ".vtt" }) File.Move(output + ext, Path.Combine(recording, name + ext), true);
        }
        finally
        {
            foreach (var f in new[] { wav, output + ".srt", output + ".vtt" }) File.Delete(f);
        }
    }

    // The WavWriter track is 48 kHz stereo float with a 44-byte header.
    static void Resample(string source, string target, CancellationToken ct)
    {
        using var input = File.OpenRead(source);
        using var output = File.Create(target);
        input.Position = 44;
        output.Write(new byte[44]);
        var bytes = new byte[48000 * 8];
        long samples = 0;
        for (int n; (n = input.ReadAtLeast(bytes, bytes.Length, false)) > 0;)
        {
            ct.ThrowIfCancellationRequested();
            var mono = CaptionMath.ToMono16k(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, n - n % 24)));
            output.Write(MemoryMarshal.AsBytes(mono.AsSpan()));
            samples += mono.Length;
        }
        output.Position = 0;
        output.Write(CaptionMath.Wav16kHeader(samples));
    }

    public static void AddNote(string recording, string note)
    {
        var path = Path.Combine(recording, "session.json");
        if (!File.Exists(path)) return;
        var f = SessionJson.Deserialize(File.ReadAllText(path));
        File.WriteAllText(path, SessionJson.Serialize(f with { Notes = [.. f.Notes, note] }));
    }
}
