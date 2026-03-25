package com.airbridge.app.display.interfaces

/**
 * Renders incoming H.264 frame data to a surface for the tablet-as-display feature.
 * Implemented in Iteration 6.
 */
interface IDisplayRenderer {
    /** Feed a raw H.264 NAL unit for decoding and display. */
    fun onFrame(data: ByteArray, presentationTimeUs: Long, isKeyFrame: Boolean)
    fun release()
}
