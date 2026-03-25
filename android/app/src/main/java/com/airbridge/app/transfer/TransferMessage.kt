package com.airbridge.app.transfer

import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets

/**
 * Message type tags for the file-transfer sub-protocol.
 * Values align with [com.airbridge.app.transport.protocol.MessageType]:
 * FILE_TRANSFER_START = 0x10, FILE_CHUNK = 0x11,
 * FILE_TRANSFER_ACK = 0x12, FILE_TRANSFER_END = 0x13.
 */
enum class TransferMessageType(val value: Byte) {
    /** Sender → Receiver: announces a new file transfer. */
    FILE_START(0x10),
    /** Sender → Receiver: one chunk of file data. */
    FILE_CHUNK(0x11),
    /** Receiver → Sender: acknowledges receipt of a chunk or the whole file. */
    TRANSFER_ACK(0x12),
    /** Sender → Receiver: signals end-of-file; includes SHA-256 hash. */
    FILE_END(0x13),
    /** Either side: signals an error during transfer. */
    TRANSFER_ERROR(0xFF.toByte());

    companion object {
        /** Returns the [TransferMessageType] for [value], or null if unknown. */
        fun fromByte(value: Byte): TransferMessageType? =
            entries.firstOrNull { it.value == value }
    }
}

/**
 * Wire-format message sent by the sender to start a file transfer.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x10
 * [4 bytes] session-id length (N)
 * [N bytes] session-id (UTF-8)
 * [4 bytes] file-name length (M)
 * [M bytes] file-name (UTF-8)
 * [8 bytes] total-bytes (int64)
 * ```
 */
data class FileStartMessage(val sessionId: String, val fileName: String, val totalBytes: Long) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val sidBytes  = sessionId.toByteArray(StandardCharsets.UTF_8)
        val nameBytes = fileName.toByteArray(StandardCharsets.UTF_8)
        val buf = ByteBuffer.allocate(1 + 4 + sidBytes.size + 4 + nameBytes.size + 8)
        buf.put(TransferMessageType.FILE_START.value)
        buf.putInt(sidBytes.size);  buf.put(sidBytes)
        buf.putInt(nameBytes.size); buf.put(nameBytes)
        buf.putLong(totalBytes)
        return buf.array()
    }

    companion object {
        /** Deserializes a [FileStartMessage] from [data] (including the type byte at index 0). */
        fun fromBytes(data: ByteArray): FileStartMessage {
            val buf = ByteBuffer.wrap(data, 1, data.size - 1) // skip type byte
            val sidLen  = buf.int; val sidBytes  = ByteArray(sidLen).also { buf.get(it) }
            val nameLen = buf.int; val nameBytes = ByteArray(nameLen).also { buf.get(it) }
            val total   = buf.long
            return FileStartMessage(
                String(sidBytes, StandardCharsets.UTF_8),
                String(nameBytes, StandardCharsets.UTF_8),
                total
            )
        }
    }
}

/**
 * Wire-format message carrying one chunk of file data.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x11
 * [8 bytes] chunk offset (int64)
 * [4 bytes] chunk length (int32)
 * [N bytes] chunk data
 * ```
 */
data class FileChunkMessage(val offset: Long, val data: ByteArray) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val buf = ByteBuffer.allocate(1 + 8 + 4 + data.size)
        buf.put(TransferMessageType.FILE_CHUNK.value)
        buf.putLong(offset)
        buf.putInt(data.size)
        buf.put(data)
        return buf.array()
    }

    companion object {
        /** Deserializes a [FileChunkMessage] from [data] (including the type byte at index 0). */
        fun fromBytes(data: ByteArray): FileChunkMessage {
            val buf    = ByteBuffer.wrap(data, 1, data.size - 1)
            val offset = buf.long
            val len    = buf.int
            val chunk  = ByteArray(len).also { buf.get(it) }
            return FileChunkMessage(offset, chunk)
        }
    }

    // ByteArray requires manual equals/hashCode
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is FileChunkMessage) return false
        return offset == other.offset && data.contentEquals(other.data)
    }

    override fun hashCode(): Int = 31 * offset.hashCode() + data.contentHashCode()
}

/**
 * Wire-format message sent by the receiver to acknowledge a chunk or the whole transfer.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x12
 * [8 bytes] bytes-acknowledged (int64)
 * ```
 */
data class TransferAckMessage(val bytesAcknowledged: Long) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val buf = ByteBuffer.allocate(1 + 8)
        buf.put(TransferMessageType.TRANSFER_ACK.value)
        buf.putLong(bytesAcknowledged)
        return buf.array()
    }

    companion object {
        /** Deserializes a [TransferAckMessage] from [data] (including the type byte). */
        fun fromBytes(data: ByteArray): TransferAckMessage {
            val buf = ByteBuffer.wrap(data, 1, data.size - 1)
            return TransferAckMessage(buf.long)
        }
    }
}

/**
 * Wire-format message sent by the sender to signal end-of-file with a SHA-256 digest.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte  ] type = 0x13
 * [8 bytes ] total-bytes (int64)
 * [32 bytes] SHA-256 hash of the full file
 * ```
 */
data class FileEndMessage(val totalBytes: Long, val sha256Hash: ByteArray) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        require(sha256Hash.size == HASH_LENGTH) { "SHA-256 hash must be $HASH_LENGTH bytes." }
        val buf = ByteBuffer.allocate(1 + 8 + HASH_LENGTH)
        buf.put(TransferMessageType.FILE_END.value)
        buf.putLong(totalBytes)
        buf.put(sha256Hash)
        return buf.array()
    }

    companion object {
        /** Expected length of the SHA-256 hash field. */
        const val HASH_LENGTH = 32

        /** Deserializes a [FileEndMessage] from [data] (including the type byte). */
        fun fromBytes(data: ByteArray): FileEndMessage {
            val buf   = ByteBuffer.wrap(data, 1, data.size - 1)
            val total = buf.long
            val hash  = ByteArray(HASH_LENGTH).also { buf.get(it) }
            return FileEndMessage(total, hash)
        }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is FileEndMessage) return false
        return totalBytes == other.totalBytes && sha256Hash.contentEquals(other.sha256Hash)
    }

    override fun hashCode(): Int = 31 * totalBytes.hashCode() + sha256Hash.contentHashCode()
}

/**
 * Wire-format message sent by either side to signal a transfer error.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0xFF
 * [4 bytes] message length (int32)
 * [N bytes] error message (UTF-8)
 * ```
 */
data class TransferErrorMessage(val errorMessage: String) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val msgBytes = errorMessage.toByteArray(StandardCharsets.UTF_8)
        val buf = ByteBuffer.allocate(1 + 4 + msgBytes.size)
        buf.put(TransferMessageType.TRANSFER_ERROR.value)
        buf.putInt(msgBytes.size)
        buf.put(msgBytes)
        return buf.array()
    }

    companion object {
        /** Deserializes a [TransferErrorMessage] from [data] (including the type byte). */
        fun fromBytes(data: ByteArray): TransferErrorMessage {
            val buf    = ByteBuffer.wrap(data, 1, data.size - 1)
            val len    = buf.int
            val bytes  = ByteArray(len).also { buf.get(it) }
            return TransferErrorMessage(String(bytes, StandardCharsets.UTF_8))
        }
    }
}
