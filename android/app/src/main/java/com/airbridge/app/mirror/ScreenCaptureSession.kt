package com.airbridge.app.mirror

import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
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

    private val _frames = MutableSharedFlow<MirrorFrameMessage>(extraBufferCapacity = 60)

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
        }

        val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)

        // Surface input: VirtualDisplay renders directly to the encoder surface
        val inputSurface = encoder.createInputSurface()
        encoder.start()

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

    /** Stops capture, releases the encoder and virtual display. */
    fun stop() {
        isRunning = false
        scope.cancel()
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        try { virtualDisplay?.release() } catch (_: Exception) { }
        try { projection.stop() } catch (_: Exception) { }
        codec          = null
        virtualDisplay = null
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /**
     * Reads encoded output buffers from [encoder] in a loop and emits them
     * as [MirrorFrameMessage] objects on [_frames].
     */
    private suspend fun drainEncoder(encoder: MediaCodec, sessionId: String) {
        val bufferInfo = MediaCodec.BufferInfo()

        while (isRunning && scope.isActive) {
            val outputIndex = encoder.dequeueOutputBuffer(bufferInfo, DEQUEUE_TIMEOUT_US)
            if (outputIndex < 0) continue  // timeout or INFO_* constant — try again

            try {
                val outputBuffer = encoder.getOutputBuffer(outputIndex) ?: continue
                if (bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    // SPS/PPS config data — skip for now (included in first keyframe on many devices)
                    encoder.releaseOutputBuffer(outputIndex, false)
                    continue
                }

                val nalData = ByteArray(bufferInfo.size)
                outputBuffer.position(bufferInfo.offset)
                outputBuffer.get(nalData)

                val isKey = (bufferInfo.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                val tsMs  = bufferInfo.presentationTimeUs / 1_000L

                val frame = MirrorFrameMessage(
                    sessionId   = sessionId,
                    timestampMs = tsMs,
                    isKeyFrame  = isKey,
                    nalData     = nalData
                )
                _frames.tryEmit(frame)
            } finally {
                encoder.releaseOutputBuffer(outputIndex, false)
            }
        }
    }

    companion object {
        /** Default encoder bitrate: 4 Mbps. */
        const val DEFAULT_BITRATE_BPS = 4_000_000
        /** IDR frame interval in seconds. */
        private const val I_FRAME_INTERVAL_SECONDS = 2
        /** Output buffer dequeue timeout in microseconds (10 ms). */
        private const val DEQUEUE_TIMEOUT_US = 10_000L
        private const val VIRTUAL_DISPLAY_NAME = "AirBridgeMirror"
    }
}
