using System.Buffers.Binary;
using System.Text;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Message type tags for the screen-mirror sub-protocol.
/// Values align with <see cref="MessageType"/>:
/// <c>MirrorStart = 0x20</c>, <c>MirrorFrame = 0x21</c>, <c>MirrorStop = 0x22</c>.
/// </summary>
public enum MirrorMessageType : byte
{
    /// <summary>Initiator → Receiver: announces a new mirror session.</summary>
    MirrorStart = 0x20,

    /// <summary>Source → Sink: one H.264 NAL unit (or AVCC-framed chunk).</summary>
    MirrorFrame = 0x21,

    /// <summary>Either side: terminates the mirror session.</summary>
    MirrorStop  = 0x22,
}

/// <summary>
/// Codec negotiated in <see cref="MirrorStartMessage"/>.
/// </summary>
public enum MirrorCodec : byte
{
    H264 = 0x01,
    H265 = 0x02,
}

/// <summary>
/// Mirror mode carried in <see cref="MirrorStartMessage"/> so the receiver
/// knows whether it is the sink (TabletDisplay) or the source (PhoneWindow).
/// </summary>
public enum MirrorSessionMode : byte
{
    /// <summary>Android is the source; Windows renders a floating window.</summary>
    PhoneWindow    = 0x01,

    /// <summary>Windows is the source (IddCx virtual display); Android renders full-screen.</summary>
    TabletDisplay  = 0x02,
}

// ── MirrorStartMessage ─────────────────────────────────────────────────────

/// <summary>
/// Wire-format message that opens a mirror session.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte ] type = 0x20
/// [1 byte ] mode (MirrorSessionMode)
/// [1 byte ] codec (MirrorCodec)
/// [2 bytes] width (uint16)
/// [2 bytes] height (uint16)
/// [1 byte ] fps
/// [4 bytes] session-id length (N)
/// [N bytes] session-id (UTF-8)
/// </code>
/// </summary>
public sealed record MirrorStartMessage(
    MirrorSessionMode Mode,
    MirrorCodec       Codec,
    ushort            Width,
    ushort            Height,
    byte              Fps,
    string            SessionId)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var sidBytes = Encoding.UTF8.GetBytes(SessionId);
        // 1+1+1+2+2+1+4+N
        var buf = new byte[12 + sidBytes.Length];
        int pos = 0;
        buf[pos++] = (byte)MirrorMessageType.MirrorStart;
        buf[pos++] = (byte)Mode;
        buf[pos++] = (byte)Codec;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos, 2), Width);  pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos, 2), Height); pos += 2;
        buf[pos++] = Fps;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), sidBytes.Length); pos += 4;
        sidBytes.CopyTo(buf, pos);
        return buf;
    }

    /// <summary>Deserializes a <see cref="MirrorStartMessage"/> from <paramref name="data"/>.</summary>
    public static MirrorStartMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos = 1; // skip type byte
        var mode   = (MirrorSessionMode)data[pos++];
        var codec  = (MirrorCodec)data[pos++];
        var width  = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;
        var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;
        byte fps   = data[pos++];
        int sidLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var sid    = Encoding.UTF8.GetString(data.Slice(pos, sidLen));
        return new MirrorStartMessage(mode, codec, width, height, fps, sid);
    }
}

// ── MirrorFrameMessage ──────────────────────────────────────────────────────

/// <summary>
/// Wire-format message carrying one H.264 NAL unit from source to sink.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte ] type = 0x21
/// [1 byte ] flags: bit 0 = isKeyFrame
/// [8 bytes] presentation timestamp in microseconds (int64)
/// [4 bytes] NAL data length (N)
/// [N bytes] H.264 NAL data
/// </code>
/// </summary>
public sealed record MirrorFrameMessage(
    bool     IsKeyFrame,
    long     PresentationTimestampUs,
    byte[]   NalData)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[1 + 1 + 8 + 4 + NalData.Length];
        int pos = 0;
        buf[pos++] = (byte)MirrorMessageType.MirrorFrame;
        buf[pos++] = (byte)(IsKeyFrame ? 0x01 : 0x00);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(pos, 8), PresentationTimestampUs); pos += 8;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), NalData.Length);          pos += 4;
        NalData.CopyTo(buf, pos);
        return buf;
    }

    /// <summary>Deserializes a <see cref="MirrorFrameMessage"/> from <paramref name="data"/>.</summary>
    public static MirrorFrameMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos       = 1; // skip type byte
        bool keyFrame = (data[pos++] & 0x01) != 0;
        long pts      = BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8)); pos += 8;
        int nalLen    = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var nal       = data.Slice(pos, nalLen).ToArray();
        return new MirrorFrameMessage(keyFrame, pts, nal);
    }
}

// ── MirrorStopMessage ───────────────────────────────────────────────────────

/// <summary>
/// Wire-format message that terminates a mirror session.
/// <para>Binary layout:</para>
/// <code>
/// [1 byte] type = 0x22
/// [1 byte] reason code (0 = normal, 1 = error)
/// </code>
/// </summary>
public sealed record MirrorStopMessage(byte ReasonCode = 0)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes() => new[] { (byte)MirrorMessageType.MirrorStop, ReasonCode };

    /// <summary>Deserializes a <see cref="MirrorStopMessage"/> from <paramref name="data"/>.</summary>
    public static MirrorStopMessage FromBytes(ReadOnlySpan<byte> data)
        => new MirrorStopMessage(data.Length > 1 ? data[1] : (byte)0);
}
