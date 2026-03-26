package com.airbridge.app.core.registry

import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.models.DeviceInfo
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Thread-safe in-memory implementation of [IDeviceRegistry].
 *
 * Devices are stored in a [ConcurrentHashMap] keyed by [DeviceInfo.deviceId].
 * [devicesFlow] emits a fresh snapshot whenever the map changes.
 */
@Singleton
class InMemoryDeviceRegistry @Inject constructor() : IDeviceRegistry {

    private val _map = ConcurrentHashMap<String, DeviceInfo>()
    private val _stateFlow = MutableStateFlow<Map<String, DeviceInfo>>(emptyMap())

    /** Reactive stream of all known devices — emits on every add/update/remove. */
    override val devicesFlow: Flow<List<DeviceInfo>>
        get() = _stateFlow.asStateFlow().map { it.values.toList() }

    override fun getAllDevices(): List<DeviceInfo> = _map.values.toList()

    override fun getPairedDevices(): List<DeviceInfo> =
        _map.values.filter { it.isPaired }

    override fun addOrUpdate(device: DeviceInfo) {
        _map[device.deviceId] = device
        _stateFlow.value = _map.toMap()
    }

    override fun remove(deviceId: String) {
        _map.remove(deviceId)
        _stateFlow.value = _map.toMap()
    }

    override fun isPaired(deviceId: String): Boolean =
        _map[deviceId]?.isPaired == true
}
