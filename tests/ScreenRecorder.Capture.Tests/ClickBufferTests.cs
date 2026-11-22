using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture.Tests;

public class ClickBufferTests
{
    [Fact]
    public void Keeps_one_second_oldest_first_and_skips_pauses()
    {
        var b = new ClickBuffer();
        b.Add(1, 1, ClickButton.Left, 1000);
        b.Add(2, 2, null, 1100);
        b.Add(3, 3, ClickButton.Right, 1500);
        b.Paused = true;
        b.Add(4, 4, ClickButton.Left, 1600);
        b.Paused = false;
        Assert.Equal((4, 4), b.Pointer);
        var into = new Click[32];
        Assert.Equal([new(1, 1, ClickButton.Left, 1000), new(3, 3, ClickButton.Right, 1500)], into[..b.Read(into, 1999)]);
        Assert.Equal([new Click(3, 3, ClickButton.Right, 1500)], into[..b.Read(into, 2000)]);
        Assert.Equal(0, b.Read(into, 2500));
    }

    [Fact]
    public void Full_buffer_keeps_the_newest()
    {
        var b = new ClickBuffer();
        for (int i = 1; i <= 40; i++) b.Add(i, 0, ClickButton.Left, i);
        var into = new Click[32];
        Assert.Equal(32, b.Read(into, 100));
        Assert.Equal(9, into[0].X);
        Assert.Equal(40, into[31].X);
    }
}
