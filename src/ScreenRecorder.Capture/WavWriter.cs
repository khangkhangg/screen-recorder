using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Capture;

public sealed class WavWriter : IDisposable
{
    const int Rate = 48000, Channels = 2, FrameBytes = Channels * 4, HeaderSize = 44;
    readonly FileStream _file;
    long _dataBytes, _headerBytes;

    public WavWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        WriteHeader();
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        _file.Write(MemoryMarshal.AsBytes(interleaved));
        _dataBytes += interleaved.Length * 4;
        if (_dataBytes - _headerBytes >= Rate * FrameBytes) WriteHeader();
    }

    // Rewrites the sizes in place so a file cut off by a crash still opens.
    void WriteHeader()
    {
        _file.Flush();
        _file.Position = 0;
        using var w = new BinaryWriter(_file, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8); w.Write((int)(36 + _dataBytes)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)Channels);
        w.Write(Rate); w.Write(Rate * FrameBytes); w.Write((short)FrameBytes); w.Write((short)32);
        w.Write("data"u8); w.Write((int)_dataBytes);
        w.Flush();
        _file.Position = HeaderSize + _dataBytes;
        _file.Flush();
        _headerBytes = _dataBytes;
    }

    public void Dispose()
    {
        WriteHeader();
        _file.Dispose();
    }
}
