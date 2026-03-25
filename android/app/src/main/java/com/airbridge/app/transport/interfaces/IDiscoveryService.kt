package com.airbridge.app.transport.interfaces

import com.airbridge.app.core.models.DeviceInfo
import kotlinx.coroutines.flow.Flow

/**
 * Advertises this device on the local network and discovers peers
 * using mDNS (service type: _airbridge._tcp.local) via NsdManager.
 */
interface IDiscoveryService {
    /** Emits the current list of visible devices; updates on any change. */
    val visibleDevicesFlow: Flow<List<DeviceInfo>>

    suspend fun start()
    suspend fun stop()
    fun getVisibleDevices(): List<DeviceInfo>
}
