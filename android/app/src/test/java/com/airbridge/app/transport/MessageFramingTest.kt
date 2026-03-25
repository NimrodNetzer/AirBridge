package com.airbridge.app.transport

import com.airbridge.app.transport.connection.TlsMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.net.Socket

/**
 * Verifies encode/decode of the AirBridge wire-frame format.
 *
 * Wire format: [4-byte big-endian length][1-byte MessageType][payload bytes]
 * where `length` = 1 + payload.size.
 */
class MessageFramingTest {

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /**
     * Encodes a [ProtocolMessage] into a [ByteArray] using the AirBridge frame format,
     * mirroring the logic in [TlsMessageChannel.send].
     */
    private fun encodeFrame(message: ProtocolMessage): ByteArray {
        val buf = ByteArrayOutputStream()
        val out = DataOutputStream(buf)
        val frameLength = 1 + message.payload.size
        out.writeInt(frameLength)
        out.writeByte(message.type.value.toInt())
        if (message.payload.isNotEmpty()) out.write(message.payload)
        out.flush()
        return buf.toByteArray()
    }

    /**
     * Builds a [TlsMessageChannel] whose [Socket] streams are backed by the supplied bytes,
     * enabling decode tests without a real network connection.
     */
    private fun channelWithInput(bytes: ByteArray): TlsMessageChannel {
        val socket = mockk<Socket>(relaxed = true)
        every { socket.getInputStream() } returns ByteArrayInputStream(bytes)
        every { socket.getOutputStream() } returns ByteArrayOutputStream()
        return TlsMessageChannel(socket = socket, remoteDeviceId = "test-device")
    }

    // -------------------------------------------------------------------------
    // Encode tests
    // -------------------------------------------------------------------------

    @Test
    fun `encodes PING message with empty payload`() {
        val message = ProtocolMessage(type = MessageType.PING, payload = ByteArray(0))
        val frame = encodeFrame(message)

        // Frame: [0,0,0,1] (length=1) + [0xF0] (PING type byte)
        assertEquals(5, frame.size)
        // Length field
        assertEquals(0x00.toByte(), frame[0])
        assertEquals(0x00.toByte(), frame[1])
        assertEquals(0x00.toByte(), frame[2])
        assertEquals(0x01.toByte(), frame[3])
        // Type byte
        assertEquals(MessageType.PING.value, frame[4])
    }

    @Test
    fun `encodes HANDSHAKE message with payload`() {
        val payload = byteArrayOf(0x01, 0x02, 0x03, 0x04)
        val message = ProtocolMessage(type = MessageType.HANDSHAKE, payload = payload)
        val frame = encodeFrame(message)

        // length = 1 (type) + 4 (payload) = 5  →  frame total = 4 + 5 = 9 bytes
        assertEquals(9, frame.size)
        // Length field = 5
        assertEquals(0x00.toByte(), frame[0])
        assertEquals(0x00.toByte(), frame[1])
        assertEquals(0x00.toByte(), frame[2])
        assertEquals(0x05.toByte(), frame[3])
        // Type byte
        assertEquals(MessageType.HANDSHAKE.value, frame[4])
        // Payload
        assertArrayEquals(payload, frame.copyOfRange(5, 9))
    }

    @Test
    fun `frame length field equals one plus payload size`() {
        val payload = ByteArray(256) { it.toByte() }
        val message = ProtocolMessage(type = MessageType.FILE_CHUNK, payload = payload)
        val frame = encodeFrame(message)

        val lengthField = (frame[0].toInt() and 0xFF shl 24) or
                          (frame[1].toInt() and 0xFF shl 16) or
                          (frame[2].toInt() and 0xFF shl 8)  or
                          (frame[3].toInt() and 0xFF)
        assertEquals(1 + payload.size, lengthField)
    }

    // -------------------------------------------------------------------------
    // Decode tests (via TlsMessageChannel.incomingMessages)
    // -------------------------------------------------------------------------

    @Test
    fun `decodes single PING frame with empty payload`() = runTest {
        val original = ProtocolMessage(type = MessageType.PING, payload = ByteArray(0))
        val frame = encodeFrame(original)
        val channel = channelWithInput(frame)

        val decoded = channel.incomingMessages.first()
        assertEquals(original, decoded)
    }

    @Test
    fun `decodes HANDSHAKE_ACK frame with payload`() = runTest {
        val payload = byteArrayOf(0xDE.toByte(), 0xAD.toByte(), 0xBE.toByte(), 0xEF.toByte())
        val original = ProtocolMessage(type = MessageType.HANDSHAKE_ACK, payload = payload)
        val frame = encodeFrame(original)
        val channel = channelWithInput(frame)

        val decoded = channel.incomingMessages.first()
        assertEquals(original.type, decoded.type)
        assertArrayEquals(original.payload, decoded.payload)
    }

    @Test
    fun `round-trip encode then decode preserves all message types`() = runTest {
        val typesToTest = listOf(
            MessageType.HANDSHAKE,
            MessageType.PAIRING_REQUEST,
            MessageType.FILE_TRANSFER_START,
            MessageType.MIRROR_START,
            MessageType.PING,
            MessageType.PONG,
            MessageType.ERROR
        )
        for (type in typesToTest) {
            val payload = "hello-$type".toByteArray()
            val original = ProtocolMessage(type = type, payload = payload)

            // Encode
            val frame = encodeFrame(original)

            // Decode
            val channel = channelWithInput(frame)
            val decoded = channel.incomingMessages.first()

            assertEquals(original.type, decoded.type, "Type mismatch for $type")
            assertArrayEquals(original.payload, decoded.payload, "Payload mismatch for $type")
        }
    }

    @Test
    fun `decodes multiple consecutive frames from a single stream`() = runTest {
        val messages = listOf(
            ProtocolMessage(MessageType.PING, ByteArray(0)),
            ProtocolMessage(MessageType.PONG, ByteArray(0)),
            ProtocolMessage(MessageType.HANDSHAKE, byteArrayOf(0x01))
        )

        // Concatenate all frames into one byte array
        val buf = ByteArrayOutputStream()
        messages.forEach { buf.write(encodeFrame(it)) }
        val allFrames = buf.toByteArray()

        val channel = channelWithInput(allFrames)
        val results = mutableListOf<ProtocolMessage>()
        try {
            channel.incomingMessages.collect { results.add(it) }
        } catch (_: Exception) {}

        assertEquals(messages.size, results.size)
        messages.zip(results).forEach { (expected, actual) ->
            assertEquals(expected, actual)
        }
    }
}
