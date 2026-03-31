package com.airbridge.app.ui.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.DeviceConnectionService
import com.airbridge.app.core.ReconnectState
import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.interfaces.IDiscoveryService
import com.airbridge.app.transport.protocol.ProtocolMessage
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
    private val deviceConnectionService: DeviceConnectionService,
) : ViewModel() {

    private val _devices = MutableStateFlow<List<DeviceInfo>>(emptyList())
    val devices: StateFlow<List<DeviceInfo>> = _devices.asStateFlow()

    private val _isScanning = MutableStateFlow(false)
    val isScanning: StateFlow<Boolean> = _isScanning.asStateFlow()

    private val _statusMessage = MutableStateFlow("Tap scan to find devices")
    val statusMessage: StateFlow<String> = _statusMessage.asStateFlow()

    /**
     * Mirrors [DeviceConnectionService.reconnectState].
     * Non-null while an outbound reconnect to a paired device is in progress.
     */
    val reconnectState = deviceConnectionService.reconnectState

    /**
     * Emits a non-null error message when all reconnect attempts to a device are exhausted.
     * Observed by the UI to show a dismissible error banner with a Retry button.
     */
    private val _connectionErrorMessage = MutableStateFlow<String?>(null)
    val connectionErrorMessage: StateFlow<String?> = _connectionErrorMessage.asStateFlow()

    private var scanJob: Job? = null
    private var registryJob: Job? = null

    init {
        // Observe the registry for paired/known devices immediately.
        registryJob = viewModelScope.launch {
            deviceRegistry.devicesFlow.collect { list ->
                _devices.value = list
            }
        }
        // Surface permanent connection failures as an error message.
        viewModelScope.launch {
            deviceConnectionService.connectionFailedEvent.collect { deviceId ->
                _connectionErrorMessage.value =
                    "Could not connect to \u201c$deviceId\u201d. Check that the PC is reachable."
            }
        }
        // Auto-start scanning so the user sees devices without tapping the search icon.
        startScan()
    }

    /** Dismisses the current connection error banner. */
    fun dismissConnectionError() {
        _connectionErrorMessage.value = null
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

    /** Adds a device by manual IP entry and returns its deviceId for navigation. */
    fun addManualDevice(ip: String): String {
        val trimmed = ip.trim()
        val deviceId = "manual-$trimmed"
        val device = DeviceInfo(
            deviceId   = deviceId,
            deviceName = trimmed,
            deviceType = DeviceType.WINDOWS_PC,
            ipAddress  = trimmed,
            port       = ProtocolMessage.DEFAULT_PORT,
            isPaired   = false
        )
        deviceRegistry.addOrUpdate(device)
        return deviceId
    }

    override fun onCleared() {
        super.onCleared()
        stopScan()
        registryJob?.cancel()
    }
}
