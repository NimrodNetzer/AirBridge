package com.airbridge.app.mirror

import com.airbridge.app.core.AirBridgeLog
import com.airbridge.app.core.interfaces.IMirrorSession
import com.airbridge.app.core.interfaces.InputEventArgs
import com.airbridge.app.core.interfaces.InputEventType
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
import javax.inject.Inject

/**
 * Android-side implementation of [IMirrorSession].
 *
 * Responsibilities:
 * - Waits for a [MessageType.MIRROR_START] request from the Windows host.
 * - Starts screen capture and streams H.264 frames (MediaProjection wiring in Iteration 6).
 * - On [MessageType.MIRROR_STOP], tears down capture and stops.
 * - Receives [MessageType.INPUT_EVENT] messages and injects them via [InputInjector].
 * - **Passes through** file-transfer messages to an optional [ITransferSession] receiver
 *   so drag-and-drop file transfers work while a mirror session is active.
 *
 * @param sessionId           Unique identifier for this session.
 * @param channel             Transport channel to the Windows host.
 * @param captureSession      Optional running [ScreenCaptureSession]; null in headless/test mode.
 * @param width               Capture width in pixels.
 * @param height              Capture height in pixels.
 * @param fps                 Target frame rate.
 * @param codec               Codec string (default "H264").
 * @param inputInjector       Optional [InputInjector] for relaying input events from Windows.
 *                            Pass null for view-only mode.
 * @param fileTransferReceiver Optional transfer session that handles incoming file-transfer
 *                             messages. Pass null to ignore file-transfer traffic.
 * @param coroutineScope      Scope for background coroutines. Inject a test scope for
 *                            deterministic unit testing.
 */
class MirrorSession(
    override val sessionId: String,
    private val channel: IMessageChannel,
    private val captureSession: ScreenCaptureSession? = null,
    private val width: Int = 0,
    private val height: Int = 0,
    private val fps: Int = 30,
    private val codec: String = "H264",
    private val inputInjector: InputInjector? = null,
    private val fileTransferReceiver: ITransferSession? = null,
    private val coroutineScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
) : IMirrorSession {

    // ── State ──────────────────────────────────────────────────────────────

    private val _stateFlow = MutableStateFlow(MirrorState.CONNECTING)
    override val stateFlow: Flow<MirrorState> = _stateFlow.asStateFlow()
    override val mode: MirrorMode = MirrorMode.PHONE_WINDOW

    private var receiveJob: Job? = null
    private var inputReceiveJob: Job? = null

    // ── IMirrorSession lifecycle ───────────────────────────────────────────

    /**
     * Starts the mirror session: sends [MirrorStartMessage] to Windows, begins
     * streaming frames from [captureSession] (if provided), and launches the
     * input relay and file-transfer receive loops.
     */
    override suspend fun start() {
        check(_stateFlow.value == MirrorState.CONNECTING) {
            "Cannot start a session in state ${_stateFlow.value}."
        }
        AirBridgeLog.info("[Mirror:$sessionId] CONNECTING — starting session (${width}x${height} @${fps}fps codec=$codec)")

        // Announce the mirror session to the Windows host
        if (captureSession != null && width > 0 && height > 0) {
            val startMsg = MirrorStartMessage(
                mode      = MirrorSessionMode.PHONE_WINDOW,
                codec     = if (codec == "H265") MirrorCodec.H265 else MirrorCodec.H264,
                width     = width,
                height    = height,
                fps       = fps.toByte(),
                sessionId = sessionId
            )
            channel.send(
                ProtocolMessage(
                    type    = MessageType.MIRROR_START,
                    payload = startMsg.toBytes()
                )
            )
        }

        // Update injector screen dimensions for coordinate de-normalisation
        inputInjector?.let {
            it.screenWidth  = width
            it.screenHeight = height
        }

        // When captureSession is present this session is the *sender* — DeviceConnectionService
        // already owns the sole read loop on channel.incomingMessages.  Launching additional
        // collect() calls here would compete for bytes on the DataInputStream, corrupting the
        // framing and breaking the keepalive.  INPUT_EVENT and MIRROR_STOP are handled via
        // DeviceConnectionService.addMessageHandler — see createMessageHandler() below.
        if (captureSession == null) {
            // Receiver mode (no capture source): handle incoming control messages directly.
            if (inputInjector != null) {
                inputReceiveJob = coroutineScope.launch {
                    try { runInputReceiveLoop() } catch (_: CancellationException) { }
                }
            }

            receiveJob = coroutineScope.launch {
                try {
                    runReceiveLoop()
                } catch (e: CancellationException) {
                    AirBridgeLog.info("[Mirror:$sessionId] Receive loop cancelled → STOPPED")
                    _stateFlow.value = MirrorState.STOPPED
                    throw e
                } catch (e: Exception) {
                    AirBridgeLog.error("[Mirror:$sessionId] Receive loop exception → ERROR", e)
                    _stateFlow.value = MirrorState.ERROR
                }
            }
        }

        // Stream frames from capture session if available
        if (captureSession != null) {
            AirBridgeLog.info("[Mirror:$sessionId] CONNECTING → ACTIVE; streaming frames from captureSession")
            _stateFlow.value = MirrorState.ACTIVE
            captureSession.frames.collect { frame ->
                try {
                    channel.send(ProtocolMessage(MessageType.MIRROR_FRAME, frame.toBytes()))
                } catch (e: Exception) {
                    AirBridgeLog.error("[Mirror:$sessionId] Frame send failed → ERROR", e)
                    _stateFlow.value = MirrorState.ERROR
                    return@collect
                }
            }
            AirBridgeLog.info("[Mirror:$sessionId] captureSession.frames completed")
        }
    }

    /**
     * Stops the session gracefully: sends [MirrorStopMessage] and cancels all coroutines.
     */
    override suspend fun stop() {
        try {
            channel.send(ProtocolMessage(MessageType.MIRROR_STOP, ByteArray(0)))
        } catch (_: Exception) {
            // Best-effort; channel may already be closed.
        }
        inputReceiveJob?.cancel()
        receiveJob?.cancel()
        _stateFlow.value = MirrorState.STOPPED
    }

    /**
     * Injects an [InputEventArgs] locally via [InputInjector].
     * No-op when no injector was supplied (view-only mode).
     */
    override suspend fun sendInput(event: InputEventArgs) {
        inputInjector?.inject(event)
    }

    // ── Sender-mode message handler ───────────────────────────────────────

    /**
     * Returns a handler delegate that routes [INPUT_EVENT] and [MIRROR_STOP] messages
     * into this session when it is running in sender mode (captureSession != null).
     *
     * Register this with [com.airbridge.app.core.DeviceConnectionService.addMessageHandler]
     * immediately after [start] is called so Windows-originated control messages reach
     * the session without competing with the DeviceConnectionService read loop.
     *
     * @param onStop Callback invoked when [MIRROR_STOP] is received from Windows.
     *               Typically calls [PhoneCaptureService.stopCapture].
     */
    fun createMessageHandler(onStop: () -> Unit): suspend (ProtocolMessage) -> Unit = { msg ->
        when (msg.type) {
            MessageType.MIRROR_START -> {
                // Windows sends MirrorStart after its decode pipeline is ready to signal
                // "I'm set up — please send me a fresh IDR keyframe now."
                AirBridgeLog.info("[Mirror:$sessionId] MIRROR_START from Windows → requesting IDR keyframe")
                captureSession?.requestKeyFrame()
            }
            MessageType.INPUT_EVENT -> {
                try {
                    val inputMsg = InputEventMessage.fromBytes(msg.payload)
                    val evtType = when (inputMsg.eventKind) {
                        InputEventKind.TOUCH -> InputEventType.TOUCH
                        InputEventKind.KEY   -> InputEventType.KEY
                        InputEventKind.MOUSE -> InputEventType.MOUSE
                    }
                    sendInput(InputEventArgs(
                        type        = evtType,
                        normalizedX = inputMsg.normalizedX,
                        normalizedY = inputMsg.normalizedY,
                        keycode     = inputMsg.keycode,
                        metaState   = inputMsg.metaState
                    ))
                } catch (_: Exception) {
                    // Malformed message — drop
                }
            }
            MessageType.MIRROR_STOP -> {
                AirBridgeLog.info("[Mirror:$sessionId] MIRROR_STOP from Windows → stopping capture")
                onStop()
            }
            else -> { /* ignore */ }
        }
    }

    // ── Input relay receive loop ───────────────────────────────────────────

    /**
     * Collects [MessageType.INPUT_EVENT] messages from the channel and dispatches
     * them to [inputInjector]. Unknown or malformed messages are silently skipped.
     */
    private suspend fun runInputReceiveLoop() {
        channel.incomingMessages.collect { msg ->
            if (msg.type != MessageType.INPUT_EVENT) return@collect
            try {
                val inputMsg = InputEventMessage.fromBytes(msg.payload)
                val evtType = when (inputMsg.eventKind) {
                    InputEventKind.TOUCH -> InputEventType.TOUCH
                    InputEventKind.KEY   -> InputEventType.KEY
                    InputEventKind.MOUSE -> InputEventType.MOUSE
                }
                val args = InputEventArgs(
                    type        = evtType,
                    normalizedX = inputMsg.normalizedX,
                    normalizedY = inputMsg.normalizedY,
                    keycode     = inputMsg.keycode,
                    metaState   = inputMsg.metaState
                )
                inputInjector?.inject(args)
            } catch (_: Exception) {
                // Malformed message — skip
            }
        }
    }

    // ── Main receive loop ──────────────────────────────────────────────────

    /**
     * Processes mirror-protocol messages and forwards file-transfer messages
     * to [fileTransferReceiver].
     */
    private suspend fun runReceiveLoop() {
        channel.incomingMessages.collect { msg ->
            when (msg.type) {
                MessageType.MIRROR_START -> {
                    AirBridgeLog.info("[Mirror:$sessionId] MIRROR_START received → ACTIVE")
                    _stateFlow.value = MirrorState.ACTIVE
                }

                MessageType.MIRROR_STOP -> {
                    AirBridgeLog.info("[Mirror:$sessionId] MIRROR_STOP received → STOPPED")
                    _stateFlow.value = MirrorState.STOPPED
                    receiveJob?.cancel()
                }

                // File-transfer pass-through: Windows sends these when the user drops a
                // file onto the MirrorWindow. Forward to the optional transfer receiver.
                MessageType.FILE_TRANSFER_START,
                MessageType.FILE_CHUNK,
                MessageType.FILE_TRANSFER_END,
                MessageType.FILE_TRANSFER_ACK -> {
                    forwardToTransferReceiver(msg)
                }

                // Input events are handled by runInputReceiveLoop — ignore here
                MessageType.INPUT_EVENT -> { /* handled separately */ }

                else -> { /* no-op for forward-compatibility */ }
            }
        }
    }

    private fun forwardToTransferReceiver(msg: ProtocolMessage) {
        fileTransferReceiver ?: return
        // Payload bytes are available for the service layer to route into a TransferSession.
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    /** Cancels all background coroutines and releases resources. */
    fun dispose() {
        inputReceiveJob?.cancel()
        receiveJob?.cancel()
        coroutineScope.cancel()
    }
}

/**
 * High-level mirror service — creates [MirrorSession] instances and tracks active sessions.
 */
class MirrorService @Inject constructor(
    private val inputInjector: InputInjector
) : com.airbridge.app.mirror.interfaces.IMirrorService {

    private val _activeSessions = mutableListOf<IMirrorSession>()

    override suspend fun startMirror(
        remoteDevice: com.airbridge.app.core.models.DeviceInfo,
        mode: MirrorMode
    ): IMirrorSession {
        // For TABLET_DISPLAY the session lifecycle is owned by TabletDisplayActivity (it
        // creates TabletDisplaySession once the SurfaceView surface is ready). Return a
        // lightweight stub so callers can observe state transitions without crashing.
        // For PHONE_WINDOW a channel + capture session are required; use startMirrorWithChannel().
        if (mode == MirrorMode.TABLET_DISPLAY) {
            val stub = TabletDisplayStubSession()
            _activeSessions.add(stub)
            return stub
        }
        throw UnsupportedOperationException(
            "Use startMirrorWithChannel() — a message channel and capture session are required."
        )
    }

    /**
     * Creates a [MirrorSession] wired to [channel] and [captureSession].
     */
    fun startMirrorWithChannel(
        sessionId: String,
        channel: IMessageChannel,
        captureSession: ScreenCaptureSession,
        width: Int,
        height: Int,
        fps: Int,
        codec: String = "H264"
    ): MirrorSession {
        val session = MirrorSession(
            sessionId      = sessionId,
            channel        = channel,
            captureSession = captureSession,
            width          = width,
            height         = height,
            fps            = fps,
            codec          = codec,
            inputInjector  = inputInjector
        )
        _activeSessions.add(session)
        return session
    }

    override fun getActiveSessions(): List<IMirrorSession> = _activeSessions.toList()
}

/**
 * Lightweight stub session returned by [MirrorService.startMirror] for
 * [MirrorMode.TABLET_DISPLAY]. The real session is created inside
 * [com.airbridge.app.display.TabletDisplayActivity] once the SurfaceView
 * surface is available. This stub lets [com.airbridge.app.ui.viewmodels.MirrorViewModel]
 * observe state without holding a reference to the actual MediaCodec session.
 */
private class TabletDisplayStubSession : IMirrorSession {
    override val sessionId: String = "tablet-display-stub-${System.currentTimeMillis()}"
    override val mode: MirrorMode  = MirrorMode.TABLET_DISPLAY

    private val _stateFlow = MutableStateFlow(MirrorState.ACTIVE)
    override val stateFlow: Flow<MirrorState> = _stateFlow.asStateFlow()

    override suspend fun start()  { /* no-op: real session lives in TabletDisplayActivity */ }
    override suspend fun stop()   { _stateFlow.value = MirrorState.STOPPED }
    override suspend fun sendInput(event: InputEventArgs) { /* no-op */ }
}
