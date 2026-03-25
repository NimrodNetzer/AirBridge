package com.airbridge.app.core.interfaces

import kotlinx.coroutines.flow.Flow

enum class MirrorMode { PHONE_WINDOW, TABLET_DISPLAY }
enum class MirrorState { CONNECTING, ACTIVE, PAUSED, STOPPED, ERROR }

data class InputEventArgs(
    val type: InputEventType,
    val normalizedX: Float,
    val normalizedY: Float,
    val keycode: Int? = null,
    val metaState: Int = 0
)

enum class InputEventType { TOUCH, KEY, MOUSE }

/**
 * Represents an active screen mirror session.
 * On Android, this is the *source* side — captures screen and sends frames.
 */
interface IMirrorSession {
    val sessionId: String
    val mode: MirrorMode
    val stateFlow: Flow<MirrorState>

    suspend fun start()
    suspend fun stop()
    suspend fun sendInput(event: InputEventArgs)
}
