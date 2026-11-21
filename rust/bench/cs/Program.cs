// Helper for compare.ps1: runs the C# engine, reads recorded files and shows test windows.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenRecorder.Capture;
using ScreenRecorder.Capture.Tests;
using ScreenRecorder.Core;
using Vortice.MediaFoundation;

static class Program
{
    [STAThread]
    static int Main(string[] a)
    {
        switch (a[0])
        {
            case "record": return Record(a).GetAwaiter().GetResult();
            case "probe": Print(Probe(a[1])); return 0;
            case "pixels": Print(Pixels(a[1], double.Parse(a[2]), a[3..])); return 0;
            case "firstwhite": Print(new { seconds = FirstWhite(a[1], a[2]) }); return 0;
            case "onset": Print(new { seconds = Onset(a[1]) }); return 0;
            case "sizes": Print(Sizes(a[1])); return 0;
            case "logo": Logo(a[1]); return 0;
            case "window": Window(a[1], a[2..6].Select(int.Parse).ToArray(), a.Length > 6 ? a[6] : null); return 0;
            case "stoptime": return StopTime(a[1..]);
        }
        Console.Error.WriteLine($"Unknown mode {a[0]}");
        return 2;
    }

    static void Print(object o) => Console.WriteLine(JsonSerializer.Serialize(o));

    // record OUT TARGET FPS [mic] [sys] [logo=PATH], TARGET is monitor, region:X,Y,W,H or window:TITLE.
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int n; public nint d; public int inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string? r, desk, title; public int x, y, w, h, cx, cy, fill, flags; public short show, res2; public nint res2p, hIn, hOut, hErr; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public nint hProcess, hThread; public int pid, tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? app, string cmd, nint pa, nint ta, bool inherit, uint flags, nint env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint h, out uint code);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();

    // stoptime EXE OUTDIR SECONDS REGION FPS: runs screenrec in its own process group, records SECONDS,
    // sends Ctrl+Break (as the tool's own test does) and times the stop. Files are watched every 20 ms.
    static int StopTime(string[] a)
    {
        var (exe, dir, seconds, region, fps) = (a[0], a[1], double.Parse(a[2]), a[3], a[4]);
        var errPath = Path.Combine(dir, "..", "stderr.txt");
        var outPath = Path.Combine(dir, "..", "stdout.txt");
        var sa = new SECURITY_ATTRIBUTES { n = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), inherit = 1 };
        var hIn = CreateFileW("NUL", 0x80000000, 3, ref sa, 3, 0, 0);
        var hOut = CreateFileW(outPath, 0x40000000, 3, ref sa, 2, 0, 0);
        var hErr = CreateFileW(errPath, 0x40000000, 3, ref sa, 2, 0, 0);
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), flags = 0x100, hIn = hIn, hOut = hOut, hErr = hErr };
        var cmd = $"\"{exe}\" record --region {region} --fps {fps} --system-audio --out \"{dir}\"";
        if (!CreateProcessW(null, cmd, 0, 0, true, 0x200, 0, null, ref si, out var pi)) throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!(File.Exists(errPath) && ReadShared(errPath).Contains("recording")))
        {
            if (WaitForSingleObject(pi.hProcess, 20) == 0) throw new InvalidOperationException("The engine ended before recording: " + ReadShared(errPath));
        }
        var startMs = started.Elapsed.TotalMilliseconds;
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var ok = GenerateConsoleCtrlEvent(1, (uint)pi.pid);
        if (!ok) { Console.Error.WriteLine("Ctrl+Break failed: " + Marshal.GetLastWin32Error()); return 3; }
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var seen = new Dictionary<string, (long len, double first, double last)>();
        while (true)
        {
            var now = t0.Elapsed.TotalMilliseconds;
            foreach (var f in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
            {
                long len;
                try { f.Refresh(); len = f.Length; } catch (IOException) { continue; } // the tool renames session.json.tmp
                seen[f.Name] = !seen.TryGetValue(f.Name, out var s) ? (len, now, now) : len != s.len ? (len, s.first, now) : s;
            }
            if (WaitForSingleObject(pi.hProcess, 20) == 0) break;
        }
        var exitMs = t0.Elapsed.TotalMilliseconds;
        GetExitCodeProcess(pi.hProcess, out var code);
        Print(new
        {
            exitCode = code, stopMs = Math.Round(exitMs, 1), startMs = Math.Round(startMs, 0), seconds,
            stdout = ReadShared(outPath).Trim(),
            files = seen.OrderBy(k => k.Value.last).Select(k => new { name = k.Key, bytes = k.Value.len, firstSeenMs = Math.Round(k.Value.first), lastGrewMs = Math.Round(k.Value.last) }).ToArray(),
        });
        return 0;
    }

    static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    // Same protocol as screenrec: "recording" on stderr, "stop" on stdin, one JSON line on stdout.
    static async Task<int> Record(string[] a)
    {
        var target = Target(a[2]);
        var logo = a.FirstOrDefault(x => x.StartsWith("logo="))?[5..];
        var d = Layout.Default;
        var layout = d with
        {
            Camera = d.Camera with { Visible = false },
            Logo = logo is null ? d.Logo : d.Logo with { Visible = true, Path = logo },
        };
        var o = new RecordingOptions(target, a[1], int.Parse(a[3]), null, a.Contains("mic"), null, a.Contains("sys"), layout, true);
        var p = await RecordingSession.PreflightAsync(o);
        if (p.Encoders == EncoderSupport.TooFewHardwareSessions) o = o with { WriteFinal = false };
        await using var s = await RecordingSession.StartAsync(o, p);
        Console.Error.WriteLine("recording");
        var stop = new TaskCompletionSource();
        s.StoppedByItself += _ => stop.TrySetResult();
        new Thread(() =>
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
                if (line.Trim() == "stop") break;
            stop.TrySetResult();
        }) { IsBackground = true }.Start();
        await stop.Task;
        var seconds = s.Elapsed.TotalSeconds;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await s.StopAsync();
        Print(new { folder = r.Folder, droppedFrames = r.DroppedFrames, notes = r.Notes, frameRate = p.FrameRate, seconds, stopSeconds = sw.Elapsed.TotalSeconds });
        return 0;
    }

    static CaptureTarget Target(string t)
    {
        if (t == "monitor") return new MonitorTarget(CaptureTarget.PrimaryMonitor());
        if (t.StartsWith("window:"))
        {
            var hwnd = FindWindow(null, t[7..]);
            if (hwnd == 0) throw new InvalidOperationException($"No window {t[7..]}");
            return new WindowTarget(hwnd);
        }
        var n = t[7..].Split(',').Select(int.Parse).ToArray();
        return new RegionTarget(CaptureTarget.PrimaryMonitor(), n[0], n[1], n[2], n[3]);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindow(string? cls, string title);

    static object Probe(string path)
    {
        if (path.EndsWith(".wav")) return new { duration = WavSeconds(path) };
        var m = MediaInfo.Read(path);
        return new
        {
            duration = m.Duration.TotalSeconds, frameCount = m.FrameCount, width = m.Width, height = m.Height,
            constantFrameRate = m.ConstantFrameRate, audioDuration = m.AudioDuration.TotalSeconds,
        };
    }

    // Compressed sample sizes in bytes with their times, read without decoding.
    static object Sizes(string path)
    {
        MediaFactory.MFStartup();
        using var r = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        r.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        r.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        var list = new List<object>();
        while (true)
        {
            using var s = r.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out _);
            if (s != null) list.Add(new[] { s.SampleTime / 1e7, s.TotalLength });
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) return list;
        }
    }

    // Decoded frames as top-down BGRA. Media Foundation's video processor turns the H.264
    // limited-range YUV into full-range RGB32.
    static IEnumerable<(double Time, byte[] Bgra, int Width)> Frames(string path)
    {
        MediaFactory.MFStartup();
        using var attr = MediaFactory.MFCreateAttributes(1);
        attr.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        using var r = MediaFactory.MFCreateSourceReaderFromURL(path, attr);
        r.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        r.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var t = MediaFactory.MFCreateMediaType())
        {
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            r.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, t);
        }
        using var cur = r.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
        // A negative stride is a bottom-up image.
        int stride;
        try { stride = (int)cur.GetUInt32(MediaTypeAttributeKeys.DefaultStride); } catch (SharpGen.Runtime.SharpGenException) { stride = w * 4; }
        while (true)
        {
            using var s = r.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out _);
            if (s != null)
            {
                using var buf = s.ConvertToContiguousBuffer();
                buf.Lock(out var data, out _, out _);
                var scan0 = stride < 0 ? data + (nint)(h - 1) * -stride : data;
                var bgra = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(scan0 + (nint)y * stride, bgra, y * w * 4, w * 4);
                buf.Unlock();
                yield return (s.SampleTime / 1e7, bgra, w);
            }
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) yield break;
        }
    }

    static (int X, int Y) Point(string p) { var n = p.Split(',').Select(int.Parse).ToArray(); return (n[0], n[1]); }

    static int[] Pixel((double, byte[] Bgra, int Width) f, (int X, int Y) p) =>
        f.Bgra.Skip((p.Y * f.Width + p.X) * 4).Take(4).Select(b => (int)b).ToArray();

    // BGRA of each X,Y point on the first frame at or after the given time.
    static object Pixels(string path, double time, string[] points)
    {
        var f = Frames(path).First(f => f.Time >= time);
        return new { time = f.Time, pixels = points.Select(p => new { at = p, bgra = Pixel(f, Point(p)) }) };
    }

    static double? FirstWhite(string path, string point)
    {
        var p = Point(point);
        foreach (var f in Frames(path))
            if (Pixel(f, p).Take(3).All(c => c > 200)) return f.Time;
        return null;
    }

    static (int Channels, int Rate, long Offset, long Bytes) Wav(BinaryReader r)
    {
        int channels = 0, rate = 0;
        r.BaseStream.Position = 12;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var id = new string(r.ReadChars(4));
            long len = r.ReadUInt32();
            if (id == "fmt ") { r.ReadUInt16(); channels = r.ReadUInt16(); rate = r.ReadInt32(); r.BaseStream.Position += len - 8; }
            else if (id == "data")
            {
                // A killed recording can leave the size unwritten, so the file length decides.
                var left = r.BaseStream.Length - r.BaseStream.Position;
                return (channels, rate, r.BaseStream.Position, len == 0 || len > left ? left : len);
            }
            else r.BaseStream.Position += len;
        }
        return (channels, rate, 0, 0);
    }

    static double WavSeconds(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        var w = Wav(r);
        return w.Bytes / (4.0 * w.Channels) / w.Rate;
    }

    // Seconds to the first sample louder than a quarter of the loudest one (32-bit float WAV).
    static double? Onset(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        var w = Wav(r);
        r.BaseStream.Position = w.Offset;
        var s = new float[w.Bytes / 4];
        for (int i = 0; i < s.Length; i++) s[i] = r.ReadSingle();
        var peak = s.Max(Math.Abs);
        if (peak < 0.01f) return null;
        var i0 = Array.FindIndex(s, x => Math.Abs(x) > peak / 4);
        return (double)(i0 / w.Channels) / w.Rate;
    }

    static void Logo(string path)
    {
        using var b = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(new SolidBrush(Color.FromArgb(200, 255, 140, 0)), 16, 16, 224, 224);
        }
        b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // window MODE X Y W H in screen pixels. Modes: busy (redraws random shapes about 60 times
    // a second), patch (gray 128 left half, white right half), flash (black, turns white and
    // beeps on the line "flash" on stdin). Prints "ready" and runs until killed.
    static void Window(string mode, int[] r, string? log)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var f = new BenchForm(mode, log) { Bounds = new Rectangle(r[0], r[1], r[2], r[3]) };
        f.Shown += (_, _) => Console.WriteLine("ready");
        Application.Run(f);
    }
}

sealed class BenchForm : Form
{
    int _painted;
    readonly string _mode;
    readonly Random _rng = new(1);
    readonly SoundPlayer _beep = new(Beep());

    public BenchForm(string mode, string? log = null)
    {
        _mode = mode;
        if (log != null)
        {
            // One line per second: paints started, the real length of that second, and the end time in UTC ticks.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += (_, _) =>
            {
                File.AppendAllText(log, $"{_painted} {clock.Elapsed.TotalSeconds:F3} {DateTime.UtcNow.Ticks}\n");
                _painted = 0;
                clock.Restart();
            };
            tick.Start();
        }
        Text = $"screenrec-bench-{Environment.ProcessId}";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        if (mode == "busy")
        {
            var t = new System.Windows.Forms.Timer { Interval = 15 };
            t.Tick += (_, _) => Invalidate();
            t.Start();
        }
        if (mode == "flash")
        {
            _beep.Load();
            new Thread(() =>
            {
                while (Console.ReadLine() is { } line)
                    if (line.Trim() == "flash") BeginInvoke(Flash);
            }) { IsBackground = true }.Start();
        }
    }

    protected override bool ShowWithoutActivation => true;

    void Flash()
    {
        BackColor = Color.White;
        Refresh();
        _beep.Play();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var (w, h) = (ClientSize.Width, ClientSize.Height);
        if (_mode == "patch")
        {
            g.FillRectangle(new SolidBrush(Color.FromArgb(128, 128, 128)), 0, 0, w / 2, h);
            g.FillRectangle(Brushes.White, w / 2, 0, w - w / 2, h);
        }
        if (_mode != "busy") return;
        _painted++;
        using var font = new Font("Consolas", 28);
        for (int i = 0; i < 300; i++)
        {
            using var b = new SolidBrush(Color.FromArgb(_rng.Next(256), _rng.Next(256), _rng.Next(256)));
            g.FillRectangle(b, _rng.Next(w), _rng.Next(h), _rng.Next(20, w / 6), _rng.Next(20, h / 6));
            if (i % 10 == 0) g.DrawString($"{_rng.Next():X8} {DateTime.Now:HH:mm:ss.fff}", font, Brushes.White, _rng.Next(w), _rng.Next(h));
        }
    }

    // 0.5 seconds of a loud 1 kHz tone, 48 kHz 16-bit stereo.
    static MemoryStream Beep()
    {
        const int rate = 48000, n = rate / 2;
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        w.Write("RIFF"u8); w.Write(36 + n * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(n * 4);
        for (int i = 0; i < n; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 26000);
            w.Write(v); w.Write(v);
        }
        m.Position = 0;
        return m;
    }
}
