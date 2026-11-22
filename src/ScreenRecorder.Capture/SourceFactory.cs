using System.Diagnostics.CodeAnalysis;
using Microsoft.Graphics.Canvas;
using ScreenRecorder.Core;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenRecorder.Capture;

internal interface IScreenSource : IDisposable
{
    PixelSize SourceSize { get; }
    bool IsMinimized { get; }
    event Action? Closed;
    bool TryGetLatestFrame([MaybeNullWhen(false)] out CanvasBitmap frame);
}

internal interface IWebcamSource : IAsyncDisposable
{
    PixelSize Size { get; }
    event Action? Lost;
    bool TryGetLatestFrame([MaybeNullWhen(false)] out CanvasBitmap frame);
}

internal interface IAudioSource : IAsyncDisposable
{
    event Action? Lost;
    int Read(Span<float> interleavedStereo48k);
}

internal interface IVideoWriter : IDisposable
{
    long FramesBehind { get; }
    // One sample stands for count frames, so repeated frames cost one texture and one encode.
    void WriteFrame(IDirect3DSurface surface, long frameIndex, long count = 1);
    void WriteAudio(ReadOnlySpan<float> interleavedStereo48k, long firstSampleIndex);
}

// X and Y are desktop pixels, Ticks comes from Environment.TickCount64.
public readonly record struct Click(int X, int Y, ClickButton Button, long Ticks);

internal interface IClickSource : IDisposable
{
    (int X, int Y) Pointer { get; }
    // Copies the clicks of the last second into `into`, oldest first, and returns how many.
    int Read(Span<Click> into);
}

internal interface ISourceFactory
{
    IClickSource OpenClicks();
    IScreenSource OpenScreen(CaptureTarget target, CanvasDevice device);
    Task<IWebcamSource> OpenWebcamAsync(string id, CanvasDevice device);
    Task<IAudioSource> OpenMicAsync(string? id);
    Task<IAudioSource> OpenSystemAudioAsync();
    IVideoWriter OpenWriter(string path, PixelSize size, int frameRate, CanvasDevice device, bool hardware, bool withAudio);
}

internal sealed class SourceFactory : ISourceFactory
{
    public static readonly SourceFactory Instance = new();
    public IClickSource OpenClicks() => new ClickHook();
    public IScreenSource OpenScreen(CaptureTarget target, CanvasDevice device) => ScreenSource.Create(target, device);
    public async Task<IWebcamSource> OpenWebcamAsync(string id, CanvasDevice device) => await WebcamSource.OpenAsync(id, device);
    public async Task<IAudioSource> OpenMicAsync(string? id) => await AudioSource.OpenMicAsync(id);
    public async Task<IAudioSource> OpenSystemAudioAsync() => await AudioSource.OpenLoopbackAsync();
    public IVideoWriter OpenWriter(string path, PixelSize size, int frameRate, CanvasDevice device, bool hardware, bool withAudio) =>
        new VideoFileWriter(path, size, frameRate, device, hardware, withAudio);
}
