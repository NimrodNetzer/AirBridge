package com.airbridge.app.transport.protocol

/**
 * All message types in the AirBridge wire protocol v1.
 * Values match the Type byte in the wire format.
 * See protocol/v1/spec.md for full documentation.
 */
enum class MessageType(val value: Byte) {
    // Connection & Pairing
    HANDSHAKE(0x01),
    HANDSHAKE_ACK(0x02),
    PAIRING_REQUEST(0x03),
    PAIRING_RESPONSE(0x04),

    // File Transfer
    FILE_TRANSFER_START(0x10),
    FILE_CHUNK(0x11),
    FILE_TRANSFER_ACK(0x12),
    FILE_TRANSFER_END(0x13),

    // Screen Mirror
    MIRROR_START(0x20),
    MIRROR_FRAME(0x21),
    MIRROR_STOP(0x22),

    // Input & Clipboard
    INPUT_EVENT(0x30),
    CLIPBOARD_SYNC(0x40),

    // Keepalive
    PING(0xF0.toByte()),
    PONG(0xF1.toByte()),

    // Error
    ERROR(0xFF.toByte());

    companion object {
        fun fromByte(value: Byte): MessageType =
            entries.firstOrNull { it.value == value }
                ?: throw IllegalArgumentException("Unknown message type: 0x${value.toString(16)}")
    }
}

/**
 * A framed protocol message as read/written on the wire.
 * [payload] holds raw Protobuf bytes; callers deserialize based on [type].
 */
data class ProtocolMessage(
    val type: MessageType,
    val payload: ByteArray
) {
    companion object {
        const val PROTOCOL_VERSION = 1
        const val DEFAULT_PORT = 47821
        const val MAX_PAYLOAD_BYTES = 64 * 1024 * 1024  // 64 MB
    }

    // ByteArray requires manual equals/hashCode
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolMessage) return false
        return type == other.type && payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int = 31 * type.hashCode() + payload.contentHashCode()
}
