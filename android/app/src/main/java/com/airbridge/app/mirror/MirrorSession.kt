package com.airbridge.app.mirror

import com.airbridge.app.core.interfaces.IMirrorSession
import com.airbridge.app.core.interfaces.InputEventArgs
import com.airbridge.app.core.interfaces.MirrorMode
import com.airbridge.app.core.interfaces.MirrorState
import com.airbridge.app.mirror.interfaces.IMirrorService
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject

/**
 * Result type returned by [MirrorSession] operations at module boundaries.
 */
sealed class MirrorResult {
    /** The operation completed successfully. */
    object Success : MirrorResult()
    /** The operation failed with [message]. */
    data class Failure(val message: String) : MirrorResult()
}

/**
 * Implements [IMirrorSession] for the Android (source) side of a screen mirror.
 *
 * Wires a [ScreenCaptureSession] to an [IMessageChannel]:
 * - On [start]: sends [MirrorStartMessage] to the Windows peer, then streams
 *   [MirrorFrameMessage] for every H.264 NAL buffer emitted by the capture session.
 * - On [stop]: sends [MirrorStopMessage] and releases resources.
 *
 * The caller must supply a [ScreenCaptureSession] that has already had
 * [ScreenCaptureSession.start] called on it before invoking [start].
 *
 * @param sessionId      Unique identifier for this session.
 * @param channel        The TLS message channel to the Windows peer.
 * @param captureSession A running screen capture session.
 * @param width          Capture width (must match what [captureSession] was started with).
 * @param height         Capture height.
 * @param fps            Target frame rate.
 * @param codec          Codec name sent in [MirrorStartMessage], e.g. "H264".
 */
class MirrorSession(
    override val sessionId: String,
    private val channel: IMessageChannel,
    private val captureSession: ScreenCaptureSession,
    private val width: Int,
    private val height: Int,
    private val fps: Int,
    private val codec: String = "H264"
) : IMirrorSession {

    override val mode: MirrorMode = MirrorMode.PHONE_WINDOW

    private val _stateFlow = MutableStateFlow(MirrorState.CONNECTING)

    /** Emits [MirrorState] updates; collect from the UI layer for state-dependent display. */
    override val stateFlow: Flow<MirrorState> = _stateFlow

    /** Convenience [StateFlow] that is `true` while the session is [MirrorState.ACTIVE]. */
    val isActive: StateFlow<Boolean>
        get() = _isActive.asStateFlow()

    private val _isActive = MutableStateFlow(false)

    private val scope = CoroutineScope(Dispatchers.IO + Job())

    @Volatile
    private var streaming = false

    /**
     * Sends [MirrorStartMessage] to the Windows peer and begins streaming encoded frames.
     *
     * Suspends until [stop] is called or the frame flow from the capture session completes.
     */
    override suspend fun start() {
        _stateFlow.value = MirrorState.CONNECTING

        // Send MirrorStart so the Windows side can open its decoder and window
        val startMsg = MirrorStartMessage(sessionId, width, height, fps, codec)
        channel.send(
            ProtocolMessage(
                type    = MessageType.MIRROR_START,
                payload = startMsg.toBytes()
            )
        )

        _stateFlow.value = MirrorState.ACTIVE
        _isActive.value  = true
        streaming        = true

        // Stream frames from the capture session over the channel
        captureSession.frames.collect { frame ->
            if (!streaming) return@collect
            try {
                channel.send(
                    ProtocolMessage(
                        type    = MessageType.MIRROR_FRAME,
                        payload = frame.toBytes()
                    )
                )
            } catch (_: Exception) {
                // Channel disconnected; stop streaming
                streaming        = false
                _isActive.value  = false
                _stateFlow.value = MirrorState.ERROR
            }
        }

        if (_stateFlow.value == MirrorState.ACTIVE) {
            _stateFlow.value = MirrorState.STOPPED
            _isActive.value  = false
        }
    }

    /**
     * Sends [MirrorStopMessage] to the Windows peer, stops the capture session,
     * and cancels the streaming coroutine scope.
     */
    override suspend fun stop() {
        streaming        = false
        _isActive.value  = false
        _stateFlow.value = MirrorState.STOPPED

        try {
            val stopMsg = MirrorStopMessage(sessionId)
            channel.send(
                ProtocolMessage(
                    type    = MessageType.MIRROR_STOP,
                    payload = stopMsg.toBytes()
                )
            )
        } catch (_: Exception) {
            // Best-effort — channel may already be closed
        }

        captureSession.stop()
        scope.cancel()
    }

    /**
     * Not implemented in Iteration 5 (view-only mirror).
     * Input relay will be added in Iteration 6.
     */
    override suspend fun sendInput(event: InputEventArgs) {
        // No-op in Iteration 5
    }
}

/**
 * High-level mirror service — creates [MirrorSession] instances and tracks active sessions.
 *
 * Implements [IMirrorService]. Bound via [com.airbridge.app.mirror.di.MirrorModule].
 */
class MirrorService @Inject constructor() : IMirrorService {

    private val _activeSessions = mutableListOf<IMirrorSession>()

    /**
     * Not the primary entry point; use [startMirrorWithChannel] instead.
     *
     * This overload throws [UnsupportedOperationException] because a channel and capture
     * session must be provided explicitly — they cannot be resolved from [remoteDevice] alone
     * until the connection and MediaProjection permission flow are wired in the UI layer.
     */
    override suspend fun startMirror(
        remoteDevice: DeviceInfo,
        mode: MirrorMode
    ): IMirrorSession {
        throw UnsupportedOperationException(
            "Use startMirrorWithChannel() — a message channel and capture session are required."
        )
    }

    /**
     * Creates a [MirrorSession] wired to [channel] and [captureSession].
     *
     * The caller must:
     * 1. Start the [captureSession] by calling [ScreenCaptureSession.start].
     * 2. Call [IMirrorSession.start] on the returned session to begin streaming.
     *
     * @param sessionId      Unique session identifier.
     * @param channel        Active TLS channel to the Windows peer.
     * @param captureSession A running [ScreenCaptureSession].
     * @param width          Capture width in pixels.
     * @param height         Capture height in pixels.
     * @param fps            Target frame rate.
     * @param codec          Codec string (default "H264").
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
            codec          = codec
        )
        _activeSessions.add(session)
        return session
    }

    /** Returns all currently active mirror sessions. */
    override fun getActiveSessions(): List<IMirrorSession> = _activeSessions.toList()
}
