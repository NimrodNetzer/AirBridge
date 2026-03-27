package com.airbridge.app.ui.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.models.DeviceInfo
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * ViewModel for the Settings screen.
 *
 * Exposes [pairedDevices] for the "Paired Devices" section and provides
 * [revokePairing] to forget a device.
 */
@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val pairingService: IPairingService,
    private val deviceRegistry: IDeviceRegistry,
) : ViewModel() {

    /** Reactive list of currently paired devices, derived from [IDeviceRegistry.devicesFlow]. */
    val pairedDevices: StateFlow<List<DeviceInfo>> = deviceRegistry.devicesFlow
        .map { devices -> devices.filter { it.isPaired } }
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5_000),
            initialValue = deviceRegistry.getPairedDevices(),
        )

    /**
     * Revokes the pairing with the device identified by [deviceId], removes it from the
     * registry, and cancels all stored keys for that device.
     */
    fun revokePairing(deviceId: String) {
        viewModelScope.launch {
            pairingService.revokePairing(deviceId)
            deviceRegistry.remove(deviceId)
        }
    }
}
