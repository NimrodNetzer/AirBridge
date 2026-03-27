using AirBridge.Mirror;

namespace AirBridge.Tests.Mirror;

/// <summary>
/// Round-trip serialization tests for the three mirror message types.
/// No network sockets or WinRT APIs are required.
/// </summary>
public class MirrorMessageTests
{
    // ── MirrorStartMessage ─────────────────────────────────────────────────

    [Fact]
    public void MirrorStartMessage_RoundTrip_BasicValues()
    {
        var msg     = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1920, 1080, 30, "session-abc");
        var bytes   = msg.ToBytes();
        var decoded = MirrorStartMessage.FromBytes(bytes);

        Assert.Equal(msg.SessionId, decoded.SessionId);
        Assert.Equal(msg.Width,     decoded.Width);
        Assert.Equal(msg.Height,    decoded.Height);
        Assert.Equal(msg.Fps,       decoded.Fps);
        Assert.Equal(msg.Codec,     decoded.Codec);
        Assert.Equal(msg.Mode,      decoded.Mode);
    }

    [Fact]
    public void MirrorStartMessage_TypeByte_IsCorrect()
    {
        var msg   = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H265, 1280, 720, 60, "s");
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.MirrorStart, bytes[0]);
    }

    [Fact]
    public void MirrorStartMessage_RoundTrip_H265Codec()
    {
        var msg     = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H265, 3840, 2160, 30, "s1");
        var decoded = MirrorStartMessage.FromBytes(msg.ToBytes());
        Assert.Equal(MirrorCodec.H265, decoded.Codec);
    }

    [Fact]
    public void MirrorStartMessage_RoundTrip_MaxDimensions()
    {
        var msg     = new MirrorStartMessage(MirrorSessionMode.TabletDisplay, MirrorCodec.H264, ushort.MaxValue, ushort.MaxValue, 120, "sid");
        var decoded = MirrorStartMessage.FromBytes(msg.ToBytes());
        Assert.Equal(ushort.MaxValue, decoded.Width);
        Assert.Equal(ushort.MaxValue, decoded.Height);
        Assert.Equal(120, decoded.Fps);
    }

    [Fact]
    public void MirrorStartMessage_RoundTrip_UnicodeSessionId()
    {
        var msg     = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1920, 1080, 24, "session-\u4e2d\u6587");
        var decoded = MirrorStartMessage.FromBytes(msg.ToBytes());
        Assert.Equal(msg.SessionId, decoded.SessionId);
    }

    // ── MirrorFrameMessage ─────────────────────────────────────────────────

    [Fact]
    public void MirrorFrameMessage_RoundTrip_Keyframe()
    {
        var nal     = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0xAA };
        var msg     = new MirrorFrameMessage(true, 1_234_567_890L, nal);
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());

        Assert.True(decoded.IsKeyFrame);
        Assert.Equal(msg.PresentationTimestampUs, decoded.PresentationTimestampUs);
        Assert.Equal(msg.NalData, decoded.NalData);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_NonKeyframe()
    {
        var nal     = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41 };
        var msg     = new MirrorFrameMessage(false, 0L, nal);
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());

        Assert.False(decoded.IsKeyFrame);
        Assert.Equal(0L, decoded.PresentationTimestampUs);
        Assert.Equal(nal, decoded.NalData);
    }

    [Fact]
    public void MirrorFrameMessage_TypeByte_IsCorrect()
    {
        var msg   = new MirrorFrameMessage(false, 0L, new byte[] { 1, 2, 3 });
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.MirrorFrame, bytes[0]);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_LargeNalPayload()
    {
        var nal = new byte[65536];
        Random.Shared.NextBytes(nal);
        var msg     = new MirrorFrameMessage(true, 99_999L, nal);
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());

        Assert.Equal(nal, decoded.NalData);
        Assert.Equal(99_999L, decoded.PresentationTimestampUs);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_MaxTimestamp()
    {
        var msg     = new MirrorFrameMessage(true, long.MaxValue, new byte[] { 0 });
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());
        Assert.Equal(long.MaxValue, decoded.PresentationTimestampUs);
    }

    // ── MirrorStopMessage ──────────────────────────────────────────────────

    [Fact]
    public void MirrorStopMessage_RoundTrip_NormalReason()
    {
        var msg     = new MirrorStopMessage(0);
        var decoded = MirrorStopMessage.FromBytes(msg.ToBytes());
        Assert.Equal(0, decoded.ReasonCode);
    }

    [Fact]
    public void MirrorStopMessage_TypeByte_IsCorrect()
    {
        var msg   = new MirrorStopMessage();
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.MirrorStop, bytes[0]);
    }

    [Fact]
    public void MirrorStopMessage_RoundTrip_ErrorReason()
    {
        var msg     = new MirrorStopMessage(1);
        var decoded = MirrorStopMessage.FromBytes(msg.ToBytes());
        Assert.Equal(1, decoded.ReasonCode);
    }
}
