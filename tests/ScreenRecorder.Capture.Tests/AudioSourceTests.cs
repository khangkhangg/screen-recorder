using ScreenRecorder.Capture;

namespace ScreenRecorder.Capture.Tests;

public class AudioSourceTests
{
    [Fact]
    public async Task Loopback_opens_and_reads_without_error()
    {
        await using var src = await AudioSource.OpenLoopbackAsync();
        await Task.Delay(200);
        var buf = new float[4800];
        var n = src.Read(buf);
        Assert.InRange(n, 0, buf.Length);
        Assert.Equal(0, n % 2);
    }

    [Fact]
    public async Task Loopback_reads_48k_stereo_pairs()
    {
        await using var src = await AudioSource.OpenLoopbackAsync();
        await Task.Delay(300);
        var n = src.Read(new float[96_000]);
        Assert.InRange(n, 0, 57_600); // at most 0.6 s of stereo floats
        Assert.Equal(0, n % 2);
    }

    [Fact]
    public async Task Lost_handler_can_dispose_the_source()
    {
        var src = await AudioSource.OpenLoopbackAsync();
        var done = new ManualResetEventSlim();
        src.Lost += () => { src.DisposeAsync().AsTask().Wait(); done.Set(); };
        src.RaiseLost();
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Loopback_can_be_disposed_twice()
    {
        var src = await AudioSource.OpenLoopbackAsync();
        await src.DisposeAsync();
        await src.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_mic_id_gives_a_plain_message()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => AudioSource.OpenMicAsync("no-such-mic"));
        Assert.True(e.Message.Contains("No microphone") || e.Message.Contains("privacy"), e.Message);
    }

    [Fact]
    public async Task Mic_opens_or_fails_with_a_plain_message()
    {
        var mics = await AudioSource.ListMicsAsync();
        try
        {
            await using var src = await AudioSource.OpenMicAsync(null);
            await Task.Delay(200);
            var n = src.Read(new float[4800]);
            Assert.Equal(0, n % 2);
        }
        catch (InvalidOperationException e)
        {
            // No microphone, or Windows privacy settings block it.
            Assert.True(mics.Count == 0 ? e.Message.Contains("No microphone") : e.Message.Contains("privacy"), e.Message);
        }
    }
}
