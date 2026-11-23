using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class CaptionTests
{
    [Theory]
    [InlineData(new[] { "mic.wav", "system.wav" }, "mic.wav")]
    [InlineData(new[] { "system.wav" }, "system.wav")]
    [InlineData(new string[0], null)]
    public void Microphone_wins_then_computer_sound(string[] files, string? track) =>
        Assert.Equal(track, CaptionMath.Track(files));

    [Fact]
    public void Captions_take_the_name_of_the_played_video()
    {
        Assert.Equal("final", CaptionMath.OutputName(true));
        Assert.Equal("screen", CaptionMath.OutputName(false));
    }

    [Fact]
    public void Each_choice_has_its_model_and_language()
    {
        Assert.Equal("ggml-base.en-q5_1.bin", CaptionMath.Model(CaptionChoice.English).Name);
        Assert.Equal("ggml-base-q5_1.bin", CaptionMath.Model(CaptionChoice.Any).Name);
        Assert.Equal(["whisper-bin-x64.zip", "ggml-base-q5_1.bin"], CaptionMath.Downloads(CaptionChoice.Any).Select(d => d.Name));
        var args = CaptionMath.Arguments("m.bin", "a.wav", "out", CaptionChoice.English);
        Assert.Equal("en", args[Array.IndexOf(args, "-l") + 1]);
        Assert.Equal("auto", CaptionMath.Arguments("m", "a", "o", CaptionChoice.Any)[7]);
        Assert.Contains("-osrt", args);
        Assert.Contains("-ovtt", args);
        Assert.Equal("out", args[Array.IndexOf(args, "-of") + 1]);
        foreach (var d in CaptionMath.Downloads(CaptionChoice.English))
        {
            Assert.StartsWith("https://", d.Url);
            Assert.Matches("^[0-9a-f]{64}$", d.Sha256);
        }
    }

    [Theory]
    [InlineData("whisper-cli.exe", true)]
    [InlineData("ggml-cpu-haswell.dll", true)]
    [InlineData("ggml-base.dll", true)]
    [InlineData("parakeet.dll", false)]
    [InlineData("main.exe", false)]
    public void Only_the_helper_files_are_unpacked(string name, bool keep) => Assert.Equal(keep, CaptionMath.Keep(name));

    [Theory]
    [InlineData("whisper_print_progress_callback: progress =  45%", 45)]
    [InlineData("whisper_print_progress_callback: progress = 100%", 100)]
    [InlineData("output_srt: saving output to 'out.srt'", null)]
    [InlineData(null, null)]
    public void Progress_is_read_from_the_helper(string? line, int? percent) => Assert.Equal(percent, CaptionMath.Progress(line));

    [Fact]
    public void Three_stereo_frames_make_one_mono_sample()
    {
        float[] stereo = [1, 1, 1, 1, 1, 1, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, 2, 2, 2, 2, 2, 2, 0.1f];
        Assert.Equal([short.MaxValue, (short)(-0.5f * short.MaxValue), short.MaxValue], CaptionMath.ToMono16k(stereo));
    }

    [Fact]
    public void Header_describes_16_kHz_mono_16_bit()
    {
        var h = CaptionMath.Wav16kHeader(16000);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal(36 + 32000, BitConverter.ToInt32(h, 4));
        Assert.Equal(1, BitConverter.ToInt16(h, 22));
        Assert.Equal(16000, BitConverter.ToInt32(h, 24));
        Assert.Equal(16, BitConverter.ToInt16(h, 34));
        Assert.Equal(32000, BitConverter.ToInt32(h, 40));
    }

    [Fact]
    public void One_job_runs_at_a_time_in_order()
    {
        var q = new CaptionQueue();
        q.Add("a");
        q.Add("b");
        Assert.Equal("a", q.Next());
        Assert.Null(q.Next());
        q.Done("a");
        Assert.Equal("b", q.Next());
    }

    [Fact]
    public void A_recording_stops_the_job_and_puts_it_first_again()
    {
        var q = new CaptionQueue();
        q.Add("a");
        q.Add("b");
        Assert.Equal("a", q.Next());
        Assert.Equal("a", q.Pause());
        q.Add("c");
        Assert.Null(q.Next());
        q.Resume();
        Assert.Equal("a", q.Next());
        q.Done("a");
        Assert.Equal("b", q.Next());
        q.Done("b");
        Assert.Equal("c", q.Next());
    }

    [Fact]
    public void A_job_that_ended_before_the_pause_does_not_run_again()
    {
        var q = new CaptionQueue();
        q.Add("a");
        q.Add("b");
        Assert.Equal("a", q.Next());
        Assert.Equal("a", q.Pause());
        q.Done("a");
        q.Resume();
        Assert.Equal("b", q.Next());
        q.Done("b");
        Assert.Null(q.Next());
    }

    [Theory]
    [InlineData(60, 600)]
    [InlineData(200, 600)]
    [InlineData(1200, 3600)]
    public void The_helper_gets_10_minutes_or_3_times_the_sound(int soundSeconds, int limitSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(limitSeconds), CaptionMath.HelperLimit(TimeSpan.FromSeconds(soundSeconds)));

    [Fact]
    public void Pause_with_nothing_running_stops_nothing() => Assert.Null(new CaptionQueue().Pause());
}
