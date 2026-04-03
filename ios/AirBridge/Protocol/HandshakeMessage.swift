import Foundation

/// Codable model for the HANDSHAKE JSON payload.
/// The HANDSHAKE is the only message whose payload does NOT contain a leading type byte.
struct HandshakeMessage: Codable {
    let deviceId: String
    let deviceName: String
    let deviceType: String

    // MARK: - Serialisation

    /// Encodes to UTF-8 JSON (no type byte).
    func toPayload() throws -> Data {
        try JSONEncoder().encode(self)
    }

    /// Decodes from raw UTF-8 JSON bytes (no type byte).
    static func fromPayload(_ data: Data) throws -> HandshakeMessage {
        try JSONDecoder().decode(HandshakeMessage.self, from: data)
    }
}
