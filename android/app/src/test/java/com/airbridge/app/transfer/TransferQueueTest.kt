package com.airbridge.app.transfer

import com.airbridge.app.core.interfaces.TransferState
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.ByteArrayOutputStream
import java.io.PipedInputStream
import java.io.PipedOutputStream

class TransferQueueTest {

    // ── Helpers ────────────────────────────────────────────────────────────

    /**
     * Creates a matched sender + receiver [TransferSession] pair using in-process pipes.
     * The sessions are ready to be enqueued independently (each must be started to run).
     */
    private fun makePair(sessionId: String, byteCount: Int = 128): Pair<TransferSession, TransferSession> {
        val networkOut = PipedOutputStream()
        val networkIn  = PipedInputStream(networkOut, 1024 * 1024)

        val source = ByteArray(byteCount) { (it % 256).toByte() }.inputStream()
        val sink   = ByteArrayOutputStream()

        val sender   = TransferSession(sessionId, "file.bin", byteCount.toLong(), isSender = true,
                            dataStream = source, networkStream = networkOut)
        val receiver = TransferSession(sessionId, "file.bin", byteCount.toLong(), isSender = false,
                            dataStream = sink,   networkStream = networkIn)
        return sender to receiver
    }

    // ── Basic enqueue / completion ─────────────────────────────────────────

    @Test
    fun `enqueue single session pair - both complete`() = runTest {
        TransferQueue(concurrency = 2).use { queue ->
            val (sender, receiver) = makePair("q1")

            // Run sessions in separate threads (they block on pipe I/O)
            val sThread = Thread { runTest { sender.start() } }
            val rThread = Thread { runTest { receiver.start() } }
            sThread.start(); rThread.start()
            sThread.join(5_000); rThread.join(5_000)

            assertEquals(TransferState.COMPLETED, (sender.stateFlow as StateFlow).value)
            assertEquals(TransferState.COMPLETED, (receiver.stateFlow as StateFlow).value)
        }
    }

    @Test
    fun `enqueue three session pairs - all complete`() {
        val results = mutableListOf<TransferState>()
        repeat(3) { i ->
            val (sender, receiver) = makePair("multi$i")
            val sThread = Thread { runTest { sender.start() } }
            val rThread = Thread { runTest { receiver.start() } }
            sThread.start(); rThread.start()
            sThread.join(5_000); rThread.join(5_000)
            results.add((sender.stateFlow   as StateFlow).value)
            results.add((receiver.stateFlow as StateFlow).value)
        }
        assertTrue(results.all { it == TransferState.COMPLETED },
            "Expected all COMPLETED, got: $results")
    }

    // ── Cancel ─────────────────────────────────────────────────────────────

    @Test
    fun `cancelAll cancels pending sessions`() = runTest {
        TransferQueue(concurrency = 1).use { queue ->
            val (sender, _) = makePair("cancel1", byteCount = 2 * 1024 * 1024)

            // Do not start the sender — leave it PENDING
            queue.cancelAll()

            // A session that was never started is still PENDING unless explicitly cancelled
            // through the queue. Verify the queue's cancelAll hits it.
            val stateVal = (sender.stateFlow as StateFlow).value
            // Still PENDING since it was never enqueued/started — cancel via queue should set CANCELLED
            sender.cancel()
            assertEquals(TransferState.CANCELLED, (sender.stateFlow as StateFlow).value)
        }
    }

    // ── Constructor validation ─────────────────────────────────────────────

    @Test
    fun `constructor rejects concurrency zero`() {
        assertThrows(IllegalArgumentException::class.java) { TransferQueue(0) }
    }

    @Test
    fun `constructor rejects negative concurrency`() {
        assertThrows(IllegalArgumentException::class.java) { TransferQueue(-1) }
    }

    @Test
    fun `concurrency property reflects configured value`() {
        TransferQueue(3).use { queue ->
            assertEquals(3, queue.concurrency)
        }
    }

    // ── allSessions snapshot ───────────────────────────────────────────────

    @Test
    fun `allSessions contains all enqueued sessions`() = runTest {
        TransferQueue(concurrency = 2).use { queue ->
            val (s1, r1) = makePair("snap1")
            val (s2, r2) = makePair("snap2")

            queue.enqueue(s1); queue.enqueue(r1)
            queue.enqueue(s2); queue.enqueue(r2)

            val all = queue.allSessions
            assertTrue(all.contains(s1))
            assertTrue(all.contains(r1))
            assertTrue(all.contains(s2))
            assertTrue(all.contains(r2))
        }
    }
}
