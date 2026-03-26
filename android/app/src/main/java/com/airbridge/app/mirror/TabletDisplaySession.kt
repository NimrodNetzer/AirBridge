package com.airbridge.app.mirror

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import com.airbridge.app.core.interfaces.IMirrorSession
import com.airbridge.app.core.interfaces.InputEventArgs
import com.airbridge.app.core.interfaces.MirrorMode
import com.airbridge.app.core.interfaces.MirrorState
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * Android-side session for the "tablet as second monitor" feature.
 *
 * Receives a [MirrorStartMessage] from the Windows host that specifies the
 * virtual display dimensions and codec, then decodes the incoming H.264 NAL
 * stream and renders it to [outputSurface] at native resolution.
 *
 * Mode: [MirrorMode.TABLET_DISPLAY] — Windows is the source, Android is the sink.
 *
 * ## Lifecycle
 * 1. [TabletDisplayActivity] creates a [TabletDisplaySession] with the
 *    [Surface] from its [android.view.SurfaceView].
 * 2. Calls [start] — this sends [MirrorSessionMode.TABLET_DISPLAY] acceptance
 *    (the actual [MirrorStartMessage] comes from Windows) and starts the
 *    message receive loop.
 * 3. On each [MirrorFrameMessage]: feeds NAL data to the [MediaCodec] decoder.
 * 4. Calls [stop] on surface destruction — sends [MirrorStopMessage] and
 *    releases the decoder.
 *
 * @param sessionId     Unique identifier for this session (from Windows).
 * @param channel       Authenticated TLS message channel to the Windows host.
 * @param outputSurface [Surface] provided by [TabletDisplayActivity]; decoded
 *                      frames are rendered directly to this surface.
 */
class TabletDisplaySession(
    override val sessionId: String,
    private val channel:       IMessageChannel,
    private val outputSurface: Surface,
) : IMirrorSession {

    // ── State ──────────────────────────────────────────────────────────────

    override val mode: MirrorMode = MirrorMode.TABLET_DISPLAY

    private val _stateFlow = MutableStateFlow(MirrorState.CONNECTING)

    /** Observe state transitions. Starts as [MirrorState.CONNECTING]. */
    override val stateFlow: StateFlow<MirrorState> = _stateFlow.asStateFlow()

    // ── Internal ───────────────────────────────────────────────────────────

    private val scope = CoroutineScope(Dispatchers.IO)
    private var receiveJob: Job? = null
    private var decoder: MediaCodec? = null

    // Session parameters negotiated in MirrorStartMessage
    @Volatile private var width:  Int  = 1920
    @Volatile private var height: Int  = 1080
    @Volatile private var fps:    Byte = 60

    // Presentation timestamp counter (tracks last submitted PTS)
    private var lastPtsUs: Long = 0L

    // ── IMirrorSession ─────────────────────────────────────────────────────

    /**
     * Starts the receive loop.
     *
     * The [MirrorStartMessage] is expected to arrive from the Windows host
     * shortly after the transport session is established. Once received, the
     * [MediaCodec] decoder is configured and the session transitions to
     * [MirrorState.ACTIVE].
     */
    override suspend fun start() {
        if (_stateFlow.value != MirrorState.CONNECTING) return
        receiveJob = scope.launch { receiveLoop() }
    }

    /** Stops the session: cancels the receive loop, releases the decoder, notifies Windows. */
    override suspend fun stop() {
        val current = _stateFlow.value
        if (current == MirrorState.STOPPED || current == MirrorState.ERROR) return

        receiveJob?.cancel()
        releaseDecoder()

        // Send MirrorStop to Windows
        try {
            val stopMsg = MirrorStopMessage(0)
            channel.send(ProtocolMessage(MessageType.MIRROR_STOP, stopMsg.toBytes()))
        } catch (_: Exception) { /* channel may already be closed */ }

        _stateFlow.value = MirrorState.STOPPED
        scope.cancel()
    }

    /**
     * Not applicable for [MirrorMode.TABLET_DISPLAY] (Windows sends the
     * display; the tablet does not relay input back in the basic display mode).
     */
    override suspend fun sendInput(event: InputEventArgs) {
        // No-op for tablet display mode
    }

    // ── Receive loop ───────────────────────────────────────────────────────

    private suspend fun receiveLoop() {
        try {
            channel.incomingMessages.collect { message ->
                when (message.type) {
                    MessageType.MIRROR_START -> handleMirrorStart(message.payload)
                    MessageType.MIRROR_FRAME -> handleMirrorFrame(message.payload)
                    MessageType.MIRROR_STOP  -> handleMirrorStop()
                    else -> { /* ignore unrelated messages */ }
                }
            }
        } catch (_: Exception) {
            _stateFlow.value = MirrorState.ERROR
        }
    }

    // ── Message handlers ───────────────────────────────────────────────────

    private fun handleMirrorStart(payload: ByteArray) {
        val msg = MirrorStartMessage.fromBytes(payload)
        width  = msg.width
        height = msg.height
        fps    = msg.fps

        initDecoder(width, height, msg.codec)
        _stateFlow.value = MirrorState.ACTIVE
    }

    private fun handleMirrorFrame(payload: ByteArray) {
        if (_stateFlow.value != MirrorState.ACTIVE) return
        val msg = MirrorFrameMessage.fromBytes(payload)
        feedDecoder(msg.nalData, msg.presentationTimestampUs, msg.isKeyFrame)
    }

    private fun handleMirrorStop() {
        releaseDecoder()
        _stateFlow.value = MirrorState.STOPPED
        scope.cancel()
    }

    // ── MediaCodec decoder ─────────────────────────────────────────────────

    /**
     * Initialises the H.264 [MediaCodec] decoder configured to render directly
     * to [outputSurface].
     */
    private fun initDecoder(width: Int, height: Int, codec: MirrorCodec) {
        releaseDecoder()

        val mimeType = when (codec) {
            MirrorCodec.H265 -> MediaFormat.MIMETYPE_VIDEO_HEVC
            else             -> MediaFormat.MIMETYPE_VIDEO_AVC
        }

        val format = MediaFormat.createVideoFormat(mimeType, width, height).apply {
            // Request low-latency decoding (Android 10+)
            setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps.toInt())
        }

        val mediaCodec = MediaCodec.createDecoderByType(mimeType)
        // Configure with the output Surface so decoded frames go directly to display
        mediaCodec.configure(format, outputSurface, null, 0 /* decoder flag */)
        mediaCodec.start()
        decoder = mediaCodec
    }

    /**
     * Submits one NAL unit to the decoder input buffer.
     *
     * On each call we attempt to dequeue an input buffer, copy the NAL data,
     * and queue it back. The decoder renders decoded frames to [outputSurface]
     * automatically via its async output path.
     *
     * @param nalData    Raw H.264 NAL unit bytes (Annex-B or raw NALU).
     * @param ptsUs      Presentation timestamp in microseconds.
     * @param isKeyFrame Whether this NAL unit contains an IDR frame.
     */
    private fun feedDecoder(nalData: ByteArray, ptsUs: Long, isKeyFrame: Boolean) {
        val codec = decoder ?: return
        lastPtsUs = ptsUs

        // Dequeue an available input buffer (timeout 0 = non-blocking)
        val inputIndex = codec.dequeueInputBuffer(0L)
        if (inputIndex < 0) return // no buffer available; drop frame

        val inputBuffer = codec.getInputBuffer(inputIndex) ?: return
        inputBuffer.clear()

        // Truncate if NAL exceeds buffer capacity (should not happen with correct framing)
        val copyLen = minOf(nalData.size, inputBuffer.capacity())
        inputBuffer.put(nalData, 0, copyLen)

        val flags = if (isKeyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
        codec.queueInputBuffer(inputIndex, 0, copyLen, ptsUs, flags)

        // Drain available output frames (render to surface)
        drainDecoder()
    }

    /**
     * Drains all currently available decoder output buffers and renders them
     * to [outputSurface].
     */
    private fun drainDecoder() {
        val codec = decoder ?: return
        val bufferInfo = MediaCodec.BufferInfo()

        while (true) {
            val outputIndex = codec.dequeueOutputBuffer(bufferInfo, 0L)
            when {
                outputIndex >= 0 -> {
                    // render = true: the decoded frame is pushed to the surface
                    codec.releaseOutputBuffer(outputIndex, true)
                }
                outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> break
                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> { /* ignore */ }
                else -> break
            }
        }
    }

    private fun releaseDecoder() {
        try {
            decoder?.stop()
            decoder?.release()
        } catch (_: Exception) { }
        decoder = null
    }
}
