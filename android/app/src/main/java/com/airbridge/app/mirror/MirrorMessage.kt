package com.airbridge.app.mirror

import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets

/**
 * Message type tags for the screen-mirror sub-protocol.
 * Values align with [com.airbridge.app.transport.protocol.MessageType]:
 * MIRROR_START = 0x20, MIRROR_FRAME = 0x21, MIRROR_STOP = 0x22.
 */
enum class MirrorMessageType(val value: Byte) {
    /** Android → Windows: announces a new mirror session with stream parameters. */
    MIRROR_START(0x20.toByte()),
    /** Android → Windows: one encoded H.264/H.265 frame. */
    MIRROR_FRAME(0x21.toByte()),
    /** Either direction: graceful teardown of a mirror session. */
    MIRROR_STOP(0x22.toByte());

    companion object {
        /** Returns the [MirrorMessageType] for [value], or null if unknown. */
        fun fromByte(value: Byte): MirrorMessageType? =
            entries.firstOrNull { it.value == value }
    }
}

/**
 * Wire-format message sent by Android to start a mirror session.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x20
 * [4 bytes] session-id length (N)
 * [N bytes] session-id (UTF-8)
 * [4 bytes] width  (int32, pixels)
 * [4 bytes] height (int32, pixels)
 * [4 bytes] fps    (int32)
 * [4 bytes] codec string length (M)
 * [M bytes] codec  (UTF-8, e.g. "H264")
 * ```
 */
data class MirrorStartMessage(
    val sessionId: String,
    val width: Int,
    val height: Int,
    val fps: Int,
    val codec: String
) {
    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val sidBytes   = sessionId.toByteArray(StandardCharsets.UTF_8)
        val codecBytes = codec.toByteArray(StandardCharsets.UTF_8)
        val buf = ByteBuffer.allocate(
            1 + 4 + sidBytes.size + 4 + 4 + 4 + 4 + codecBytes.size
        )
        buf.put(MirrorMessageType.MIRROR_START.value)
        buf.putInt(sidBytes.size);  buf.put(sidBytes)
        buf.putInt(width)
        buf.putInt(height)
        buf.putInt(fps)
        buf.putInt(codecBytes.size); buf.put(codecBytes)
        return buf.array()
    }

    companion object {
        /**
         * Deserializes a [MirrorStartMessage] from [data]
         * (including the type byte at index 0).
         */
        fun fromBytes(data: ByteArray): MirrorStartMessage {
            val buf      = ByteBuffer.wrap(data, 1, data.size - 1) // skip type byte
            val sidLen   = buf.int; val sidBytes   = ByteArray(sidLen).also { buf.get(it) }
            val width    = buf.int
            val height   = buf.int
            val fps      = buf.int
            val codecLen = buf.int; val codecBytes = ByteArray(codecLen).also { buf.get(it) }
            return MirrorStartMessage(
                sessionId = String(sidBytes, StandardCharsets.UTF_8),
                width     = width,
                height    = height,
                fps       = fps,
                codec     = String(codecBytes, StandardCharsets.UTF_8)
            )
        }
    }
}

/**
 * Wire-format message carrying one encoded H.264/H.265 NAL buffer.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x21
 * [4 bytes] session-id length (N)
 * [N bytes] session-id (UTF-8)
 * [8 bytes] timestamp-ms (int64, presentation time in milliseconds)
 * [1 byte ] flags (bit 0 = keyframe)
 * [4 bytes] payload length (P)
 * [P bytes] H.264 NAL data
 * ```
 */
data class MirrorFrameMessage(
    val sessionId: String,
    val timestampMs: Long,
    val isKeyFrame: Boolean,
    val nalData: ByteArray
) {
    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val sidBytes = sessionId.toByteArray(StandardCharsets.UTF_8)
        val flags: Byte = if (isKeyFrame) 0x01 else 0x00
        val buf = ByteBuffer.allocate(
            1 + 4 + sidBytes.size + 8 + 1 + 4 + nalData.size
        )
        buf.put(MirrorMessageType.MIRROR_FRAME.value)
        buf.putInt(sidBytes.size); buf.put(sidBytes)
        buf.putLong(timestampMs)
        buf.put(flags)
        buf.putInt(nalData.size)
        buf.put(nalData)
        return buf.array()
    }

    companion object {
        /**
         * Deserializes a [MirrorFrameMessage] from [data]
         * (including the type byte at index 0).
         */
        fun fromBytes(data: ByteArray): MirrorFrameMessage {
            val buf      = ByteBuffer.wrap(data, 1, data.size - 1)
            val sidLen   = buf.int; val sidBytes = ByteArray(sidLen).also { buf.get(it) }
            val tsMs     = buf.long
            val flags    = buf.get()
            val nalLen   = buf.int
            val nalData  = ByteArray(nalLen).also { buf.get(it) }
            return MirrorFrameMessage(
                sessionId    = String(sidBytes, StandardCharsets.UTF_8),
                timestampMs  = tsMs,
                isKeyFrame   = (flags.toInt() and 0x01) != 0,
                nalData      = nalData
            )
        }
    }

    // ByteArray requires manual equals/hashCode
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MirrorFrameMessage) return false
        return sessionId   == other.sessionId   &&
               timestampMs == other.timestampMs &&
               isKeyFrame  == other.isKeyFrame  &&
               nalData.contentEquals(other.nalData)
    }

    override fun hashCode(): Int {
        var result = sessionId.hashCode()
        result = 31 * result + timestampMs.hashCode()
        result = 31 * result + isKeyFrame.hashCode()
        result = 31 * result + nalData.contentHashCode()
        return result
    }
}

/**
 * Wire-format message sent by either side to signal graceful mirror teardown.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x22
 * [4 bytes] session-id length (N)
 * [N bytes] session-id (UTF-8)
 * ```
 */
data class MirrorStopMessage(val sessionId: String) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val sidBytes = sessionId.toByteArray(StandardCharsets.UTF_8)
        val buf = ByteBuffer.allocate(1 + 4 + sidBytes.size)
        buf.put(MirrorMessageType.MIRROR_STOP.value)
        buf.putInt(sidBytes.size); buf.put(sidBytes)
        return buf.array()
    }

    companion object {
        /**
         * Deserializes a [MirrorStopMessage] from [data]
         * (including the type byte at index 0).
         */
        fun fromBytes(data: ByteArray): MirrorStopMessage {
            val buf    = ByteBuffer.wrap(data, 1, data.size - 1)
            val sidLen = buf.int; val sidBytes = ByteArray(sidLen).also { buf.get(it) }
            return MirrorStopMessage(String(sidBytes, StandardCharsets.UTF_8))
        }
    }
}
