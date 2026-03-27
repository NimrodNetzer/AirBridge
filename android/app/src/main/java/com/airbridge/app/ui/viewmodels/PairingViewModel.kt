package com.airbridge.app.ui.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.DeviceConnectionService
import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IConnectionManager
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/** Models the lifecycle of a pairing attempt for the UI. */
sealed class PairingState {
    object Idle : PairingState()
    object Connecting : PairingState()
    data class WaitingForPin(val pin: String) : PairingState()
    object Success : PairingState()
    data class Failed(val message: String) : PairingState()
}

/**
 * ViewModel for the Pairing screen.
 *
 * Drives the TOFU Ed25519 key-exchange via [IPairingService] and exposes
 * [pairingState] for the UI to react to each step of the handshake.
 */
@HiltViewModel
class PairingViewModel @Inject constructor(
    private val pairingService: IPairingService,
    private val connectionManager: IConnectionManager,
    private val deviceRegistry: IDeviceRegistry,
    private val deviceConnectionService: DeviceConnectionService,
) : ViewModel() {

    private val _pairingState = MutableStateFlow<PairingState>(PairingState.Idle)
    val pairingState: StateFlow<PairingState> = _pairingState.asStateFlow()

    private var pairingJob: Job? = null

    /**
     * Looks up [deviceId] in the registry and starts the outbound pairing handshake.
     * If the device is not found, transitions immediately to [PairingState.Failed].
     */
    fun startPairing(deviceId: String) {
        val device = deviceRegistry.getAllDevices().find { it.deviceId == deviceId }
        if (device == null) {
            _pairingState.value = PairingState.Failed("Device not found. Please scan again.")
            return
        }
        startPairingWithDevice(device)
    }

    private fun startPairingWithDevice(device: DeviceInfo) {
        if (_pairingState.value is PairingState.Connecting ||
            _pairingState.value is PairingState.WaitingForPin
        ) return

        _pairingState.value = PairingState.Connecting

        pairingJob = viewModelScope.launch {
            try {
                val channel = connectionManager.connect(device)

                // Observe the PIN flow via the interface — no concrete cast needed.
                val pinObserverJob = launch {
                    pairingService.pinFlow.collect { pin ->
                        if (pin != null) {
                            _pairingState.value = PairingState.WaitingForPin(pin)
                        }
                    }
                }

                val result = pairingService.requestPairingOnChannel(device, channel)
                pinObserverJob.cancel()

                // Keep the pairing channel alive as the active session on success.
                if (result == PairingResult.SUCCESS || result == PairingResult.ALREADY_PAIRED) {
                    deviceConnectionService.registerSession(device.deviceId, channel)
                }

                handleResult(result, device)
            } catch (e: Exception) {
                _pairingState.value = PairingState.Failed(e.message ?: "Unknown error")
            }
        }
    }

    /**
     * Called when the Windows side initiates pairing to this Android device.
     * The [DeviceConnectionService] has already read the first message and determined it is a
     * PAIRING_REQUEST; this method drives the inbound handshake to completion and updates
     * [pairingState] accordingly.
     */
    fun handleIncomingPairingRequest(device: DeviceInfo, channel: com.airbridge.app.transport.interfaces.IMessageChannel) {
        if (_pairingState.value is PairingState.Connecting ||
            _pairingState.value is PairingState.WaitingForPin
        ) return

        _pairingState.value = PairingState.Connecting

        pairingJob = viewModelScope.launch {
            try {
                // Observe the PIN emitted by acceptPairingOnChannel so the UI can display it.
                val pinObserverJob = launch {
                    pairingService.pinFlow.collect { pin ->
                        if (pin != null) {
                            _pairingState.value = PairingState.WaitingForPin(pin)
                        }
                    }
                }

                val result = pairingService.acceptPairingOnChannel(device, channel)
                pinObserverJob.cancel()

                handleResult(result, device)
            } catch (e: Exception) {
                _pairingState.value = PairingState.Failed(e.message ?: "Unknown error")
            }
        }
    }

    /**
     * Confirms or rejects the currently displayed PIN for an inbound pairing request.
     * Must be called after [pairingState] has transitioned to [PairingState.WaitingForPin]
     * during an inbound flow started by [handleIncomingPairingRequest].
     */
    fun acceptIncomingPairing(accepted: Boolean) {
        viewModelScope.launch {
            pairingService.confirmPairing(accepted)
        }
    }

    private fun handleResult(result: PairingResult, device: DeviceInfo) {
        deviceRegistry.addOrUpdate(device.copy(isPaired = result == PairingResult.SUCCESS || result == PairingResult.ALREADY_PAIRED))
        _pairingState.value = when (result) {
            PairingResult.SUCCESS -> PairingState.Success
            PairingResult.ALREADY_PAIRED -> PairingState.Success
            PairingResult.REJECTED_BY_USER -> PairingState.Failed("Rejected on the remote device.")
            PairingResult.TIMEOUT -> PairingState.Failed("Pairing timed out. Please try again.")
            PairingResult.ERROR -> PairingState.Failed("An error occurred during pairing.")
        }
    }

    /** Cancels the in-progress pairing attempt. */
    fun cancel() {
        pairingJob?.cancel()
        _pairingState.value = PairingState.Idle
    }

    override fun onCleared() {
        super.onCleared()
        cancel()
    }
}
