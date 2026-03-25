using System.Buffers.Binary;
using System.Text;

namespace AirBridge.Mirror;

/// <summary>
/// Message type tags for the screen-mirror sub-protocol.
/// These align with the values in <c>AirBridge.Transport.Protocol.MessageType</c>:
/// <c>MirrorStart = 0x20</c>, <c>MirrorFrame = 0x21</c>, <c>MirrorStop = 0x22</c>.
/// </summary>
public enum MirrorMessageType : byte
{
    /// <summary>Android → Windows: announces a new mirror session with stream parameters.</summary>
    MirrorStart = 0x20,

    /// <summary>Android → Windows: one encoded H.264/H.265 NAL frame.</summary>
    MirrorFrame = 0x21,

    /// <summary>Either direction: graceful teardown of a mirror session.</summary>
    MirrorStop = 0x22,

    /// <summary>Windows → Android: a pointer, key, or mouse input event to inject on the phone.</summary>
    InputEvent = 0x30
}

/// <summary>
/// Wire-format message sent by Android to start a mirror session.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte ] type = 0x20
/// [4 bytes] session-id length (N)
/// [N bytes] session-id (UTF-8)
/// [4 bytes] width  (int32, pixels)
/// [4 bytes] height (int32, pixels)
/// [4 bytes] fps    (int32)
/// [4 bytes] codec string length (M)
/// [M bytes] codec  (UTF-8, e.g. "H264")
/// </code>
/// </summary>
public sealed record MirrorStartMessage(
    string SessionId,
    int Width,
    int Height,
    int Fps,
    string Codec)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var sidBytes   = Encoding.UTF8.GetBytes(SessionId);
        var codecBytes = Encoding.UTF8.GetBytes(Codec);
        // 1 (type) + 4 (sid len) + N + 4 (width) + 4 (height) + 4 (fps) + 4 (codec len) + M
        var buf = new byte[1 + 4 + sidBytes.Length + 4 + 4 + 4 + 4 + codecBytes.Length];
        int pos = 0;
        buf[pos++] = (byte)MirrorMessageType.MirrorStart;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), sidBytes.Length);   pos += 4;
        sidBytes.CopyTo(buf, pos);                                                    pos += sidBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), Width);              pos += 4;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), Height);             pos += 4;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), Fps);                pos += 4;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), codecBytes.Length);  pos += 4;
        codecBytes.CopyTo(buf, pos);
        return buf;
    }

    /// <summary>
    /// Deserializes a <see cref="MirrorStartMessage"/> from <paramref name="data"/>
    /// (including the type byte at index 0).
    /// </summary>
    public static MirrorStartMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos      = 1; // skip type byte
        int sidLen   = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var sessionId = Encoding.UTF8.GetString(data.Slice(pos, sidLen));       pos += sidLen;
        int width    = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        int height   = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        int fps      = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        int codecLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var codec    = Encoding.UTF8.GetString(data.Slice(pos, codecLen));
        return new MirrorStartMessage(sessionId, width, height, fps, codec);
    }
}

/// <summary>
/// Wire-format message carrying one encoded H.264/H.265 NAL buffer.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte ] type = 0x21
/// [4 bytes] session-id length (N)
/// [N bytes] session-id (UTF-8)
/// [8 bytes] timestamp-ms (int64, presentation time in milliseconds)
/// [1 byte ] flags (bit 0 = keyframe)
/// [4 bytes] payload length (P)
/// [P bytes] H.264 NAL data
/// </code>
/// </summary>
public sealed record MirrorFrameMessage(
    string SessionId,
    long TimestampMs,
    bool IsKeyFrame,
    byte[] NalData)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var sidBytes = Encoding.UTF8.GetBytes(SessionId);
        // 1 (type) + 4 (sid len) + N + 8 (ts) + 1 (flags) + 4 (nal len) + P
        var buf = new byte[1 + 4 + sidBytes.Length + 8 + 1 + 4 + NalData.Length];
        int pos = 0;
        buf[pos++] = (byte)MirrorMessageType.MirrorFrame;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), sidBytes.Length); pos += 4;
        sidBytes.CopyTo(buf, pos);                                                  pos += sidBytes.Length;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(pos, 8), TimestampMs);      pos += 8;
        buf[pos++] = IsKeyFrame ? (byte)0x01 : (byte)0x00;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), NalData.Length);   pos += 4;
        NalData.CopyTo(buf, pos);
        return buf;
    }

    /// <summary>
    /// Deserializes a <see cref="MirrorFrameMessage"/> from <paramref name="data"/>
    /// (including the type byte at index 0).
    /// </summary>
    public static MirrorFrameMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos      = 1;
        int sidLen   = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));  pos += 4;
        var sessionId = Encoding.UTF8.GetString(data.Slice(pos, sidLen));         pos += sidLen;
        long tsMs    = BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8));   pos += 8;
        bool keyFrame = (data[pos++] & 0x01) != 0;
        int nalLen   = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));   pos += 4;
        var nalData  = data.Slice(pos, nalLen).ToArray();
        return new MirrorFrameMessage(sessionId, tsMs, keyFrame, nalData);
    }
}

/// <summary>
/// Wire-format message sent by either side to signal graceful mirror teardown.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte ] type = 0x22
/// [4 bytes] session-id length (N)
/// [N bytes] session-id (UTF-8)
/// </code>
/// </summary>
public sealed record MirrorStopMessage(string SessionId)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var sidBytes = Encoding.UTF8.GetBytes(SessionId);
        var buf = new byte[1 + 4 + sidBytes.Length];
        buf[0] = (byte)MirrorMessageType.MirrorStop;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1, 4), sidBytes.Length);
        sidBytes.CopyTo(buf, 5);
        return buf;
    }

    /// <summary>
    /// Deserializes a <see cref="MirrorStopMessage"/> from <paramref name="data"/>
    /// (including the type byte at index 0).
    /// </summary>
    public static MirrorStopMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int sidLen  = BinaryPrimitives.ReadInt32BigEndian(data.Slice(1, 4));
        var sessionId = Encoding.UTF8.GetString(data.Slice(5, sidLen));
        return new MirrorStopMessage(sessionId);
    }
}

/// <summary>
/// Wire-format message sent by Windows to relay a pointer/key input event to the Android device.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte ] type = 0x30
/// [4 bytes] session-id length (N)
/// [N bytes] session-id (UTF-8)
/// [1 byte ] event-type (0 = Touch, 1 = Key, 2 = Mouse)
/// [4 bytes] normalizedX (IEEE 754 float32, 0.0 – 1.0)
/// [4 bytes] normalizedY (IEEE 754 float32, 0.0 – 1.0)
/// [1 byte ] has-keycode (0 or 1)
/// [4 bytes] keycode     (int32, present only if has-keycode = 1)
/// [4 bytes] metaState   (int32)
/// </code>
/// </summary>
public sealed record InputEventMessage(
    string SessionId,
    InputEventKind EventKind,
    float NormalizedX,
    float NormalizedY,
    int? Keycode,
    int MetaState)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var sidBytes = Encoding.UTF8.GetBytes(SessionId);
        // 1 (type) + 4 (sid len) + N + 1 (kind) + 4 (x) + 4 (y) + 1 (hasKey) [+ 4 (keycode)] + 4 (meta)
        int size = 1 + 4 + sidBytes.Length + 1 + 4 + 4 + 1 + (Keycode.HasValue ? 4 : 0) + 4;
        var buf  = new byte[size];
        int pos  = 0;

        buf[pos++] = (byte)MirrorMessageType.InputEvent;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), sidBytes.Length); pos += 4;
        sidBytes.CopyTo(buf, pos);                                                  pos += sidBytes.Length;
        buf[pos++] = (byte)EventKind;
        BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(pos, 4), NormalizedX);    pos += 4;
        BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(pos, 4), NormalizedY);    pos += 4;
        if (Keycode.HasValue)
        {
            buf[pos++] = 0x01;
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), Keycode.Value); pos += 4;
        }
        else
        {
            buf[pos++] = 0x00;
        }
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), MetaState);
        return buf;
    }

    /// <summary>
    /// Deserializes an <see cref="InputEventMessage"/> from <paramref name="data"/>
    /// (including the type byte at index 0).
    /// </summary>
    public static InputEventMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos     = 1;
        int sidLen  = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));  pos += 4;
        var sessionId = Encoding.UTF8.GetString(data.Slice(pos, sidLen));       pos += sidLen;
        var kind    = (InputEventKind)data[pos++];
        float x     = BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4)); pos += 4;
        float y     = BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4)); pos += 4;
        bool hasKey = data[pos++] != 0;
        int? keycode = null;
        if (hasKey)
        {
            keycode = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));  pos += 4;
        }
        int meta = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
        return new InputEventMessage(sessionId, kind, x, y, keycode, meta);
    }
}

/// <summary>Kind of input event carried in <see cref="InputEventMessage"/>.</summary>
public enum InputEventKind : byte
{
    /// <summary>A touch/tap event (finger down, move, or up).</summary>
    Touch = 0,
    /// <summary>A hardware key press or release.</summary>
    Key   = 1,
    /// <summary>A mouse pointer event.</summary>
    Mouse = 2
}
