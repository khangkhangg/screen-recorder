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
        Assert.True(n >= 0);
        Assert.Equal(0, n % 2);
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
