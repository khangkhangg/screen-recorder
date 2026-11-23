using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace ScreenRecorder.Core;

public enum CaptionChoice { Off, English, Any }

public record CaptionFile(string Name, string Url, long Size, string Sha256);

public static partial class CaptionMath
{
    public const string Cli = "whisper-cli.exe";

    public static readonly CaptionFile Helper = new("whisper-bin-x64.zip",
        "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip", 8928640,
        "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb");

    public static readonly CaptionFile EnglishModel = new("ggml-base.en-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q5_1.bin", 59721011,
        "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f");

    public static readonly CaptionFile AnyModel = new("ggml-base-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin", 59707625,
        "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898");

    public static CaptionFile Model(CaptionChoice c) => c == CaptionChoice.English ? EnglishModel : AnyModel;

    public static CaptionFile[] Downloads(CaptionChoice c) => [Helper, Model(c)];

    // The zip holds more programs than the helper needs.
    public static bool Keep(string zipEntry) =>
        zipEntry is Cli or "whisper.dll" or "ggml.dll" or "ggml-base.dll"
        || zipEntry.StartsWith("ggml-cpu-") && zipEntry.EndsWith(".dll");

    // Captions come from the microphone, and from the computer sound when there is no microphone.
    public static string? Track(IReadOnlyCollection<string> soundFiles) =>
        soundFiles.Contains("mic.wav") ? "mic.wav" : soundFiles.Contains("system.wav") ? "system.wav" : null;

    // Same name as the video the card plays, so players load the captions.
    public static string OutputName(bool hasFinal) => hasFinal ? "final" : "screen";

    // One caption line of at most 42 characters, split between words.
    public static string[] Arguments(string model, string wav, string output, CaptionChoice c) =>
        ["-m", model, "-f", wav, "-t", "4", "-l", c == CaptionChoice.English ? "en" : "auto",
         "-osrt", "-ovtt", "-of", output, "-pp", "-ml", "42", "-sow"];

    [GeneratedRegex(@"progress =\s*(\d+)%")]
    private static partial Regex ProgressLine();

    public static int? Progress(string? line) =>
        line is not null && ProgressLine().Match(line) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    // 48 kHz stereo float to 16 kHz mono 16-bit: averages each 3 frames of both channels.
    public static short[] ToMono16k(ReadOnlySpan<float> stereo)
    {
        var mono = new short[stereo.Length / 6];
        for (int i = 0; i < mono.Length; i++)
        {
            var s = stereo.Slice(i * 6, 6);
            var v = (s[0] + s[1] + s[2] + s[3] + s[4] + s[5]) / 6;
            mono[i] = (short)(Math.Clamp(v, -1f, 1f) * short.MaxValue);
        }
        return mono;
    }

    public static byte[] Wav16kHeader(long samples)
    {
        var h = new byte[44];
        var data = (int)(samples * 2);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), data);
        return h;
    }

    public const string License = """
        whisper.cpp (the helper program, https://github.com/ggml-org/whisper.cpp)
        MIT License, Copyright (c) 2023-2024 The ggml authors

        Whisper models (the speech model, https://github.com/openai/whisper)
        MIT License, Copyright (c) 2022 OpenAI

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;
}

// One caption job at a time, and none during a countdown or a recording.
public sealed class CaptionQueue
{
    readonly List<string> _waiting = [];
    bool _paused;

    public string? Running { get; private set; }

    public void Add(string folder) => _waiting.Add(folder);

    // Returns the job to stop. It goes back to the front of the line.
    public string? Pause()
    {
        _paused = true;
        if (Running is not { } r) return null;
        _waiting.Insert(0, r);
        Running = null;
        return r;
    }

    public void Resume() => _paused = false;

    public string? Next()
    {
        if (_paused || Running is not null || _waiting.Count == 0) return null;
        Running = _waiting[0];
        _waiting.RemoveAt(0);
        return Running;
    }

    public void Done() => Running = null;
}
