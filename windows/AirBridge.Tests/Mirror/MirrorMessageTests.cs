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
        var msg     = new MirrorStartMessage("session-abc", 1080, 1920, 30, "H264");
        var bytes   = msg.ToBytes();
        var decoded = MirrorStartMessage.FromBytes(bytes);

        Assert.Equal(msg.SessionId, decoded.SessionId);
        Assert.Equal(msg.Width,     decoded.Width);
        Assert.Equal(msg.Height,    decoded.Height);
        Assert.Equal(msg.Fps,       decoded.Fps);
        Assert.Equal(msg.Codec,     decoded.Codec);
    }

    [Fact]
    public void MirrorStartMessage_TypeByte_IsCorrect()
    {
        var msg   = new MirrorStartMessage("s", 720, 1280, 60, "H265");
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.MirrorStart, bytes[0]);
    }

    [Fact]
    public void MirrorStartMessage_RoundTrip_H265Codec()
    {
        var msg     = new MirrorStartMessage("s1", 3840, 2160, 30, "H265");
        var decoded = MirrorStartMessage.FromBytes(msg.ToBytes());
        Assert.Equal("H265", decoded.Codec);
    }

    [Fact]
    public void MirrorStartMessage_RoundTrip_MaxDimensions()
    {
        var msg     = new MirrorStartMessage("sid", int.MaxValue, int.MaxValue, 120, "H264");
        var decoded = MirrorStartMessage.FromBytes(msg.ToBytes());
        Assert.Equal(int.MaxValue, decoded.Width);
        Assert.Equal(int.MaxValue, decoded.Height);
        Assert.Equal(120, decoded.Fps);
    }

    [Fact]
    public void MirrorStartMessage_RoundTrip_UnicodeSessionId()
    {
        var msg     = new MirrorStartMessage("session-\u4e2d\u6587", 1920, 1080, 24, "H264");
        var decoded = MirrorStartMessage.FromBytes(msg.ToBytes());
        Assert.Equal(msg.SessionId, decoded.SessionId);
    }

    // ── MirrorFrameMessage ─────────────────────────────────────────────────

    [Fact]
    public void MirrorFrameMessage_RoundTrip_Keyframe()
    {
        var nal     = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0xAA };
        var msg     = new MirrorFrameMessage("session-abc", 1_234_567_890L, true, nal);
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());

        Assert.Equal(msg.SessionId,   decoded.SessionId);
        Assert.Equal(msg.TimestampMs, decoded.TimestampMs);
        Assert.True(decoded.IsKeyFrame);
        Assert.Equal(msg.NalData, decoded.NalData);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_NonKeyframe()
    {
        var nal     = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41 };
        var msg     = new MirrorFrameMessage("s2", 0L, false, nal);
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());

        Assert.False(decoded.IsKeyFrame);
        Assert.Equal(0L, decoded.TimestampMs);
        Assert.Equal(nal, decoded.NalData);
    }

    [Fact]
    public void MirrorFrameMessage_TypeByte_IsCorrect()
    {
        var msg   = new MirrorFrameMessage("s", 0L, false, new byte[] { 1, 2, 3 });
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.MirrorFrame, bytes[0]);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_LargeNalPayload()
    {
        var nal = new byte[65536];
        Random.Shared.NextBytes(nal);
        var msg     = new MirrorFrameMessage("big-session", 99_999L, true, nal);
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());

        Assert.Equal(nal, decoded.NalData);
        Assert.Equal(99_999L, decoded.TimestampMs);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_MaxTimestamp()
    {
        var msg     = new MirrorFrameMessage("s", long.MaxValue, true, new byte[] { 0 });
        var decoded = MirrorFrameMessage.FromBytes(msg.ToBytes());
        Assert.Equal(long.MaxValue, decoded.TimestampMs);
    }

    // ── MirrorStopMessage ──────────────────────────────────────────────────

    [Fact]
    public void MirrorStopMessage_RoundTrip_SessionId()
    {
        var msg     = new MirrorStopMessage("session-xyz");
        var decoded = MirrorStopMessage.FromBytes(msg.ToBytes());
        Assert.Equal(msg.SessionId, decoded.SessionId);
    }

    [Fact]
    public void MirrorStopMessage_TypeByte_IsCorrect()
    {
        var msg   = new MirrorStopMessage("s");
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.MirrorStop, bytes[0]);
    }

    [Fact]
    public void MirrorStopMessage_RoundTrip_EmptySessionId()
    {
        var msg     = new MirrorStopMessage(string.Empty);
        var decoded = MirrorStopMessage.FromBytes(msg.ToBytes());
        Assert.Equal(string.Empty, decoded.SessionId);
    }
}
