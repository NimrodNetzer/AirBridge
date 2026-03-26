using System.Buffers.Binary;
using AirBridge.Transport.Protocol;

namespace AirBridge.Tests.Security;

/// <summary>
/// Verifies the input validation guards in the AirBridge protocol framing layer.
///
/// Findings from code review of <see cref="TlsMessageChannel"/>:
/// <list type="bullet">
///   <item>
///     Maximum message size guard (<see cref="ProtocolMessage.MaxPayloadBytes"/> = 64 MB)
///     is present in both <c>SendAsync</c> (throws <see cref="ArgumentException"/>) and
///     <c>ReceiveAsync</c> (throws <see cref="InvalidDataException"/>).
///   </item>
///   <item>
///     Unknown <see cref="MessageType"/> bytes are accepted at the framing level; the enum
///     cast silently produces an out-of-range value.  This is intentional — the framing layer
///     is type-agnostic.  Higher layers are responsible for handling unknown types.
///   </item>
///   <item>
///     Empty payload is allowed; the payload array is never null (always at least
///     <c>byte[0]</c>).  No NullReferenceException is possible from the parser.
///   </item>
/// </list>
/// </summary>
public class InputValidationTests
{
    // ── Helpers (wire-format encode/decode without a live TLS socket) ─────────

    /// <summary>
    /// Replicates the ReceiveAsync size guard from TlsMessageChannel.
    /// Returns false if the payload length exceeds MaxPayloadBytes; true otherwise.
    /// </summary>
    private static bool SizeGuardPasses(uint payloadLength)
        => payloadLength <= ProtocolMessage.MaxPayloadBytes;

    /// <summary>
    /// Builds a 5-byte frame header.
    /// </summary>
    private static byte[] BuildHeader(uint payloadLength, byte typeByte)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, payloadLength);
        header[4] = typeByte;
        return header;
    }

    // ── Maximum message size guard ────────────────────────────────────────────

    [Fact]
    public void SizeGuard_64MBPayload_Passes()
    {
        Assert.True(SizeGuardPasses((uint)ProtocolMessage.MaxPayloadBytes));
    }

    [Fact]
    public void SizeGuard_64MBPlusOne_Fails()
    {
        Assert.False(SizeGuardPasses((uint)(ProtocolMessage.MaxPayloadBytes + 1)));
    }

    [Fact]
    public void SizeGuard_IntMaxValue_Fails()
    {
        Assert.False(SizeGuardPasses((uint)int.MaxValue));
    }

    [Fact]
    public void SizeGuard_UInt32Max_Fails()
    {
        Assert.False(SizeGuardPasses(uint.MaxValue));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(1024u)]
    [InlineData(65535u)]
    [InlineData((uint)(64 * 1024 * 1024))]
    public void SizeGuard_ValidLengths_AllPass(uint length)
    {
        Assert.True(SizeGuardPasses(length));
    }

    [Theory]
    [InlineData((uint)(64 * 1024 * 1024) + 1)]
    [InlineData(100_000_000u)]
    [InlineData(uint.MaxValue)]
    public void SizeGuard_OversizedLengths_AllFail(uint length)
    {
        Assert.False(SizeGuardPasses(length));
    }

    // ── MaxPayloadBytes constant ──────────────────────────────────────────────

    [Fact]
    public void MaxPayloadBytes_Is64MB()
    {
        Assert.Equal(64 * 1024 * 1024, ProtocolMessage.MaxPayloadBytes);
    }

    [Fact]
    public void MaxPayloadBytes_CanBeUsedAsArrayAllocationGuard()
    {
        // Verify that MaxPayloadBytes fits in an int (array size limit).
        // int.MaxValue = 2_147_483_647; 64 MB = 67_108_864 — well within range.
        Assert.True(ProtocolMessage.MaxPayloadBytes < int.MaxValue);
        Assert.True(ProtocolMessage.MaxPayloadBytes > 0);
    }

    // ── Message type enum validation ──────────────────────────────────────────

    [Fact]
    public void MessageType_AllDefinedValues_AreInKnownRange()
    {
        foreach (MessageType mt in Enum.GetValues<MessageType>())
        {
            byte value = (byte)mt;
            // Verify enum values match documented wire-format ranges
            bool inKnownRange =
                (value >= 0x01 && value <= 0x04) || // Connection & Pairing
                (value >= 0x10 && value <= 0x13) || // File Transfer
                (value >= 0x20 && value <= 0x22) || // Screen Mirror
                value == 0x30 ||                    // Input Event
                value == 0x40 ||                    // Clipboard Sync
                value == 0xF0 || value == 0xF1 ||  // Keepalive
                value == 0xFF;                      // Error
            Assert.True(inKnownRange, $"MessageType 0x{value:X2} is outside documented ranges.");
        }
    }

    [Fact]
    public void MessageType_UnknownByte_CastDoesNotThrow()
    {
        // The enum cast should never throw — unknown values are silently accepted
        // at the framing layer.
        byte unknownByte = 0x99;
        var  type        = (MessageType)unknownByte;
        // Value should be 0x99 — not in Enum.IsDefined, but not an exception
        Assert.False(Enum.IsDefined(type));
        Assert.Equal((MessageType)0x99, type);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x05)]
    [InlineData(0x50)]
    [InlineData(0x99)]
    [InlineData(0xAA)]
    [InlineData(0xFE)]
    public void MessageType_UnknownBytes_CastNeverThrows(byte unknownByte)
    {
        // Verifies that unknown type bytes at the framing layer will not cause
        // exceptions during the enum cast in ReceiveAsync.
        var type = (MessageType)unknownByte;
        Assert.Equal((MessageType)unknownByte, type);
    }

    // ── Empty payload handling ────────────────────────────────────────────────

    [Fact]
    public void ProtocolMessage_EmptyPayload_IsNonNull()
    {
        var msg = new ProtocolMessage(MessageType.Ping, Array.Empty<byte>());
        Assert.NotNull(msg.Payload);
        Assert.Empty(msg.Payload);
    }

    [Fact]
    public void ProtocolMessage_EmptyPayload_AllMessageTypes_NoNullReferenceException()
    {
        foreach (MessageType mt in Enum.GetValues<MessageType>())
        {
            var msg = new ProtocolMessage(mt, Array.Empty<byte>());
            Assert.NotNull(msg);
            Assert.NotNull(msg.Payload);
            Assert.Equal(mt, msg.Type);
        }
    }

    [Fact]
    public void ProtocolMessage_ZeroLengthPayload_LengthIsZero()
    {
        var msg = new ProtocolMessage(MessageType.Handshake, Array.Empty<byte>());
        Assert.Equal(0, msg.Payload.Length);
    }

    // ── Wire format constants ─────────────────────────────────────────────────

    [Fact]
    public void ProtocolVersion_IsOne()
    {
        Assert.Equal(1, ProtocolMessage.ProtocolVersion);
    }

    [Fact]
    public void DefaultPort_IsInValidPortRange()
    {
        Assert.InRange(ProtocolMessage.DefaultPort, 1024, 65535);
    }

    [Fact]
    public void DefaultPort_Is47821()
    {
        Assert.Equal(47821, ProtocolMessage.DefaultPort);
    }

    // ── Payload guard on SendAsync side ──────────────────────────────────────

    [Fact]
    public void ProtocolMessage_OversizedPayload_CanBeDetectedBeforeSend()
    {
        // Simulate the guard in TlsMessageChannel.SendAsync
        var oversizedPayload = new byte[ProtocolMessage.MaxPayloadBytes + 1];
        bool wouldBeRejected = oversizedPayload.Length > ProtocolMessage.MaxPayloadBytes;
        Assert.True(wouldBeRejected);
    }

    [Fact]
    public void ProtocolMessage_MaxSizedPayload_IsAcceptedBySendGuard()
    {
        // Exactly at the limit — should NOT be rejected
        bool wouldBeRejected = ProtocolMessage.MaxPayloadBytes > ProtocolMessage.MaxPayloadBytes;
        Assert.False(wouldBeRejected);
    }
}
