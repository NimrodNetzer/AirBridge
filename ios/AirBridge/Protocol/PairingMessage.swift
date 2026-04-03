import Foundation

// MARK: - PAIRING_REQUEST

/// Helper that builds / parses the PAIRING_REQUEST payload.
/// Wire layout (payload, no embedded type byte — caller wraps in ProtocolMessage):
/// [2-byte big-endian key_length][key_length bytes Ed25519 public key][6 ASCII digit PIN]
struct PairingRequestPayload {
    let publicKey: Data   // 32-byte Ed25519 public key raw bytes
    let pin: String       // 6 ASCII digit PIN

    /// Builds payload WITHOUT the type byte (caller provides type byte via ProtocolMessage).
    func toPayload() -> Data {
        var data = Data()
        // key_length (2-byte big-endian)
        var keyLen = UInt16(publicKey.count).bigEndian
        withUnsafeBytes(of: &keyLen) { data.append(contentsOf: $0) }
        // public key bytes
        data.append(publicKey)
        // 6 ASCII digit PIN
        data.append(contentsOf: pin.utf8)
        return data
    }

    /// Parses from raw payload bytes (no type byte).
    static func fromPayload(_ data: Data) throws -> PairingRequestPayload {
        guard data.count >= 2 else {
            throw AirBridgeError.malformedPayload("PAIRING_REQUEST too short")
        }
        var offset = 0
        let keyLen = Int(UInt16(bigEndian: data.withUnsafeBytes {
            $0.load(fromByteOffset: offset, as: UInt16.self)
        }))
        offset += 2
        guard data.count >= offset + keyLen + 6 else {
            throw AirBridgeError.malformedPayload("PAIRING_REQUEST truncated")
        }
        let publicKey = Data(data[offset..<(offset + keyLen)])
        offset += keyLen
        guard let pin = String(bytes: data[offset..<(offset + 6)], encoding: .ascii) else {
            throw AirBridgeError.malformedPayload("PAIRING_REQUEST PIN not ASCII")
        }
        return PairingRequestPayload(publicKey: publicKey, pin: pin)
    }
}

// MARK: - PAIRING_RESPONSE

/// Helper that builds / parses the PAIRING_RESPONSE payload (no embedded type byte).
/// Wire layout:
/// [1 byte accepted: 1=yes 0=no][2-byte big-endian key_length][key_length bytes Ed25519 public key]
struct PairingResponsePayload {
    let accepted: Bool
    let publicKey: Data   // Windows Ed25519 public key raw bytes

    /// Builds payload WITHOUT the type byte.
    func toPayload() -> Data {
        var data = Data()
        data.append(accepted ? 1 : 0)
        var keyLen = UInt16(publicKey.count).bigEndian
        withUnsafeBytes(of: &keyLen) { data.append(contentsOf: $0) }
        data.append(publicKey)
        return data
    }

    /// Parses from raw payload bytes (no type byte).
    static func fromPayload(_ data: Data) throws -> PairingResponsePayload {
        guard data.count >= 3 else {
            throw AirBridgeError.malformedPayload("PAIRING_RESPONSE too short")
        }
        var offset = 0
        let accepted = data[offset] == 1
        offset += 1
        let keyLen = Int(UInt16(bigEndian: data.withUnsafeBytes {
            $0.load(fromByteOffset: offset, as: UInt16.self)
        }))
        offset += 2
        guard data.count >= offset + keyLen else {
            throw AirBridgeError.malformedPayload("PAIRING_RESPONSE public key truncated")
        }
        let publicKey = Data(data[offset..<(offset + keyLen)])
        return PairingResponsePayload(accepted: accepted, publicKey: publicKey)
    }
}
