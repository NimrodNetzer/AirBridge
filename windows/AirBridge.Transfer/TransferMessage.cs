using System.Buffers.Binary;
using System.Text;

namespace AirBridge.Transfer;

/// <summary>
/// Message type tags for the file-transfer sub-protocol.
/// These align with the values in <c>AirBridge.Transport.Protocol.MessageType</c>:
/// <c>FileTransferStart = 0x10</c>, <c>FileChunk = 0x11</c>,
/// <c>FileTransferAck = 0x12</c>, <c>FileTransferEnd = 0x13</c>.
/// </summary>
public enum TransferMessageType : byte
{
    /// <summary>Sender → Receiver: announces a new file transfer.</summary>
    FileStart = 0x10,

    /// <summary>Sender → Receiver: one chunk of file data.</summary>
    FileChunk = 0x11,

    /// <summary>Receiver → Sender: acknowledges receipt of a chunk or the whole file.</summary>
    TransferAck = 0x12,

    /// <summary>Sender → Receiver: signals end-of-file; includes SHA-256 hash.</summary>
    FileEnd = 0x13,

    /// <summary>Either side: signals an error during transfer.</summary>
    TransferError = 0xFF
}

/// <summary>
/// Wire-format message sent by the sender to start a file transfer.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte  ] type = 0x10
/// [4 bytes ] session-id length (N)
/// [N bytes ] session-id (UTF-8)
/// [4 bytes ] file-name length (M)
/// [M bytes ] file-name (UTF-8)
/// [8 bytes ] total-bytes (int64)
/// </code>
/// </summary>
public sealed record FileStartMessage(string SessionId, string FileName, long TotalBytes)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var sessionBytes = Encoding.UTF8.GetBytes(SessionId);
        var nameBytes    = Encoding.UTF8.GetBytes(FileName);
        // 1 (type) + 4 (sessionId len) + N + 4 (fileName len) + M + 8 (totalBytes)
        var buf = new byte[1 + 4 + sessionBytes.Length + 4 + nameBytes.Length + 8];
        int pos = 0;
        buf[pos++] = (byte)TransferMessageType.FileStart;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), sessionBytes.Length); pos += 4;
        sessionBytes.CopyTo(buf, pos); pos += sessionBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), nameBytes.Length); pos += 4;
        nameBytes.CopyTo(buf, pos); pos += nameBytes.Length;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(pos, 8), TotalBytes);
        return buf;
    }

    /// <summary>Deserializes a <see cref="FileStartMessage"/> from <paramref name="data"/> (skipping the type byte).</summary>
    public static FileStartMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos = 1; // skip type byte
        int sidLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var sessionId = Encoding.UTF8.GetString(data.Slice(pos, sidLen)); pos += sidLen;
        int nameLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var fileName = Encoding.UTF8.GetString(data.Slice(pos, nameLen)); pos += nameLen;
        long totalBytes = BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8));
        return new FileStartMessage(sessionId, fileName, totalBytes);
    }
}

/// <summary>
/// Wire-format message carrying one chunk of file data.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte  ] type = 0x11
/// [8 bytes ] chunk offset (int64)
/// [4 bytes ] chunk length (int32)
/// [N bytes ] chunk data
/// </code>
/// </summary>
public sealed record FileChunkMessage(long Offset, byte[] Data)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[1 + 8 + 4 + Data.Length];
        int pos = 0;
        buf[pos++] = (byte)TransferMessageType.FileChunk;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(pos, 8), Offset); pos += 8;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos, 4), Data.Length); pos += 4;
        Data.CopyTo(buf, pos);
        return buf;
    }

    /// <summary>Deserializes a <see cref="FileChunkMessage"/> from <paramref name="data"/> (skipping the type byte).</summary>
    public static FileChunkMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int pos = 1;
        long offset = BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8)); pos += 8;
        int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        var chunk = data.Slice(pos, len).ToArray();
        return new FileChunkMessage(offset, chunk);
    }
}

/// <summary>
/// Wire-format message sent by the receiver to acknowledge a chunk or the whole transfer.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte  ] type = 0x12
/// [8 bytes ] bytes-acknowledged (int64)
/// </code>
/// </summary>
public sealed record TransferAckMessage(long BytesAcknowledged)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[1 + 8];
        buf[0] = (byte)TransferMessageType.TransferAck;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), BytesAcknowledged);
        return buf;
    }

    /// <summary>Deserializes a <see cref="TransferAckMessage"/> from <paramref name="data"/> (skipping the type byte).</summary>
    public static TransferAckMessage FromBytes(ReadOnlySpan<byte> data)
    {
        long acked = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1, 8));
        return new TransferAckMessage(acked);
    }
}

/// <summary>
/// Wire-format message sent by the sender to signal end of file, along with the SHA-256 digest.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte  ] type = 0x13
/// [8 bytes ] total-bytes (int64)
/// [32 bytes] SHA-256 hash of the full file
/// </code>
/// </summary>
public sealed record FileEndMessage(long TotalBytes, byte[] Sha256Hash)
{
    /// <summary>Expected length of the SHA-256 hash field.</summary>
    public const int HashLength = 32;

    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        if (Sha256Hash.Length != HashLength)
            throw new ArgumentException($"SHA-256 hash must be {HashLength} bytes.", nameof(Sha256Hash));
        var buf = new byte[1 + 8 + HashLength];
        buf[0] = (byte)TransferMessageType.FileEnd;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), TotalBytes);
        Sha256Hash.CopyTo(buf, 9);
        return buf;
    }

    /// <summary>Deserializes a <see cref="FileEndMessage"/> from <paramref name="data"/> (skipping the type byte).</summary>
    public static FileEndMessage FromBytes(ReadOnlySpan<byte> data)
    {
        long total = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1, 8));
        var hash   = data.Slice(9, HashLength).ToArray();
        return new FileEndMessage(total, hash);
    }
}

/// <summary>
/// Wire-format message sent by either side to signal a transfer error.
/// <para>Binary layout (big-endian):</para>
/// <code>
/// [1 byte  ] type = 0xFF
/// [4 bytes ] message length (int32)
/// [N bytes ] error message (UTF-8)
/// </code>
/// </summary>
public sealed record TransferErrorMessage(string ErrorMessage)
{
    /// <summary>Serializes the message to bytes.</summary>
    public byte[] ToBytes()
    {
        var msgBytes = Encoding.UTF8.GetBytes(ErrorMessage);
        var buf = new byte[1 + 4 + msgBytes.Length];
        buf[0] = (byte)TransferMessageType.TransferError;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1, 4), msgBytes.Length);
        msgBytes.CopyTo(buf, 5);
        return buf;
    }

    /// <summary>Deserializes a <see cref="TransferErrorMessage"/> from <paramref name="data"/> (skipping the type byte).</summary>
    public static TransferErrorMessage FromBytes(ReadOnlySpan<byte> data)
    {
        int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(1, 4));
        var msg = Encoding.UTF8.GetString(data.Slice(5, len));
        return new TransferErrorMessage(msg);
    }
}
