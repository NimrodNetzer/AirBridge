package com.airbridge.app.mirror

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * Round-trip serialization tests for the three mirror message types.
 * Follows the same pattern as TransferSessionTest.
 */
class MirrorMessageTest {

    // ── MirrorStartMessage ─────────────────────────────────────────────────

    @Test
    fun `MirrorStartMessage round-trips basic values`() {
        val msg = MirrorStartMessage(
            sessionId = "session-abc",
            width     = 1080,
            height    = 1920,
            fps       = 30,
            codec     = "H264"
        )
        val decoded = MirrorStartMessage.fromBytes(msg.toBytes())

        assertEquals(msg.sessionId, decoded.sessionId)
        assertEquals(msg.width,     decoded.width)
        assertEquals(msg.height,    decoded.height)
        assertEquals(msg.fps,       decoded.fps)
        assertEquals(msg.codec,     decoded.codec)
    }

    @Test
    fun `MirrorStartMessage preserves type byte`() {
        val msg   = MirrorStartMessage("sid", 720, 1280, 60, "H265")
        val bytes = msg.toBytes()
        assertEquals(MirrorMessageType.MIRROR_START.value, bytes[0])
    }

    @Test
    fun `MirrorStartMessage round-trips unicode session id`() {
        val msg     = MirrorStartMessage("session-\u4e2d\u6587", 1920, 1080, 24, "H264")
        val decoded = MirrorStartMessage.fromBytes(msg.toBytes())
        assertEquals(msg.sessionId, decoded.sessionId)
    }

    @Test
    fun `MirrorStartMessage round-trips H265 codec`() {
        val msg     = MirrorStartMessage("s1", 3840, 2160, 30, "H265")
        val decoded = MirrorStartMessage.fromBytes(msg.toBytes())
        assertEquals("H265", decoded.codec)
    }

    // ── MirrorFrameMessage ─────────────────────────────────────────────────

    @Test
    fun `MirrorFrameMessage round-trips keyframe`() {
        val nal = byteArrayOf(0x00, 0x00, 0x00, 0x01, 0x65.toByte(), 0xAA.toByte())
        val msg = MirrorFrameMessage(
            sessionId   = "session-abc",
            timestampMs = 1234567890L,
            isKeyFrame  = true,
            nalData     = nal
        )
        val decoded = MirrorFrameMessage.fromBytes(msg.toBytes())

        assertEquals(msg.sessionId,   decoded.sessionId)
        assertEquals(msg.timestampMs, decoded.timestampMs)
        assertTrue(decoded.isKeyFrame)
        assertArrayEquals(msg.nalData, decoded.nalData)
    }

    @Test
    fun `MirrorFrameMessage round-trips non-keyframe`() {
        val nal = byteArrayOf(0x00, 0x00, 0x00, 0x01, 0x41.toByte())
        val msg = MirrorFrameMessage(
            sessionId   = "s2",
            timestampMs = 0L,
            isKeyFrame  = false,
            nalData     = nal
        )
        val decoded = MirrorFrameMessage.fromBytes(msg.toBytes())

        assertFalse(decoded.isKeyFrame)
        assertEquals(0L, decoded.timestampMs)
        assertArrayEquals(nal, decoded.nalData)
    }

    @Test
    fun `MirrorFrameMessage preserves type byte`() {
        val msg   = MirrorFrameMessage("s", 0L, false, byteArrayOf(1, 2, 3))
        val bytes = msg.toBytes()
        assertEquals(MirrorMessageType.MIRROR_FRAME.value, bytes[0])
    }

    @Test
    fun `MirrorFrameMessage round-trips large NAL payload`() {
        val nal     = ByteArray(65536) { (it % 256).toByte() }
        val msg     = MirrorFrameMessage("big-session", 99999L, true, nal)
        val decoded = MirrorFrameMessage.fromBytes(msg.toBytes())
        assertArrayEquals(nal, decoded.nalData)
        assertEquals(99999L, decoded.timestampMs)
    }

    @Test
    fun `MirrorFrameMessage max timestamp round-trips`() {
        val msg     = MirrorFrameMessage("s", Long.MAX_VALUE, true, byteArrayOf(0))
        val decoded = MirrorFrameMessage.fromBytes(msg.toBytes())
        assertEquals(Long.MAX_VALUE, decoded.timestampMs)
    }

    // ── MirrorStopMessage ──────────────────────────────────────────────────

    @Test
    fun `MirrorStopMessage round-trips session id`() {
        val msg     = MirrorStopMessage("session-xyz")
        val decoded = MirrorStopMessage.fromBytes(msg.toBytes())
        assertEquals(msg.sessionId, decoded.sessionId)
    }

    @Test
    fun `MirrorStopMessage preserves type byte`() {
        val msg   = MirrorStopMessage("s")
        val bytes = msg.toBytes()
        assertEquals(MirrorMessageType.MIRROR_STOP.value, bytes[0])
    }

    @Test
    fun `MirrorStopMessage round-trips empty session id`() {
        val msg     = MirrorStopMessage("")
        val decoded = MirrorStopMessage.fromBytes(msg.toBytes())
        assertEquals("", decoded.sessionId)
    }

    // ── MirrorMessageType fromByte ─────────────────────────────────────────

    @Test
    fun `MirrorMessageType fromByte returns correct type for all values`() {
        assertEquals(MirrorMessageType.MIRROR_START, MirrorMessageType.fromByte(0x20.toByte()))
        assertEquals(MirrorMessageType.MIRROR_FRAME, MirrorMessageType.fromByte(0x21.toByte()))
        assertEquals(MirrorMessageType.MIRROR_STOP,  MirrorMessageType.fromByte(0x22.toByte()))
    }

    @Test
    fun `MirrorMessageType fromByte returns null for unknown value`() {
        assertNull(MirrorMessageType.fromByte(0x99.toByte()))
    }
}
