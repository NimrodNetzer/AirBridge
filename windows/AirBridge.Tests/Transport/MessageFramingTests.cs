using System.Buffers.Binary;
using AirBridge.Transport.Connection;
using AirBridge.Transport.Protocol;

namespace AirBridge.Tests.Transport;

/// <summary>
/// Unit tests for the <see cref="TlsMessageChannel"/> framing logic using in-memory
/// streams — no network sockets are created.
/// </summary>
public class MessageFramingTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a <see cref="ProtocolMessage"/> into its wire representation
    /// (<c>[4-byte length][1-byte type][payload]</c>) and returns the raw bytes.
    /// This mirrors the logic inside <see cref="TlsMessageChannel.SendAsync"/> so
    /// tests can verify the format without an open network connection.
    /// </summary>
    private static byte[] EncodeFrame(ProtocolMessage message)
    {
        var frame = new byte[4 + 1 + message.Payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)message.Payload.Length);
        frame[4] = (byte)message.Type;
        message.Payload.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>
    /// Decodes one framed message from a byte array (populates a <see cref="MemoryStream"/>
    /// and reads via a <see cref="TlsMessageChannel"/>-compatible approach).
    /// </summary>
    private static ProtocolMessage? DecodeFrame(byte[] data)
    {
        if (data.Length < 5) return null;

        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        var type        = (MessageType)data[4];
        var payload     = data.AsSpan(5, (int)payloadLen).ToArray();
        return new ProtocolMessage(type, payload);
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void EncodeFrame_EmptyPayload_ProducesCorrectHeader()
    {
        var msg   = new ProtocolMessage(MessageType.Ping, Array.Empty<byte>());
        var frame = EncodeFrame(msg);

        Assert.Equal(5, frame.Length);                      // 4 + 1 + 0
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(frame)); // length = 0
        Assert.Equal((byte)MessageType.Ping, frame[4]);    // type byte
    }

    [Fact]
    public void EncodeFrame_WithPayload_LengthIsPayloadOnlyNotIncludingTypeByte()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var msg     = new ProtocolMessage(MessageType.FileChunk, payload);
        var frame   = EncodeFrame(msg);

        // Length field must be payload bytes only (not counting the type byte)
        uint encodedLen = BinaryPrimitives.ReadUInt32BigEndian(frame);
        Assert.Equal((uint)payload.Length, encodedLen);
    }

    [Fact]
    public void EncodeFrame_TypeByteIsAtCorrectOffset()
    {
        var msg   = new ProtocolMessage(MessageType.Handshake, new byte[] { 0xAB });
        var frame = EncodeFrame(msg);

        Assert.Equal((byte)MessageType.Handshake, frame[4]);
    }

    [Fact]
    public void EncodeFrame_PayloadBytesFollowTypeByte()
    {
        var payload = new byte[] { 10, 20, 30 };
        var msg     = new ProtocolMessage(MessageType.MirrorFrame, payload);
        var frame   = EncodeFrame(msg);

        Assert.Equal(payload[0], frame[5]);
        Assert.Equal(payload[1], frame[6]);
        Assert.Equal(payload[2], frame[7]);
    }

    [Fact]
    public void DecodeFrame_RoundTrip_PreservesTypeAndPayload()
    {
        var original = new ProtocolMessage(MessageType.ClipboardSync, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var frame    = EncodeFrame(original);
        var decoded  = DecodeFrame(frame);

        Assert.NotNull(decoded);
        Assert.Equal(original.Type, decoded!.Type);
        Assert.Equal(original.Payload, decoded.Payload);
    }

    [Fact]
    public async Task SendAndReceive_ViaMemoryStream_RoundTrip()
    {
        // Arrange — write a frame to a MemoryStream then read it back
        var payload  = System.Text.Encoding.UTF8.GetBytes("hello world");
        var outgoing = new ProtocolMessage(MessageType.Handshake, payload);
        var frame    = EncodeFrame(outgoing);

        using var ms = new MemoryStream(frame);

        // Act — read manually (mirrors TlsMessageChannel.ReceiveAsync logic)
        var header   = new byte[4];
        await ms.ReadExactlyAsync(header).ConfigureAwait(false);
        uint len = BinaryPrimitives.ReadUInt32BigEndian(header);

        var body = new byte[1 + len];
        await ms.ReadExactlyAsync(body).ConfigureAwait(false);

        var type     = (MessageType)body[0];
        var received = body[1..];

        // Assert
        Assert.Equal(MessageType.Handshake, type);
        Assert.Equal(payload, received);
    }

    [Fact]
    public void EncodeFrame_BigEndianLengthIsCorrect()
    {
        // 256 bytes of payload: length should be 0x00_00_01_00 in big-endian
        var payload = new byte[256];
        var msg     = new ProtocolMessage(MessageType.FileChunk, payload);
        var frame   = EncodeFrame(msg);

        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0x01, frame[2]);
        Assert.Equal(0x00, frame[3]);
    }

    [Fact]
    public void MaxPayloadBytes_ConstantMatchesSpec()
    {
        // 64 MB
        Assert.Equal(64 * 1024 * 1024, ProtocolMessage.MaxPayloadBytes);
    }

    [Fact]
    public void AllMessageTypeValues_RoundTripThroughByte()
    {
        foreach (MessageType mt in Enum.GetValues<MessageType>())
        {
            var payload = new ProtocolMessage(mt, Array.Empty<byte>());
            var frame   = EncodeFrame(payload);
            var decoded = DecodeFrame(frame);

            Assert.Equal(mt, decoded!.Type);
        }
    }
}
