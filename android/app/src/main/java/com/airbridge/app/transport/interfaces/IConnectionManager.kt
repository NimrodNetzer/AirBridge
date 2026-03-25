package com.airbridge.app.transport.interfaces

import com.airbridge.app.core.models.DeviceInfo
import kotlinx.coroutines.flow.Flow

/**
 * Manages TLS 1.3 TCP connections to peer devices.
 * On Android, acts as client — connects outbound to the Windows host.
 */
interface IConnectionManager {
    /** Emits every time a new inbound connection is established. */
    val incomingConnections: Flow<IMessageChannel>

    suspend fun startListening()
    suspend fun stop()
    suspend fun connect(remoteDevice: DeviceInfo): IMessageChannel
}
