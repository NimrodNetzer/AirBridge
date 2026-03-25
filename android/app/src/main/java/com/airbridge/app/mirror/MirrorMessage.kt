package com.airbridge.app.mirror

import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets

/**
 * Message type tags for the screen-mirror sub-protocol.
 * Values align with [com.airbridge.app.transport.protocol.MessageType]:
 * MIRROR_START = 0x20, MIRROR_FRAME = 0x21, MIRROR_STOP = 0x22.
 */
enum class MirrorMessageType(val value: Byte) {
    /** Initiator → Receiver: announces a new mirror session. */
    MIRROR_START(0x20),
    /** Source → Sink: one H.264 NAL unit. */
    MIRROR_FRAME(0x21),
    /** Either side: terminates the mirror session. */
    MIRROR_STOP(0x22),
    /** Windows → Android: a pointer, key, or mouse input event to inject on the phone. */
    INPUT_EVENT(0x30);

    companion object {
        fun fromByte(value: Byte): MirrorMessageType? =
            entries.firstOrNull { it.value == value }
    }
}

/** Codec negotiated in [MirrorStartMessage]. */
enum class MirrorCodec(val value: Byte) {
    H264(0x01),
    H265(0x02);

    companion object {
        fun fromByte(value: Byte): MirrorCodec =
            entries.firstOrNull { it.value == value } ?: H264
    }
}

/**
 * Mirror mode carried in [MirrorStartMessage].
 * Tells the receiver whether it is the sink or the source.
 */
enum class MirrorSessionMode(val value: Byte) {
    /** Android is the source; Windows renders a floating window. */
    PHONE_WINDOW(0x01),
    /** Windows (IddCx) is the source; Android renders full-screen. */
    TABLET_DISPLAY(0x02);

    companion object {
        fun fromByte(value: Byte): MirrorSessionMode =
            entries.firstOrNull { it.value == value } ?: PHONE_WINDOW
    }
}

// ── MirrorStartMessage ─────────────────────────────────────────────────────

/**
 * Wire-format message that opens a mirror session.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x20
 * [1 byte ] mode  (MirrorSessionMode value)
 * [1 byte ] codec (MirrorCodec value)
 * [2 bytes] width  (uint16)
 * [2 bytes] height (uint16)
 * [1 byte ] fps
 * [4 bytes] session-id length (N)
 * [N bytes] session-id (UTF-8)
 * ```
 */
data class MirrorStartMessage(
    val mode:      MirrorSessionMode,
    val codec:     MirrorCodec,
    val width:     Int,
    val height:    Int,
    val fps:       Byte,
    val sessionId: String
) {
    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val sidBytes = sessionId.toByteArray(StandardCharsets.UTF_8)
        val buf = ByteBuffer.allocate(12 + sidBytes.size)
        buf.put(MirrorMessageType.MIRROR_START.value)
        buf.put(mode.value)
        buf.put(codec.value)
        buf.putShort(width.toShort())
        buf.putShort(height.toShort())
        buf.put(fps)
        buf.putInt(sidBytes.size)
        buf.put(sidBytes)
        return buf.array()
    }

    companion object {
        /** Deserializes a [MirrorStartMessage] from [data] (type byte at index 0). */
        fun fromBytes(data: ByteArray): MirrorStartMessage {
            val buf    = ByteBuffer.wrap(data, 1, data.size - 1)
            val mode   = MirrorSessionMode.fromByte(buf.get())
            val codec  = MirrorCodec.fromByte(buf.get())
            val width  = buf.short.toInt() and 0xFFFF
            val height = buf.short.toInt() and 0xFFFF
            val fps    = buf.get()
            val sidLen = buf.int
            val sidBytes = ByteArray(sidLen).also { buf.get(it) }
            return MirrorStartMessage(mode, codec, width, height, fps,
                String(sidBytes, StandardCharsets.UTF_8))
        }
    }
}

// ── MirrorFrameMessage ─────────────────────────────────────────────────────

/**
 * Wire-format message carrying one H.264 NAL unit from source to sink.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x21
 * [1 byte ] flags: bit 0 = isKeyFrame
 * [8 bytes] presentation timestamp in microseconds (int64)
 * [4 bytes] NAL data length (N)
 * [N bytes] H.264 NAL data
 * ```
 */
data class MirrorFrameMessage(
    val isKeyFrame:              Boolean,
    val presentationTimestampUs: Long,
    val nalData:                 ByteArray
) {
    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val buf = ByteBuffer.allocate(1 + 1 + 8 + 4 + nalData.size)
        buf.put(MirrorMessageType.MIRROR_FRAME.value)
        buf.put(if (isKeyFrame) 0x01.toByte() else 0x00.toByte())
        buf.putLong(presentationTimestampUs)
        buf.putInt(nalData.size)
        buf.put(nalData)
        return buf.array()
    }

    companion object {
        /** Deserializes a [MirrorFrameMessage] from [data] (type byte at index 0). */
        fun fromBytes(data: ByteArray): MirrorFrameMessage {
            val buf       = ByteBuffer.wrap(data, 1, data.size - 1)
            val flags     = buf.get()
            val keyFrame  = (flags.toInt() and 0x01) != 0
            val pts       = buf.long
            val nalLen    = buf.int
            val nal       = ByteArray(nalLen).also { buf.get(it) }
            return MirrorFrameMessage(keyFrame, pts, nal)
        }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MirrorFrameMessage) return false
        return isKeyFrame == other.isKeyFrame &&
               presentationTimestampUs == other.presentationTimestampUs &&
               nalData.contentEquals(other.nalData)
    }

    override fun hashCode(): Int {
        var result = isKeyFrame.hashCode()
        result = 31 * result + presentationTimestampUs.hashCode()
        result = 31 * result + nalData.contentHashCode()
        return result
    }
}

// ── MirrorStopMessage ──────────────────────────────────────────────────────

/**
 * Wire-format message that terminates a mirror session.
 *
 * Binary layout:
 * ```
 * [1 byte] type = 0x22
 * [1 byte] reason code (0 = normal, 1 = error)
 * ```
 */
data class MirrorStopMessage(val reasonCode: Byte = 0) {

    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray = byteArrayOf(MirrorMessageType.MIRROR_STOP.value, reasonCode)

    companion object {
        /** Deserializes a [MirrorStopMessage] from [data] (type byte at index 0). */
        fun fromBytes(data: ByteArray): MirrorStopMessage =
            MirrorStopMessage(if (data.size > 1) data[1] else 0)
    }
}

/** Kind of input event carried in [InputEventMessage]. Mirrors the Windows-side enum. */
enum class InputEventKind(val value: Byte) {
    /** A touch/tap event (finger down, move, or up). */
    TOUCH(0x00),
    /** A hardware key press or release. */
    KEY(0x01),
    /** A mouse pointer event. */
    MOUSE(0x02);

    companion object {
        /** Returns the [InputEventKind] for [value], or null if unknown. */
        fun fromByte(value: Byte): InputEventKind? =
            entries.firstOrNull { it.value == value }
    }
}

/**
 * Wire-format message sent by Windows to relay a pointer/key input event to the Android device.
 *
 * Binary layout (big-endian):
 * ```
 * [1 byte ] type = 0x30
 * [4 bytes] session-id length (N)
 * [N bytes] session-id (UTF-8)
 * [1 byte ] event-kind (0 = TOUCH, 1 = KEY, 2 = MOUSE)
 * [4 bytes] normalizedX (IEEE 754 float32, 0.0 – 1.0)
 * [4 bytes] normalizedY (IEEE 754 float32, 0.0 – 1.0)
 * [1 byte ] has-keycode (0 or 1)
 * [4 bytes] keycode     (int32, present only if has-keycode = 1)
 * [4 bytes] metaState   (int32)
 * ```
 */
data class InputEventMessage(
    val sessionId: String,
    val eventKind: InputEventKind,
    val normalizedX: Float,
    val normalizedY: Float,
    val keycode: Int?,
    val metaState: Int
) {
    /** Serializes the message to a [ByteArray]. */
    fun toBytes(): ByteArray {
        val sidBytes  = sessionId.toByteArray(StandardCharsets.UTF_8)
        val hasKeycode = keycode != null
        val size = 1 + 4 + sidBytes.size + 1 + 4 + 4 + 1 + (if (hasKeycode) 4 else 0) + 4
        val buf  = ByteBuffer.allocate(size)
        buf.put(MirrorMessageType.INPUT_EVENT.value)
        buf.putInt(sidBytes.size); buf.put(sidBytes)
        buf.put(eventKind.value)
        buf.putFloat(normalizedX)
        buf.putFloat(normalizedY)
        if (hasKeycode) {
            buf.put(0x01.toByte())
            buf.putInt(keycode!!)
        } else {
            buf.put(0x00.toByte())
        }
        buf.putInt(metaState)
        return buf.array()
    }

    companion object {
        /**
         * Deserializes an [InputEventMessage] from [data]
         * (including the type byte at index 0).
         */
        fun fromBytes(data: ByteArray): InputEventMessage {
            val buf       = ByteBuffer.wrap(data, 1, data.size - 1)
            val sidLen    = buf.int; val sidBytes = ByteArray(sidLen).also { buf.get(it) }
            val sessionId = String(sidBytes, StandardCharsets.UTF_8)
            val kind      = InputEventKind.fromByte(buf.get())
                ?: throw IllegalArgumentException("Unknown InputEventKind byte")
            val x         = buf.float
            val y         = buf.float
            val hasKey    = buf.get() != 0.toByte()
            val keycode   = if (hasKey) buf.int else null
            val metaState = buf.int
            return InputEventMessage(sessionId, kind, x, y, keycode, metaState)
        }
    }
}
