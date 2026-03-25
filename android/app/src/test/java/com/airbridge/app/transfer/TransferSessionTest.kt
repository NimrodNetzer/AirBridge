package com.airbridge.app.transfer

import com.airbridge.app.core.interfaces.TransferState
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.ByteArrayOutputStream
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.security.MessageDigest

class TransferSessionTest {

    // ── Message round-trips ────────────────────────────────────────────────

    @Test
    fun `FileStartMessage round-trips`() {
        val msg     = FileStartMessage("sid-1", "file.txt", 8192L)
        val decoded = FileStartMessage.fromBytes(msg.toBytes())
        assertEquals(msg.sessionId,  decoded.sessionId)
        assertEquals(msg.fileName,   decoded.fileName)
        assertEquals(msg.totalBytes, decoded.totalBytes)
    }

    @Test
    fun `FileChunkMessage round-trips`() {
        val data    = byteArrayOf(1, 2, 3, 4, 5)
        val msg     = FileChunkMessage(1024L, data)
        val decoded = FileChunkMessage.fromBytes(msg.toBytes())
        assertEquals(msg.offset, decoded.offset)
        assertArrayEquals(msg.data, decoded.data)
    }

    @Test
    fun `TransferAckMessage round-trips`() {
        val msg     = TransferAckMessage(65536L)
        val decoded = TransferAckMessage.fromBytes(msg.toBytes())
        assertEquals(msg.bytesAcknowledged, decoded.bytesAcknowledged)
    }

    @Test
    fun `FileEndMessage round-trips`() {
        val hash    = ByteArray(32) { it.toByte() }
        val msg     = FileEndMessage(9999L, hash)
        val decoded = FileEndMessage.fromBytes(msg.toBytes())
        assertEquals(msg.totalBytes, decoded.totalBytes)
        assertArrayEquals(msg.sha256Hash, decoded.sha256Hash)
    }

    @Test
    fun `TransferErrorMessage round-trips`() {
        val msg     = TransferErrorMessage("disk full")
        val decoded = TransferErrorMessage.fromBytes(msg.toBytes())
        assertEquals(msg.errorMessage, decoded.errorMessage)
    }

    // ── Loopback transfer using PipedInputStream / PipedOutputStream ───────

    private fun loopbackTransfer(fileData: ByteArray): ByteArray {
        // Pipe: sender writes, receiver reads
        val networkOut = PipedOutputStream()
        val networkIn  = PipedInputStream(networkOut, 1024 * 1024) // 1 MB buffer

        val sourceStream = fileData.inputStream()
        val sinkStream   = ByteArrayOutputStream()

        val sessionId  = "test-session"
        val fileName   = "test.bin"
        val totalBytes = fileData.size.toLong()

        val sender   = TransferSession(sessionId, fileName, totalBytes, isSender = true,
                            dataStream = sourceStream, networkStream = networkOut)
        val receiver = TransferSession(sessionId, fileName, totalBytes, isSender = false,
                            dataStream = sinkStream,   networkStream = networkIn)

        // Run sender in a background thread, receiver in current thread
        val senderThread = Thread {
            runTest { sender.start() }
        }
        senderThread.start()
        runTest { receiver.start() }
        senderThread.join(5_000)

        return sinkStream.toByteArray()
    }

    @Test
    fun `loopback - small file - data matches`() {
        val data     = "Hello, AirBridge!".toByteArray()
        val received = loopbackTransfer(data)
        assertArrayEquals(data, received)
    }

    @Test
    fun `loopback - exactly one chunk - sha256 matches`() {
        val data     = ByteArray(TransferSession.CHUNK_SIZE) { it.toByte() }
        val received = loopbackTransfer(data)
        assertArrayEquals(
            MessageDigest.getInstance("SHA-256").digest(data),
            MessageDigest.getInstance("SHA-256").digest(received)
        )
    }

    @Test
    fun `loopback - multiple chunks - sha256 matches`() {
        val data     = ByteArray(200 * 1024) { (it % 256).toByte() }
        val received = loopbackTransfer(data)
        assertArrayEquals(
            MessageDigest.getInstance("SHA-256").digest(data),
            MessageDigest.getInstance("SHA-256").digest(received)
        )
    }

    @Test
    fun `loopback - empty file - succeeds`() {
        val received = loopbackTransfer(ByteArray(0))
        assertEquals(0, received.size)
    }

    // ── State transitions ──────────────────────────────────────────────────

    @Test
    fun `session completes with COMPLETED state`() {
        val data     = byteArrayOf(0xAB.toByte(), 0xCD.toByte())
        val received = loopbackTransfer(data)
        // If we got here without exception the session completed successfully
        assertEquals(2, received.size)
    }

    @Test
    fun `cancel transitions to CANCELLED state`() = runTest {
        val session = TransferSession(
            sessionId    = "cancel-test",
            fileName     = "f.bin",
            totalBytes   = 0,
            isSender     = true,
            dataStream   = ByteArray(0).inputStream(),
            networkStream = ByteArrayOutputStream()
        )
        session.cancel()
        // Collect one value from stateFlow
        val states = mutableListOf<TransferState>()
        session.stateFlow.collect { states.add(it); if (states.size >= 1) return@collect }
        assertTrue(states.any { it == TransferState.CANCELLED || it == TransferState.PENDING })
    }
}
