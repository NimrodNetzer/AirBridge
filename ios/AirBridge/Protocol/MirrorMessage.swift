import Foundation

// MARK: - MIRROR_START

/// Decoded MIRROR_START payload.
struct MirrorStartMessage {
    enum Mode: UInt8 {
        case phoneWindow    = 0x01
        case tabletDisplay  = 0x02
    }
    enum Codec: UInt8 {
        case h264 = 0x01
        case h265 = 0x02
    }

    let mode: Mode
    let codec: Codec
    let width: UInt16
    let height: UInt16
    let fps: UInt8
    let sessionId: String

    /// Parses from raw payload bytes where payload[0] is the type byte (skipped).
    static func fromPayload(_ data: Data) throws -> MirrorStartMessage {
        guard data.count >= 8 else {
            throw AirBridgeError.malformedPayload("MIRROR_START too short")
        }
        // Skip payload[0] (type byte)
        var offset = 1
        guard let mode = Mode(rawValue: data[offset]) else {
            throw AirBridgeError.malformedPayload("Unknown MIRROR_START mode")
        }
        offset += 1
        guard let codec = Codec(rawValue: data[offset]) else {
            throw AirBridgeError.malformedPayload("Unknown MIRROR_START codec")
        }
        offset += 1
        let width = data.readUInt16BE(at: offset)
        offset += 2
        let height = data.readUInt16BE(at: offset)
        offset += 2
        let fps = data[offset]
        offset += 1
        guard data.count >= offset + 4 else {
            throw AirBridgeError.malformedPayload("MIRROR_START sessionId length missing")
        }
        let sessionIdLength = Int(data.readInt32BE(at: offset))
        offset += 4
        guard data.count >= offset + sessionIdLength else {
            throw AirBridgeError.malformedPayload("MIRROR_START sessionId truncated")
        }
        guard let sessionId = String(bytes: data[offset..<(offset + sessionIdLength)], encoding: .utf8) else {
            throw AirBridgeError.malformedPayload("MIRROR_START sessionId not UTF-8")
        }
        return MirrorStartMessage(mode: mode, codec: codec, width: width, height: height, fps: fps, sessionId: sessionId)
    }
}

// MARK: - MIRROR_FRAME

/// Decoded MIRROR_FRAME payload.
struct MirrorFrameMessage {
    let isKeyFrame: Bool
    let pts: Int64   // microseconds
    let nalData: Data

    /// Parses from raw payload bytes where payload[0] is the type byte (skipped).
    static func fromPayload(_ data: Data) throws -> MirrorFrameMessage {
        guard data.count >= 15 else {
            throw AirBridgeError.malformedPayload("MIRROR_FRAME too short")
        }
        // Skip payload[0] (type byte)
        var offset = 1
        let flags = data[offset]
        let isKeyFrame = (flags & 0x01) != 0
        offset += 1
        let pts = data.readInt64BE(at: offset)
        offset += 8
        let nalLength = Int(data.readInt32BE(at: offset))
        offset += 4
        guard data.count >= offset + nalLength else {
            throw AirBridgeError.malformedPayload("MIRROR_FRAME NAL data truncated")
        }
        let nalData = data[offset..<(offset + nalLength)]
        return MirrorFrameMessage(isKeyFrame: isKeyFrame, pts: pts, nalData: Data(nalData))
    }
}

// MARK: - MIRROR_STOP

/// Decoded MIRROR_STOP payload.
struct MirrorStopMessage {
    let reasonCode: UInt8

    /// Parses from raw payload bytes where payload[0] is the type byte (skipped).
    static func fromPayload(_ data: Data) throws -> MirrorStopMessage {
        // Skip payload[0] (type byte); payload[1] is reason code
        guard data.count >= 2 else {
            throw AirBridgeError.malformedPayload("MIRROR_STOP too short")
        }
        return MirrorStopMessage(reasonCode: data[1])
    }

    /// Builds payload with type byte at [0].
    func toPayload() -> Data {
        var d = Data()
        d.append(MessageType.mirrorStop.rawValue)
        d.append(reasonCode)
        return d
    }
}

// MARK: - Data helpers

extension Data {
    func readUInt16BE(at offset: Int) -> UInt16 {
        let hi = UInt16(self[offset])
        let lo = UInt16(self[offset + 1])
        return (hi << 8) | lo
    }

    func readInt32BE(at offset: Int) -> Int32 {
        var value: Int32 = 0
        withUnsafeMutableBytes(of: &value) { (dst: UnsafeMutableRawBufferPointer) in
            dst.copyBytes(from: self[offset..<(offset + 4)])
        }
        return Int32(bigEndian: value)
    }

    func readInt64BE(at offset: Int) -> Int64 {
        var value: Int64 = 0
        withUnsafeMutableBytes(of: &value) { (dst: UnsafeMutableRawBufferPointer) in
            dst.copyBytes(from: self[offset..<(offset + 8)])
        }
        return Int64(bigEndian: value)
    }
}

// MARK: - Errors

enum AirBridgeError: Error, LocalizedError {
    case malformedPayload(String)
    case connectionFailed(String)
    case pairingFailed(String)
    case decodingFailed(String)
    case timeout

    var errorDescription: String? {
        switch self {
        case .malformedPayload(let msg): return "Malformed payload: \(msg)"
        case .connectionFailed(let msg): return "Connection failed: \(msg)"
        case .pairingFailed(let msg):    return "Pairing failed: \(msg)"
        case .decodingFailed(let msg):   return "Decoding failed: \(msg)"
        case .timeout:                   return "Operation timed out"
        }
    }
}
