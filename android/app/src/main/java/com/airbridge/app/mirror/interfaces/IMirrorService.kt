package com.airbridge.app.mirror.interfaces

import com.airbridge.app.core.interfaces.IMirrorSession
import com.airbridge.app.core.interfaces.MirrorMode
import com.airbridge.app.core.models.DeviceInfo

/**
 * High-level mirror service — manages screen capture sessions.
 * Implemented in Iteration 5/6.
 */
interface IMirrorService {
    suspend fun startMirror(remoteDevice: DeviceInfo, mode: MirrorMode): IMirrorSession
    fun getActiveSessions(): List<IMirrorSession>
}
