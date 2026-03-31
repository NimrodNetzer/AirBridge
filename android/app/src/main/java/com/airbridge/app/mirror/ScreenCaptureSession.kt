package com.airbridge.app.mirror

import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Manages a MediaProjection capture pipeline that encodes the screen to H.264.
 *
 * The caller is responsible for obtaining a [MediaProjection] via
 * [android.media.projection.MediaProjectionManager] before constructing this class.
 * This class does NOT handle the Activity-level permission flow.
 *
 * Usage:
 * 1. Collect [frames] to receive encoded NAL buffers.
 * 2. Call [start] to begin capture.
 * 3. Call [stop] to release all resources.
 */
class ScreenCaptureSession(
    private val projection: MediaProjection,
    private val screenDensityDpi: Int
) {
    private val scope = CoroutineScope(Dispatchers.IO + Job())

    // DROP_OLDEST with a tiny buffer: if the network send loop falls behind, drop
    // stale frames immediately rather than queuing 2 seconds of backlog.
    // 4 frames ≈ 133 ms headroom at 30 fps — enough for a TCP burst, not enough
    // to cause visible lag.
    private val _frames = MutableSharedFlow<MirrorFrameMessage>(
        extraBufferCapacity = 4,
        onBufferOverflow    = kotlinx.coroutines.channels.BufferOverflow.DROP_OLDEST
    )

    // SPS/PPS parameter-set bytes emitted by MediaCodec as BUFFER_FLAG_CODEC_CONFIG.
    // Stored here and prepended to every IDR (keyframe) NAL unit so the Windows decoder
    // always receives a self-contained, decodable keyframe regardless of stream position.
    @Volatile private var spsPpsBytes: ByteArray? = null

    /**
     * Flow of encoded [MirrorFrameMessage] objects emitted as each H.264 NAL unit
     * is produced by the encoder. Collect this before calling [start].
     */
    val frames: SharedFlow<MirrorFrameMessage> = _frames

    private var codec: MediaCodec? = null
    private var virtualDisplay: VirtualDisplay? = null

    /** True while the encoder loop is running. */
    @Volatile
    var isRunning: Boolean = false
        private set

    /**
     * Starts screen capture and H.264 encoding at the given parameters.
     *
     * @param sessionId  Identifier embedded in each emitted [MirrorFrameMessage].
     * @param width      Capture width in pixels.
     * @param height     Capture height in pixels.
     * @param fps        Target frame rate (frames per second).
     * @param bitrateBps Target encoder bitrate in bits per second.
     */
    fun start(
        sessionId: String,
        width: Int,
        height: Int,
        fps: Int,
        bitrateBps: Int = DEFAULT_BITRATE_BPS
    ) {
        require(!isRunning) { "ScreenCaptureSession is already running." }
        require(width > 0 && height > 0) { "Width and height must be positive." }
        require(fps in 1..120) { "fps must be in 1..120." }
        require(bitrateBps > 0) { "bitrateBps must be positive." }

        // Configure MediaCodec H.264 encoder
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, bitrateBps)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, I_FRAME_INTERVAL_SECONDS)
            setInteger(
                MediaFormat.KEY_COLOR_FORMAT,
                MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface
            )
            // VBR: Qualcomm/Samsung hardware encoders frequently reset under CBR when
            // using surface input (the encoder can't maintain constant bitrate if the
            // VirtualDisplay surface doesn't feed frames fast enough).  VBR with the
            // 2 Mbps target still keeps frames small while avoiding encoder state cycling.
            setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR)
            // KEY_PRIORITY 1 = non-real-time: the encoder queues frames rather than resetting
            // under load.  Priority 0 (real-time) caused continuous state-cycling
            // (setCodecState 0→1→0→1 every ~2s) that filled the pipeline with IDR-only frames.
            setInteger(MediaFormat.KEY_PRIORITY, 1)
            // 1 frame of encoder latency — minimum that keeps the HW encoder stable
            // at half resolution (540p).  Lower latency = faster frame delivery.
            // API 30+.
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
                setInteger(MediaFormat.KEY_LATENCY, 1)
            }
        }

        val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)

        // Surface input: VirtualDisplay renders directly to the encoder surface
        val inputSurface = encoder.createInputSurface()
        encoder.start()

        // Android 14+ (API 34) requires a callback to be registered before createVirtualDisplay()
        // or IllegalStateException is thrown.  Register a no-op callback on the main looper.
        projection.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                // MediaProjection was stopped externally (e.g. user revoked permission).
                // Mirror the stop back into our own shutdown path.
                if (isRunning) stop()
            }
        }, Handler(Looper.getMainLooper()))

        // Create VirtualDisplay feeding the encoder surface
        val vd = projection.createVirtualDisplay(
            VIRTUAL_DISPLAY_NAME,
            width, height, screenDensityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            inputSurface,
            null, null
        )

        codec          = encoder
        virtualDisplay = vd
        isRunning      = true

        // Drain encoded output in a coroutine
        scope.launch {
            drainEncoder(encoder, sessionId)
        }
    }

    /**
     * Optional callback invoked when the capture session stops for any reason
     * (explicit [stop] call or external MediaProjection revocation).
     * [PhoneCaptureService] wires this to its own [stopCapture] so that a
     * system-initiated projection stop always triggers a clean MIRROR_STOP teardown.
     */
    var onStopped: (() -> Unit)? = null

    /** Stops capture, releases the encoder and virtual display. */
    fun stop() {
        if (!isRunning) return  // already stopped — prevent double-invoke of onStopped
        isRunning = false
        scope.cancel()
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        try { virtualDisplay?.release() } catch (_: Exception) { }
        try { projection.stop() } catch (_: Exception) { }
        codec          = null
        virtualDisplay = null
        onStopped?.invoke()
    }

    /**
     * Requests the encoder to emit a sync (IDR) frame as soon as possible.
     * Called by [MirrorSession] when Windows signals it is ready to decode —
     * ensures the first IDR is not missed due to pipeline startup latency.
     */
    fun requestKeyFrame() {
        val c = codec ?: return
        val params = android.os.Bundle()
        params.putInt(android.media.MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
        try { c.setParameters(params) } catch (_: Exception) { }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /**
     * Reads encoded output buffers from [encoder] in a loop and emits them
     * as [MirrorFrameMessage] objects on [_frames].
     */
    private suspend fun drainEncoder(encoder: MediaCodec, sessionId: String) {
        val bufferInfo = MediaCodec.BufferInfo()

        while (isRunning && scope.isActive) {
            val outputIndex = try {
                encoder.dequeueOutputBuffer(bufferInfo, DEQUEUE_TIMEOUT_US)
            } catch (e: IllegalStateException) {
                // codec.stop() was called while we were blocked in a native dequeue call —
                // this is normal during shutdown; exit the loop cleanly.
                break
            } catch (e: Exception) {
                // Unexpected encoder error — skip this iteration rather than crashing the
                // service.  The encoder will recover or stop() will be called externally.
                continue
            }
            if (outputIndex < 0) continue  // timeout or INFO_* constant — try again

            try {
                val outputBuffer = encoder.getOutputBuffer(outputIndex)
                if (outputBuffer == null) {
                    try { encoder.releaseOutputBuffer(outputIndex, false) } catch (_: IllegalStateException) { break }
                    continue
                }

                if (bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    // SPS/PPS parameter sets — store for prepending to keyframes rather than
                    // skipping.  Without these the Windows H.264 decoder cannot initialise.
                    val config = ByteArray(bufferInfo.size)
                    outputBuffer.position(bufferInfo.offset)
                    outputBuffer.get(config)
                    spsPpsBytes = config
                    try { encoder.releaseOutputBuffer(outputIndex, false) } catch (_: IllegalStateException) { break }
                    continue
                }

                val rawNal = ByteArray(bufferInfo.size)
                outputBuffer.position(bufferInfo.offset)
                outputBuffer.get(rawNal)

                val isKey = (bufferInfo.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0

                // Prepend the stored SPS/PPS bytes to every IDR (keyframe) so the Windows
                // decoder always receives a self-contained decodable unit.
                val nalData = if (isKey) {
                    val sps = spsPpsBytes
                    if (sps != null) sps + rawNal else rawNal
                } else {
                    rawNal
                }

                val frame = MirrorFrameMessage(
                    isKeyFrame              = isKey,
                    presentationTimestampUs = bufferInfo.presentationTimeUs,
                    nalData                 = nalData
                )

                try { encoder.releaseOutputBuffer(outputIndex, false) } catch (_: IllegalStateException) { break }

                // tryEmit: non-blocking. With DROP_OLDEST overflow policy the SharedFlow will
                // discard the oldest buffered frame if full, keeping drainEncoder always running.
                _frames.tryEmit(frame)
            } catch (e: IllegalStateException) {
                // codec.stop() was called while processing — exit cleanly.
                break
            }
        }
    }

    companion object {
        /** Default encoder bitrate: 1.5 Mbps — tuned for half-resolution (540p) capture at 30fps. */
        const val DEFAULT_BITRATE_BPS = 1_500_000
        /** IDR frame interval: 10 s. Longer intervals mean smaller mandatory IDRs and fewer
         *  encoder resets; recovery still happens within ~10 s on reconnect. */
        private const val I_FRAME_INTERVAL_SECONDS = 2
        /** Output buffer dequeue timeout in microseconds (10 ms). */
        private const val DEQUEUE_TIMEOUT_US = 10_000L
        private const val VIRTUAL_DISPLAY_NAME = "AirBridgeMirror"
    }
}
