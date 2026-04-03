import Foundation

/// A decoded AirBridge protocol message.
/// For all message types except HANDSHAKE the raw payload includes the type byte
/// at payload[0] (Android convention). The framing layer stores the entire payload
/// as-is; individual message parsers are responsible for skipping payload[0].
struct ProtocolMessage {
    /// The parsed message type.
    let type: MessageType
    /// Raw payload bytes. For HANDSHAKE this is pure JSON. For all other types
    /// payload[0] duplicates the type byte.
    let payload: Data

    // MARK: - Frame building

    /// Builds a complete wire frame:
    /// [4-byte big-endian payload_length][1-byte type][payload_length bytes payload]
    /// For HANDSHAKE the payload must NOT contain a type byte.
    /// For all other types the payload must already contain the type byte at [0].
    func frame() -> Data {
        var out = Data()
        let length = UInt32(payload.count).bigEndian
        withUnsafeBytes(of: length) { out.append(contentsOf: $0) }
        out.append(type.rawValue)
        out.append(payload)
        return out
    }
}
