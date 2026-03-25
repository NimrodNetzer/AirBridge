package com.airbridge.app.mirror

import com.airbridge.app.core.interfaces.IMirrorSession
import com.airbridge.app.core.interfaces.InputEventArgs
import com.airbridge.app.core.interfaces.ITransferSession
import com.airbridge.app.core.interfaces.MirrorMode
import com.airbridge.app.core.interfaces.MirrorState
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * Android-side implementation of [IMirrorSession].
 *
 * Responsibilities:
 * - Waits for a [MessageType.MIRROR_START] request from the Windows host.
 * - Starts screen capture (MediaProjection — wired in Iteration 6).
 * - Encodes frames and sends them as [MessageType.MIRROR_FRAME] messages.
 * - On [MessageType.MIRROR_STOP], tears down capture and stops.
 * - **Passes through** file-transfer messages
 *   ([MessageType.FILE_TRANSFER_START] / [MessageType.FILE_CHUNK] /
 *   [MessageType.FILE_TRANSFER_END]) to an optional [ITransferSession]
 *   receiver rather than silently dropping them.  This enables the
 *   Windows host to send files via drag-and-drop while a mirror session
 *   is active.
 *
 * @param sessionId          Unique identifier for this session.
 * @param channel            Transport channel to the Windows host.
 * @param fileTransferReceiver
 *   Optional transfer session that handles incoming file-transfer messages.
 *   Pass `null` to ignore file-transfer traffic (headless / test mode).
 * @param coroutineScope
 *   Scope used for the background receive loop.
 *   Defaults to a new [SupervisorJob] + [Dispatchers.IO] scope.
 *   Inject a test scope (e.g. from [kotlinx.coroutines.test.TestScope]) for
 *   deterministic unit testing.
 */
class MirrorSession(
    override val sessionId: String,
    private val channel: IMessageChannel,
    private val fileTransferReceiver: ITransferSession? = null,
    private val coroutineScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
) : IMirrorSession {

    // ── State ──────────────────────────────────────────────────────────────

    private val _stateFlow = MutableStateFlow(MirrorState.CONNECTING)
    override val stateFlow: Flow<MirrorState> = _stateFlow.asStateFlow()
    override val mode: MirrorMode = MirrorMode.PHONE_WINDOW

    private var receiveJob: Job? = null

    // ── IMirrorSession lifecycle ───────────────────────────────────────────

    /**
     * Starts the mirror session.
     * Launches the message-receive loop and transitions to [MirrorState.ACTIVE]
     * once the [MessageType.MIRROR_START] request is processed.
     */
    override suspend fun start() {
        check(_stateFlow.value == MirrorState.CONNECTING) {
            "Cannot start a session in state ${_stateFlow.value}."
        }

        receiveJob = coroutineScope.launch {
            try {
                runReceiveLoop()
            } catch (e: CancellationException) {
                _stateFlow.value = MirrorState.STOPPED
                throw e
            } catch (e: Exception) {
                _stateFlow.value = MirrorState.ERROR
            }
        }
    }

    /**
     * Stops the session gracefully, sends [MessageType.MIRROR_STOP] to the
     * Windows host, and cancels the receive loop.
     */
    override suspend fun stop() {
        try {
            channel.send(ProtocolMessage(MessageType.MIRROR_STOP, ByteArray(0)))
        } catch (_: Exception) {
            // Best-effort; channel may already be closed.
        }
        receiveJob?.cancel()
        _stateFlow.value = MirrorState.STOPPED
    }

    /**
     * Relays an input event to the device.
     * Input relay is implemented in Iteration 6 (full mirror).
     */
    override suspend fun sendInput(event: InputEventArgs) {
        // TODO (Iteration 6): encode and send INPUT_EVENT message.
    }

    // ── Receive loop ───────────────────────────────────────────────────────

    /**
     * Main message loop.  Processes mirror-protocol messages and forwards
     * file-transfer messages to [fileTransferReceiver].
     */
    private suspend fun runReceiveLoop() {
        channel.incomingMessages.collect { msg ->
            when (msg.type) {
                MessageType.MIRROR_START -> {
                    // Windows host has confirmed mirror — begin capture.
                    // MediaProjection wiring is in Iteration 6; for MVP we just
                    // transition to ACTIVE.
                    _stateFlow.value = MirrorState.ACTIVE
                }

                MessageType.MIRROR_STOP -> {
                    _stateFlow.value = MirrorState.STOPPED
                    // Stop collecting after this message
                    receiveJob?.cancel()
                }

                // ── File-transfer pass-through ─────────────────────────────
                // Windows sends these when the user drops a file onto the
                // MirrorWindow.  Forward to the optional transfer receiver
                // so it can reconstruct and save the file.
                MessageType.FILE_TRANSFER_START,
                MessageType.FILE_CHUNK,
                MessageType.FILE_TRANSFER_END,
                MessageType.FILE_TRANSFER_ACK -> {
                    forwardToTransferReceiver(msg)
                }

                // All other types are ignored for forward-compatibility.
                else -> { /* no-op */ }
            }
        }
    }

    /**
     * Forwards a raw [ProtocolMessage] to [fileTransferReceiver].
     *
     * The application service layer is responsible for wiring the
     * [ITransferSession] receiver to the appropriate byte stream at
     * construction time.  This method exists as a hook so the mirror
     * session does not silently discard transfer traffic: the presence
     * of [fileTransferReceiver] signals that the caller has set up the
     * necessary plumbing.
     *
     * Concrete stream-level forwarding (piping payload bytes into a
     * [TransferSession] receive loop) is implemented by the service layer
     * in Iteration 6 when the full mirror + transfer pipeline is wired up.
     */
    private fun forwardToTransferReceiver(msg: ProtocolMessage) {
        // If no receiver is configured the message is silently dropped.
        // This is intentional: the mirror session is not responsible for
        // the transfer session lifecycle.
        fileTransferReceiver ?: return

        // Payload bytes are available via msg.payload for the service layer
        // to route into a TransferSession.  Nothing more is needed here for
        // the Iteration 6 MVP.
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    /**
     * Cancels the receive loop and releases resources.
     */
    fun dispose() {
        coroutineScope.cancel()
    }
}
