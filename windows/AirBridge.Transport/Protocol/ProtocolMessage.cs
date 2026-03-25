namespace AirBridge.Transport.Protocol;

/// <summary>
/// All message types in the AirBridge wire protocol v1.
/// Values match the Type byte in the wire format.
/// See protocol/v1/spec.md for full documentation.
/// </summary>
public enum MessageType : byte
{
    // Connection & Pairing
    Handshake       = 0x01,
    HandshakeAck    = 0x02,
    PairingRequest  = 0x03,
    PairingResponse = 0x04,

    // File Transfer
    FileTransferStart = 0x10,
    FileChunk         = 0x11,
    FileTransferAck   = 0x12,
    FileTransferEnd   = 0x13,

    // Screen Mirror
    MirrorStart = 0x20,
    MirrorFrame = 0x21,
    MirrorStop  = 0x22,

    // Input & Clipboard
    InputEvent     = 0x30,
    ClipboardSync  = 0x40,

    // Keepalive
    Ping = 0xF0,
    Pong = 0xF1,

    // Error
    Error = 0xFF
}

/// <summary>
/// Base representation of a framed protocol message.
/// <see cref="Payload"/> holds the raw Protobuf bytes; callers
/// deserialize to the concrete message type based on <see cref="Type"/>.
/// </summary>
public sealed record ProtocolMessage(MessageType Type, byte[] Payload)
{
    /// <summary>Protocol version this library implements.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Default TCP port for AirBridge connections.</summary>
    public const int DefaultPort = 47821;

    /// <summary>Maximum allowed payload size (64 MB).</summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;
}
