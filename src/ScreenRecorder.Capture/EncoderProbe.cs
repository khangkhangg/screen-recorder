using ScreenRecorder.Core;
using Vortice.MediaFoundation;

namespace ScreenRecorder.Capture;

public enum EncoderSupport { Hardware, TooFewHardwareSessions, SoftwareOnly }

public static class EncoderProbe
{
    const uint MftEnumHardware = 0x4, MftEnumSortAndFilter = 0x40;
    static readonly PixelSize Fallback = new(4096, 2304);

    static EncoderProbe() => MediaFactory.MFStartup();

    // Opens the encoders at the test size and closes them again. It encodes no frames.
    public static EncoderSupport Check(int sessionsNeeded)
    {
        // An activate object returns the same encoder on every call, so each session enumerates its own.
        var sessions = new List<IMFActivateCollection>();
        try
        {
            for (int i = 0; i < sessionsNeeded; i++)
            {
                sessions.Add(HardwareEncoders());
                if (sessions[^1].FirstOrDefault() is not { } encoder) return EncoderSupport.SoftwareOnly;
                Open(encoder, new PixelSize(1920, 1080)).Dispose();
            }
            return EncoderSupport.Hardware;
        }
        catch (SharpGen.Runtime.SharpGenException) { return EncoderSupport.TooFewHardwareSessions; }
        finally
        {
            foreach (var s in sessions)
            {
                s.FirstOrDefault()?.ShutdownObject();
                s.Dispose();
            }
        }
    }

    // Media Foundation has no "largest size" attribute, so the encoder reports 8K by accepting it.
    public static PixelSize MaxSize()
    {
        using var activates = HardwareEncoders();
        var encoder = activates.FirstOrDefault();
        if (encoder is null) return Fallback;
        var eightK = new PixelSize(7680, 4320);
        try
        {
            using var t = Open(encoder, eightK);
            return eightK;
        }
        catch (SharpGen.Runtime.SharpGenException) { return Fallback; }
        finally { encoder.ShutdownObject(); }
    }

    static IMFActivateCollection HardwareEncoders() => MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
        MftEnumHardware | MftEnumSortAndFilter, null,
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

    // The activate object keeps the encoder open until ShutdownObject, after the caller drops its reference.
    static IMFTransform Open(IMFActivate activate, PixelSize size)
    {
        var transform = activate.ActivateObject<IMFTransform>();
        try
        {
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)FrameMath.Bitrate(size, 30));
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(size.Width, size.Height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(30, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            transform.SetOutputType(0, type, 0);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            type.DeleteItem(MediaTypeAttributeKeys.AvgBitrate);
            transform.SetInputType(0, type, 0);
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            return transform;
        }
        catch
        {
            transform.Dispose();
            throw;
        }
    }

    internal static ulong Pack(int high, int low) => (ulong)(uint)high << 32 | (uint)low;
}
