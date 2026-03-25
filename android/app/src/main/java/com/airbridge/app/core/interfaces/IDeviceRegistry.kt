package com.airbridge.app.core.interfaces

import com.airbridge.app.core.models.DeviceInfo
import kotlinx.coroutines.flow.Flow

/**
 * Manages the local registry of known and paired devices.
 * Exposes [devicesFlow] as a reactive stream for UI observation.
 * Implementations must be thread-safe.
 */
interface IDeviceRegistry {
    /** Reactive stream of the current device list — emits on every change. */
    val devicesFlow: Flow<List<DeviceInfo>>

    fun getAllDevices(): List<DeviceInfo>
    fun getPairedDevices(): List<DeviceInfo>
    fun addOrUpdate(device: DeviceInfo)
    fun remove(deviceId: String)
    fun isPaired(deviceId: String): Boolean
}
