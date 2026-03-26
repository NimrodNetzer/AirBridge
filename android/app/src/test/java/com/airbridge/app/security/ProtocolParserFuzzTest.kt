package com.airbridge.app.security

import com.airbridge.app.transport.connection.TlsMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertDoesNotThrow
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.RepeatedTest
import org.junit.jupiter.api.RepetitionInfo
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.net.Socket
import kotlin.random.Random

/**
 * Property-based fuzz-style tests for the AirBridge protocol parser (Android / Kotlin).
 *
 * Key invariant: the parser must **never** propagate an unhandled exception from
 * untrusted input.  Each test asserts that feeding malformed or random bytes results in:
 *   - a clean flow completion (EOF), OR
 *   - one of the documented, typed exceptions:
 *       [IllegalStateException] (frame length violation),
 *       [IllegalArgumentException] (unknown message type),
 *       [java.io.EOFException] (truncated stream).
 *
 * Any other exception type triggers an explicit [org.junit.jupiter.api.Assertions.fail].
 *
 * Wire format reminder:
 * ```
 * [4 bytes big-endian] frameLength = 1 + payloadSize
 * [1 byte            ] MessageType
 * [payloadSize bytes ] payload
 * ```
 */
class ProtocolParserFuzzTest {

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /**
     * Builds a well-formed frame byte array.
     * [payloadSize] bytes of the payload section are filled with [payloadFill].
     */
    private fun buildFrame(
        typeByte: Byte,
        payloadSize: Int,
        payloadFill: Byte = 0x00
    ): ByteArray {
        val buf = ByteArrayOutputStream()
        val out = DataOutputStream(buf)
        out.writeInt(1 + payloadSize)   // frameLength = 1 (type) + payload
        out.writeByte(typeByte.toInt())
        repeat(payloadSize) { out.writeByte(payloadFill.toInt()) }
        out.flush()
        return buf.toByteArray()
    }

    /**
     * Creates a [TlsMessageChannel] whose input stream is backed by [bytes].
     */
    private fun channelWithInput(bytes: ByteArray): TlsMessageChannel {
        val socket = mockk<Socket>(relaxed = true)
        every { socket.getInputStream() } returns ByteArrayInputStream(bytes)
        every { socket.getOutputStream() } returns ByteArrayOutputStream()
        return TlsMessageChannel(socket = socket, remoteDeviceId = "fuzz-test")
    }

    /**
     * Collects all messages from a channel created from [input], swallowing any
     * [IllegalStateException], [IllegalArgumentException], or [java.io.EOFException].
     * Returns the list of successfully parsed messages.
     * Any *other* exception is rethrown so the test can fail.
     */
    private suspend fun parseAllFromBytes(input: ByteArray): List<ProtocolMessage> {
        val results = mutableListOf<ProtocolMessage>()
        channelWithInput(input).incomingMessages
            .catch { e ->
                // Only documented typed exceptions are acceptable
                if (e !is IllegalStateException &&
                    e !is IllegalArgumentException &&
                    e !is java.io.EOFException &&
                    e !is java.io.IOException) {
                    throw e  // will surface as test failure
                }
            }
            .toList(results)
        return results
    }

    // -------------------------------------------------------------------------
    // Category 1: Truncated frames (0–3 bytes)
    // -------------------------------------------------------------------------

    @Test
    fun `zero bytes - returns empty without exception`() = runTest {
        val results = parseAllFromBytes(ByteArray(0))
        assertTrue(results.isEmpty(), "Expected empty result for zero-byte input")
    }

    @ParameterizedTest
    @ValueSource(ints = [1, 2, 3])
    fun `truncated header 1 to 3 bytes - does not propagate unhandled exception`(byteCount: Int) =
        runTest {
            val truncated = ByteArray(byteCount) { it.toByte() }
            assertDoesNotCrash { parseAllFromBytes(truncated) }
        }

    @Test
    fun `truncated header - single byte 0xFF - does not crash`() = runTest {
        assertDoesNotCrash { parseAllFromBytes(byteArrayOf(0xFF.toByte())) }
    }

    @Test
    fun `truncated header - three bytes all max value - does not crash`() = runTest {
        assertDoesNotCrash {
            parseAllFromBytes(byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte()))
        }
    }

    // -------------------------------------------------------------------------
    // Category 2: Oversized length field
    // -------------------------------------------------------------------------

    @Test
    fun `oversized length field - Int MAX VALUE - rejected without OOM`() = runTest {
        val buf = ByteArrayOutputStream()
        DataOutputStream(buf).also { out ->
            out.writeInt(Int.MAX_VALUE)  // extremely large frame length
            out.writeByte(MessageType.HANDSHAKE.value.toInt())
            out.flush()
        }
        // Must throw IllegalStateException ("Payload too large") or IOException — not OOM
        assertDoesNotCrash { parseAllFromBytes(buf.toByteArray()) }
    }

    @Test
    fun `oversized length field - 64MB plus 1 - rejected`() = runTest {
        val buf = ByteArrayOutputStream()
        DataOutputStream(buf).also { out ->
            out.writeInt(ProtocolMessage.MAX_PAYLOAD_BYTES + 2) // +1 for type byte, +1 more to exceed
            out.writeByte(MessageType.MIRROR_FRAME.value.toInt())
            out.flush()
        }
        assertDoesNotCrash { parseAllFromBytes(buf.toByteArray()) }
    }

    @Test
    fun `oversized length field - MAX INT minus 1 - rejected without allocating`() = runTest {
        val buf = ByteArrayOutputStream()
        DataOutputStream(buf).also { out ->
            out.writeInt(Int.MAX_VALUE - 1)
            out.writeByte(0x01)
            out.flush()
        }
        assertDoesNotCrash { parseAllFromBytes(buf.toByteArray()) }
    }

    // -------------------------------------------------------------------------
    // Category 3: Unknown message type byte
    // -------------------------------------------------------------------------

    @Test
    fun `unknown type byte 0x99 - does not propagate crash`() = runTest {
        val frame = buildFrame(typeByte = 0x99.toByte(), payloadSize = 4)
        assertDoesNotCrash { parseAllFromBytes(frame) }
    }

    @ParameterizedTest
    @ValueSource(ints = [0x00, 0x05, 0x50, 0x99, 0xAA, 0xBB, 0xFE])
    fun `various unknown type bytes - none cause unhandled exception`(typeByte: Int) = runTest {
        val frame = buildFrame(typeByte = typeByte.toByte(), payloadSize = 2)
        assertDoesNotCrash { parseAllFromBytes(frame) }
    }

    @Test
    fun `unknown type 0x00 with empty payload - does not crash`() = runTest {
        val frame = buildFrame(typeByte = 0x00, payloadSize = 0)
        assertDoesNotCrash { parseAllFromBytes(frame) }
    }

    // -------------------------------------------------------------------------
    // Category 4: Corrupted payload (random garbage bytes)
    // -------------------------------------------------------------------------

    @Test
    fun `corrupted payload - all 0xFF - does not crash`() = runTest {
        val frame = buildFrame(
            typeByte = MessageType.HANDSHAKE.value,
            payloadSize = 32,
            payloadFill = 0xFF.toByte()
        )
        assertDoesNotCrash { parseAllFromBytes(frame) }
    }

    @Test
    fun `corrupted payload - random 64 bytes - does not crash`() = runTest {
        val payload = Random.nextBytes(64)
        val buf     = ByteArrayOutputStream()
        val out     = DataOutputStream(buf)
        out.writeInt(1 + payload.size)
        out.writeByte(MessageType.FILE_CHUNK.value.toInt())
        out.write(payload)
        out.flush()
        assertDoesNotCrash { parseAllFromBytes(buf.toByteArray()) }
    }

    @Test
    fun `corrupted payload - random 1KB - does not crash`() = runTest {
        val payload = Random.nextBytes(1024)
        val buf     = ByteArrayOutputStream()
        val out     = DataOutputStream(buf)
        out.writeInt(1 + payload.size)
        out.writeByte(MessageType.MIRROR_FRAME.value.toInt())
        out.write(payload)
        out.flush()
        assertDoesNotCrash { parseAllFromBytes(buf.toByteArray()) }
    }

    @Test
    fun `corrupted payload - all zeroes - does not crash`() = runTest {
        val frame = buildFrame(
            typeByte    = MessageType.PAIRING_REQUEST.value,
            payloadSize = 16,
            payloadFill = 0x00
        )
        assertDoesNotCrash { parseAllFromBytes(frame) }
    }

    // -------------------------------------------------------------------------
    // Category 5: Zero-length payload
    // -------------------------------------------------------------------------

    @Test
    fun `zero-length payload - PING type - parses successfully`() = runTest {
        // PING with empty payload is a valid frame (frameLength = 1)
        val frame = buildFrame(typeByte = MessageType.PING.value, payloadSize = 0)
        val results = parseAllFromBytes(frame)

        assertTrue(results.size == 1, "Expected exactly one parsed message")
        assertEquals(MessageType.PING, results[0].type)
        assertTrue(results[0].payload.isEmpty())
    }

    @Test
    fun `zero-length payload - PONG type - no NullPointerException`() = runTest {
        val frame = buildFrame(typeByte = MessageType.PONG.value, payloadSize = 0)
        val results = parseAllFromBytes(frame)
        if (results.isNotEmpty()) {
            assertNotNull(results[0].payload)
        }
    }

    @Test
    fun `zero-length payload - ERROR type - payload is empty not null`() = runTest {
        val frame = buildFrame(typeByte = MessageType.ERROR.value, payloadSize = 0)
        val results = parseAllFromBytes(frame)
        if (results.isNotEmpty()) {
            assertNotNull(results[0].payload)
            assertTrue(results[0].payload.isEmpty())
        }
    }

    @Test
    fun `zero-length payload - MIRROR STOP type - does not crash`() = runTest {
        val frame = buildFrame(typeByte = MessageType.MIRROR_STOP.value, payloadSize = 0)
        assertDoesNotCrash { parseAllFromBytes(frame) }
    }

    // -------------------------------------------------------------------------
    // Category 6: 100 random byte sequences
    // -------------------------------------------------------------------------

    @RepeatedTest(100)
    fun `random bytes - never propagate unhandled exception`(info: RepetitionInfo) = runTest {
        val seed  = info.currentRepetition.toLong() * 7919L  // deterministic per repetition
        val rng   = Random(seed)
        val len   = rng.nextInt(0, 101) // 0..100 bytes
        val bytes = rng.nextBytes(len)
        assertDoesNotCrash { parseAllFromBytes(bytes) }
    }

    @Test
    fun `100 random frames with valid-length headers - none crash`() = runTest {
        val rng = Random(seed = 1337)
        repeat(100) { i ->
            val typeByte   = rng.nextInt(0, 256).toByte()
            val payloadLen = rng.nextInt(0, 256)
            val payload    = rng.nextBytes(payloadLen)

            val buf = ByteArrayOutputStream()
            val out = DataOutputStream(buf)
            out.writeInt(1 + payloadLen)
            out.writeByte(typeByte.toInt())
            out.write(payload)
            out.flush()

            assertDoesNotCrash(context = "iteration $i type=0x${typeByte.toUByte().toString(16)}") {
                parseAllFromBytes(buf.toByteArray())
            }
        }
    }

    // -------------------------------------------------------------------------
    // Invariant: MAX_PAYLOAD_BYTES constant
    // -------------------------------------------------------------------------

    @Test
    fun `MAX_PAYLOAD_BYTES is 64 MB`() {
        assertEquals(64 * 1024 * 1024, ProtocolMessage.MAX_PAYLOAD_BYTES)
    }

    @Test
    fun `MAX_PAYLOAD_BYTES fits in Int without overflow`() {
        assertTrue(ProtocolMessage.MAX_PAYLOAD_BYTES > 0)
        assertTrue(ProtocolMessage.MAX_PAYLOAD_BYTES < Int.MAX_VALUE)
    }

    // -------------------------------------------------------------------------
    // Helper: assertDoesNotCrash
    // -------------------------------------------------------------------------

    /**
     * Runs [block] and asserts that any thrown exception is one of the documented
     * typed exceptions.  Fails the test if an undocumented exception escapes.
     */
    private suspend fun assertDoesNotCrash(
        context: String = "",
        block: suspend () -> Unit
    ) {
        try {
            block()
        } catch (e: IllegalStateException)  { /* documented typed error */ }
          catch (e: IllegalArgumentException) { /* documented typed error */ }
          catch (e: java.io.EOFException)     { /* documented EOF */ }
          catch (e: java.io.IOException)      { /* documented IO error */ }
          catch (e: Exception) {
              val prefix = if (context.isNotEmpty()) "[$context] " else ""
              throw AssertionError(
                  "${prefix}Unhandled ${e::class.simpleName}: ${e.message}", e
              )
          }
    }
}
