import Foundation

/// Touch / input event kinds mirroring the Android protocol.
enum InputEventKind: UInt8 {
    case touch      = 0
    case key        = 1
    case mouse      = 2
    case longPress  = 3
    case scroll     = 4
}

/// An input event to be sent from iPad to Windows.
struct InputEventMessage {
    let sessionId: String
    let kind: InputEventKind
    let normalizedX: Float
    let normalizedY: Float
    let keycode: Int32?
    let metaState: Int32

    // MARK: - Serialisation

    /// Builds payload with type byte at [0].
    func toPayload() -> Data {
        var data = Data()

        // [0] type byte
        data.append(MessageType.inputEvent.rawValue)

        // sessionId (4-byte length + UTF-8 bytes)
        let sessionIdBytes = sessionId.data(using: .utf8) ?? Data()
        var sessionIdLen = Int32(sessionIdBytes.count).bigEndian
        withUnsafeBytes(of: &sessionIdLen) { data.append(contentsOf: $0) }
        data.append(sessionIdBytes)

        // InputEventKind
        data.append(kind.rawValue)

        // normalizedX (big-endian float)
        var xBits = normalizedX.bitPattern.bigEndian
        withUnsafeBytes(of: &xBits) { data.append(contentsOf: $0) }

        // normalizedY (big-endian float)
        var yBits = normalizedY.bitPattern.bigEndian
        withUnsafeBytes(of: &yBits) { data.append(contentsOf: $0) }

        // hasKeycode + optional keycode
        if let kc = keycode {
            data.append(1)
            var kcBE = kc.bigEndian
            withUnsafeBytes(of: &kcBE) { data.append(contentsOf: $0) }
        } else {
            data.append(0)
            // Still include 4-byte placeholder so the receiver does not need
            // conditional parsing for keycode length.
            var zero: Int32 = 0
            withUnsafeBytes(of: &zero) { data.append(contentsOf: $0) }
        }

        // metaState
        var msBE = metaState.bigEndian
        withUnsafeBytes(of: &msBE) { data.append(contentsOf: $0) }

        return data
    }

    // MARK: - Deserialisation

    /// Parses from raw payload bytes where payload[0] is the type byte (skipped).
    static func fromPayload(_ data: Data) throws -> InputEventMessage {
        guard data.count >= 2 else {
            throw AirBridgeError.malformedPayload("INPUT_EVENT too short")
        }
        // Skip payload[0]
        var offset = 1

        let sessionIdLen = Int(data.readInt32BE(at: offset))
        offset += 4
        guard data.count >= offset + sessionIdLen else {
            throw AirBridgeError.malformedPayload("INPUT_EVENT sessionId truncated")
        }
        guard let sessionId = String(bytes: data[offset..<(offset + sessionIdLen)], encoding: .utf8) else {
            throw AirBridgeError.malformedPayload("INPUT_EVENT sessionId not UTF-8")
        }
        offset += sessionIdLen

        guard let kind = InputEventKind(rawValue: data[offset]) else {
            throw AirBridgeError.malformedPayload("INPUT_EVENT unknown kind")
        }
        offset += 1

        var xBitsRaw: UInt32 = 0
        withUnsafeMutableBytes(of: &xBitsRaw) { $0.copyBytes(from: data[offset..<(offset + 4)]) }
        let normalizedX = Float(bitPattern: UInt32(bigEndian: xBitsRaw))
        offset += 4

        var yBitsRaw: UInt32 = 0
        withUnsafeMutableBytes(of: &yBitsRaw) { $0.copyBytes(from: data[offset..<(offset + 4)]) }
        let normalizedY = Float(bitPattern: UInt32(bigEndian: yBitsRaw))
        offset += 4

        let hasKeycode = data[offset]
        offset += 1

        var keycode: Int32?
        if hasKeycode != 0 {
            keycode = data.readInt32BE(at: offset)
        }
        offset += 4

        let metaState = data.readInt32BE(at: offset)

        return InputEventMessage(sessionId: sessionId, kind: kind,
                                 normalizedX: normalizedX, normalizedY: normalizedY,
                                 keycode: keycode, metaState: metaState)
    }
}
