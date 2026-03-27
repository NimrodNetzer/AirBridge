package com.airbridge.app.mirror

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * Round-trip serialization tests for [InputEventMessage] and [InputEventKind].
 * Follows the same pattern as [MirrorMessageTest].
 * No Android framework dependencies — runs on the JVM.
 */
class InputEventMessageTest {

    // ── Type byte ─────────────────────────────────────────────────────────────

    @Test
    fun `InputEventMessage preserves type byte`() {
        val msg   = InputEventMessage("sid", InputEventKind.TOUCH, 0.5f, 0.5f, null, 0)
        val bytes = msg.toBytes()
        assertEquals(MirrorMessageType.INPUT_EVENT.value, bytes[0])
    }

    // ── Touch event (no keycode) ──────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips touch event without keycode`() {
        val msg = InputEventMessage(
            sessionId   = "session-touch",
            eventKind   = InputEventKind.TOUCH,
            normalizedX = 0.25f,
            normalizedY = 0.75f,
            keycode     = null,
            metaState   = 0
        )
        val decoded = InputEventMessage.fromBytes(msg.toBytes())

        assertEquals(msg.sessionId,   decoded.sessionId)
        assertEquals(msg.eventKind,   decoded.eventKind)
        assertEquals(msg.normalizedX, decoded.normalizedX, 1e-5f)
        assertEquals(msg.normalizedY, decoded.normalizedY, 1e-5f)
        assertNull(decoded.keycode)
        assertEquals(0, decoded.metaState)
    }

    // ── Key event (with keycode) ──────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips key event with keycode`() {
        val msg = InputEventMessage(
            sessionId   = "session-key",
            eventKind   = InputEventKind.KEY,
            normalizedX = 0f,
            normalizedY = 0f,
            keycode     = 66, // KEYCODE_ENTER
            metaState   = 1
        )
        val decoded = InputEventMessage.fromBytes(msg.toBytes())

        assertEquals(InputEventKind.KEY, decoded.eventKind)
        assertNotNull(decoded.keycode)
        assertEquals(66, decoded.keycode)
        assertEquals(1,  decoded.metaState)
    }

    // ── Mouse event ───────────────────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips mouse event`() {
        val msg     = InputEventMessage("s", InputEventKind.MOUSE, 0.0f, 1.0f, null, 0)
        val decoded = InputEventMessage.fromBytes(msg.toBytes())

        assertEquals(InputEventKind.MOUSE, decoded.eventKind)
        assertEquals(0.0f, decoded.normalizedX, 1e-5f)
        assertEquals(1.0f, decoded.normalizedY, 1e-5f)
    }

    // ── Boundary coordinates ──────────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips zero coordinates`() {
        val msg     = InputEventMessage("s", InputEventKind.TOUCH, 0f, 0f, null, 0)
        val decoded = InputEventMessage.fromBytes(msg.toBytes())
        assertEquals(0f, decoded.normalizedX, 1e-5f)
        assertEquals(0f, decoded.normalizedY, 1e-5f)
    }

    @Test
    fun `InputEventMessage round-trips unit coordinates`() {
        val msg     = InputEventMessage("s", InputEventKind.TOUCH, 1f, 1f, null, 0)
        val decoded = InputEventMessage.fromBytes(msg.toBytes())
        assertEquals(1f, decoded.normalizedX, 1e-5f)
        assertEquals(1f, decoded.normalizedY, 1e-5f)
    }

    // ── Unicode session ID ────────────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips unicode session id`() {
        val msg     = InputEventMessage("session-\u4e2d\u6587", InputEventKind.TOUCH, 0.5f, 0.5f, null, 0)
        val decoded = InputEventMessage.fromBytes(msg.toBytes())
        assertEquals(msg.sessionId, decoded.sessionId)
    }

    // ── MetaState ─────────────────────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips max metaState`() {
        val msg     = InputEventMessage("s", InputEventKind.KEY, 0f, 0f, 13, Int.MAX_VALUE)
        val decoded = InputEventMessage.fromBytes(msg.toBytes())
        assertEquals(Int.MAX_VALUE, decoded.metaState)
    }

    // ── All InputEventKind values ─────────────────────────────────────────────

    @Test
    fun `InputEventMessage round-trips all event kinds`() {
        InputEventKind.entries.forEach { kind ->
            val keycode = if (kind == InputEventKind.KEY) 65 else null
            val msg     = InputEventMessage("sid", kind, 0.1f, 0.9f, keycode, 0)
            val decoded = InputEventMessage.fromBytes(msg.toBytes())
            assertEquals(kind, decoded.eventKind, "Kind $kind failed round-trip")
        }
    }

    // ── InputEventKind.fromByte ───────────────────────────────────────────────

    @Test
    fun `InputEventKind fromByte returns correct values`() {
        assertEquals(InputEventKind.TOUCH, InputEventKind.fromByte(0x00.toByte()))
        assertEquals(InputEventKind.KEY,   InputEventKind.fromByte(0x01.toByte()))
        assertEquals(InputEventKind.MOUSE, InputEventKind.fromByte(0x02.toByte()))
    }

    @Test
    fun `InputEventKind fromByte returns null for unknown value`() {
        assertNull(InputEventKind.fromByte(0x99.toByte()))
    }

    // ── MirrorMessageType INPUT_EVENT entry ───────────────────────────────────

    @Test
    fun `MirrorMessageType INPUT_EVENT value is 0x30`() {
        assertEquals(0x30.toByte(), MirrorMessageType.INPUT_EVENT.value)
    }

    @Test
    fun `MirrorMessageType fromByte returns INPUT_EVENT for 0x30`() {
        assertEquals(MirrorMessageType.INPUT_EVENT, MirrorMessageType.fromByte(0x30.toByte()))
    }
}
