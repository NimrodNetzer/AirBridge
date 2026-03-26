package com.airbridge.app.mirror

import com.airbridge.app.core.interfaces.ITransferSession
import com.airbridge.app.core.interfaces.MirrorState
import com.airbridge.app.core.interfaces.TransferState
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * Unit tests for [MirrorSession].
 *
 * Covers:
 * - Initial state is [MirrorState.CONNECTING].
 * - File-transfer messages are NOT silently dropped when [fileTransferReceiver] is set.
 * - Null [fileTransferReceiver] is a silent no-op (no NPE).
 * - [stop] sends [MessageType.MIRROR_STOP] to the channel and transitions to STOPPED.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class MirrorSessionTest {

    // ── Fakes ──────────────────────────────────────────────────────────────

    /**
     * Minimal [IMessageChannel] that replays a fixed list of messages then completes.
     * Outbound [send] calls are recorded in [sent].
     */
    private class FakeChannel(
        private val messages: List<ProtocolMessage> = emptyList()
    ) : IMessageChannel {
        override val remoteDeviceId: String  = "fake-device"
        override val isConnected:    Boolean = true
        val sent = mutableListOf<ProtocolMessage>()

        override val incomingMessages: Flow<ProtocolMessage> = flow {
            for (msg in messages) emit(msg)
        }

        override suspend fun send(message: ProtocolMessage) { sent += message }
        override suspend fun close() { /* no-op */ }
    }

    /**
     * Minimal [ITransferSession] stub — tracks state transitions only.
     */
    private class FakeTransferReceiver : ITransferSession {
        override val sessionId:   String = "fake-transfer"
        override val fileName:    String = "fake.bin"
        override val totalBytes:  Long   = 0L
        override val isSender:    Boolean = false

        private val _state    = MutableStateFlow(TransferState.PENDING)
        private val _progress = MutableStateFlow(0L)
        override val stateFlow:    Flow<TransferState> = _state.asStateFlow()
        override val progressFlow: Flow<Long>          = _progress.asStateFlow()

        override suspend fun start()  { _state.value = TransferState.ACTIVE }
        override suspend fun pause()  { _state.value = TransferState.PAUSED }
        override suspend fun resume() { _state.value = TransferState.ACTIVE }
        override suspend fun cancel() { _state.value = TransferState.CANCELLED }
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    /**
     * A freshly constructed [MirrorSession] must report [MirrorState.CONNECTING].
     */
    @Test
    fun `initial state is CONNECTING`() {
        val session = MirrorSession(
            sessionId = "test-session",
            channel   = FakeChannel(),
        )
        assertEquals(MirrorState.CONNECTING, session.stateFlow.value)
    }

    /**
     * After receiving [MessageType.MIRROR_START] then [MessageType.MIRROR_STOP],
     * the session must transition to [MirrorState.STOPPED].
     */
    @Test
    fun `transitions to STOPPED after MIRROR_STOP`() = runTest {
        val channel = FakeChannel(
            messages = listOf(
                ProtocolMessage(MessageType.MIRROR_START, ByteArray(0)),
                ProtocolMessage(MessageType.MIRROR_STOP,  ByteArray(0)),
            )
        )
        // Inject the test's coroutineScope so advanceUntilIdle controls it
        val session = MirrorSession("s1", channel, coroutineScope = this)
        session.start()
        advanceUntilIdle()

        assertEquals(MirrorState.STOPPED, session.stateFlow.value)
    }

    /**
     * File-transfer messages must NOT cause the session to crash.
     * When [fileTransferReceiver] is provided, the session processes them
     * without throwing and completes cleanly.
     */
    @Test
    fun `FILE_TRANSFER messages with receiver do not crash session`() = runTest {
        val channel = FakeChannel(
            messages = listOf(
                ProtocolMessage(MessageType.FILE_TRANSFER_START, ByteArray(4) { 0x10.toByte() }),
                ProtocolMessage(MessageType.FILE_CHUNK,          ByteArray(8) { it.toByte() }),
                ProtocolMessage(MessageType.FILE_TRANSFER_END,   ByteArray(4) { 0x13.toByte() }),
                ProtocolMessage(MessageType.MIRROR_STOP,         ByteArray(0)),
            )
        )

        val receiver = FakeTransferReceiver()
        val session  = MirrorSession("s2", channel, fileTransferReceiver = receiver, coroutineScope = this)
        session.start()
        advanceUntilIdle()

        // Session must have stopped cleanly without exception
        assertEquals(MirrorState.STOPPED, session.stateFlow.value)
    }

    /**
     * When [fileTransferReceiver] is null, incoming file-transfer messages must be
     * silently ignored — no [NullPointerException] or other exception is thrown.
     */
    @Test
    fun `null fileTransferReceiver is silent no-op for FILE_TRANSFER messages`() = runTest {
        val channel = FakeChannel(
            messages = listOf(
                ProtocolMessage(MessageType.FILE_TRANSFER_START, ByteArray(4)),
                ProtocolMessage(MessageType.FILE_CHUNK,          ByteArray(8)),
                ProtocolMessage(MessageType.MIRROR_STOP,         ByteArray(0)),
            )
        )

        val session = MirrorSession("s3", channel, fileTransferReceiver = null, coroutineScope = this)
        session.start()
        advanceUntilIdle()

        assertEquals(MirrorState.STOPPED, session.stateFlow.value)
    }

    /**
     * [MirrorSession.stop] must:
     * 1. Send a [MessageType.MIRROR_STOP] message to the channel.
     * 2. Transition the session to [MirrorState.STOPPED].
     */
    @Test
    fun `stop sends MIRROR_STOP and transitions to STOPPED`() = runTest {
        val channel = FakeChannel()
        val session = MirrorSession("s4", channel, coroutineScope = this)

        session.stop()
        advanceUntilIdle()

        assertTrue(
            channel.sent.any { it.type == MessageType.MIRROR_STOP },
            "Expected MIRROR_STOP to be sent when stop() is called"
        )
        assertEquals(MirrorState.STOPPED, session.stateFlow.value)
    }
}
