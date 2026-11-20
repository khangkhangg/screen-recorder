using ScreenRecorder.Capture;

namespace ScreenRecorder.Capture.Tests;

public class WavWriterTests
{
    [Fact]
    public void Wav_header_is_valid()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var w = new WavWriter(path)) w.Write(new float[96_000]);
            Assert.Equal(44 + 96_000 * 4, new FileInfo(path).Length);
            var b = File.ReadAllBytes(path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(b, 8, 4));
            Assert.Equal(3, BitConverter.ToUInt16(b, 20));
            Assert.Equal(2, BitConverter.ToUInt16(b, 22));
            Assert.Equal(48000, BitConverter.ToInt32(b, 24));
            Assert.Equal(32, BitConverter.ToUInt16(b, 34));
            Assert.Equal(96_000 * 4, BitConverter.ToInt32(b, 40));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Wav_writer_can_be_disposed_twice()
    {
        var path = Path.GetTempFileName();
        try { var w = new WavWriter(path); w.Dispose(); w.Dispose(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cut_off_wav_has_usable_header()
    {
        var path = Path.GetTempFileName();
        var copy = Path.GetTempFileName();
        try
        {
            var w = new WavWriter(path);
            for (var i = 0; i < 3; i++) w.Write(new float[96_000]); // 3 s, never disposed
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = File.Create(copy)) src.CopyTo(dst);
            w.Dispose();
            var b = File.ReadAllBytes(copy);
            Assert.True(BitConverter.ToInt32(b, 40) >= 2 * 48000 * 8);
        }
        finally { File.Delete(path); File.Delete(copy); }
    }
}
