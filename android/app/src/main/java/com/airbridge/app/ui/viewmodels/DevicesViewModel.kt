package com.airbridge.app.ui.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IDiscoveryService
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * ViewModel for the Devices screen.
 *
 * Drives mDNS scanning via [IDiscoveryService] and exposes the live device list
 * from [IDeviceRegistry.devicesFlow] for UI observation.
 */
@HiltViewModel
class DevicesViewModel @Inject constructor(
    private val discoveryService: IDiscoveryService,
    private val deviceRegistry: IDeviceRegistry,
) : ViewModel() {

    private val _devices = MutableStateFlow<List<DeviceInfo>>(emptyList())
    val devices: StateFlow<List<DeviceInfo>> = _devices.asStateFlow()

    private val _isScanning = MutableStateFlow(false)
    val isScanning: StateFlow<Boolean> = _isScanning.asStateFlow()

    private val _statusMessage = MutableStateFlow("Tap scan to find devices")
    val statusMessage: StateFlow<String> = _statusMessage.asStateFlow()

    private var scanJob: Job? = null
    private var registryJob: Job? = null

    init {
        // Observe the registry for paired/known devices immediately.
        registryJob = viewModelScope.launch {
            deviceRegistry.devicesFlow.collect { list ->
                _devices.value = list
            }
        }
    }

    /** Starts mDNS discovery and merges discovered devices into the registry. */
    fun startScan() {
        if (_isScanning.value) return
        _isScanning.value = true
        _statusMessage.value = "Scanning for devices…"

        scanJob = viewModelScope.launch {
            try {
                discoveryService.start()
                discoveryService.visibleDevicesFlow.collect { discovered ->
                    discovered.forEach { device ->
                        val alreadyKnown = deviceRegistry.getAllDevices()
                            .any { it.deviceId == device.deviceId }
                        if (!alreadyKnown) deviceRegistry.addOrUpdate(device)
                    }
                    _statusMessage.value =
                        if (discovered.isEmpty()) "No devices found yet…"
                        else "Found ${discovered.size} device(s)"
                }
            } catch (e: Exception) {
                _statusMessage.value = "Scan failed: ${e.message}"
            } finally {
                _isScanning.value = false
            }
        }
    }

    /** Stops the ongoing mDNS scan. */
    fun stopScan() {
        scanJob?.cancel()
        viewModelScope.launch {
            runCatching { discoveryService.stop() }
        }
        _isScanning.value = false
        _statusMessage.value = "Scan stopped"
    }

    override fun onCleared() {
        super.onCleared()
        stopScan()
        registryJob?.cancel()
    }
}
