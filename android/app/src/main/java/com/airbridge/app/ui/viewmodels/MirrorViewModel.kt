package com.airbridge.app.ui.viewmodels

import android.content.Context
import android.content.Intent
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.DeviceConnectionService
import com.airbridge.app.core.interfaces.MirrorMode
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.display.TabletDisplayActivity
import com.airbridge.app.mirror.interfaces.IMirrorService
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/** Models the current state of the mirror feature for the UI. */
sealed class MirrorUiState {
    object Idle : MirrorUiState()
    object Starting : MirrorUiState()
    data class Active(val mode: String) : MirrorUiState()
    data class Error(val message: String) : MirrorUiState()
}

/**
 * ViewModel for the Mirror screen.
 *
 * Delegates session management to [IMirrorService] and navigates to
 * [TabletDisplayActivity] for the tablet-display rendering path.
 */
@HiltViewModel
class MirrorViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val mirrorService: IMirrorService,
    private val deviceConnectionService: DeviceConnectionService,
) : ViewModel() {

    private val _mirrorState = MutableStateFlow<MirrorUiState>(MirrorUiState.Idle)
    val mirrorState: StateFlow<MirrorUiState> = _mirrorState.asStateFlow()

    private var mirrorJob: Job? = null

    /**
     * Starts a Phone-Window mirror session to the given [device].
     * Frames are decoded and rendered in a floating overlay window on the PC side.
     */
    fun startPhoneWindow(device: DeviceInfo) {
        _mirrorState.value = MirrorUiState.Starting
        mirrorJob = viewModelScope.launch {
            try {
                val session = mirrorService.startMirror(device, MirrorMode.PHONE_WINDOW)
                _mirrorState.value = MirrorUiState.Active("Phone Window")
                // Observe session state for error/stop propagation.
                session.stateFlow.collect { mirrorState ->
                    when (mirrorState) {
                        com.airbridge.app.core.interfaces.MirrorState.STOPPED,
                        com.airbridge.app.core.interfaces.MirrorState.ERROR -> {
                            _mirrorState.value = if (mirrorState == com.airbridge.app.core.interfaces.MirrorState.ERROR)
                                MirrorUiState.Error("Mirror session ended with an error.")
                            else
                                MirrorUiState.Idle
                        }
                        else -> Unit
                    }
                }
            } catch (e: Exception) {
                _mirrorState.value = MirrorUiState.Error(e.message ?: "Failed to start mirror")
            }
        }
    }

    /**
     * Launches [TabletDisplayActivity] which owns the full-screen H.264 render surface,
     * then starts a [MirrorMode.TABLET_DISPLAY] session to [device].
     */
    fun startTabletDisplay(device: DeviceInfo) {
        // Retrieve the active channel before launching the activity.
        // TabletDisplayActivity reads pendingChannel in onCreate() and finishes if null.
        val channel = deviceConnectionService.getActiveSession(device.deviceId)
        if (channel == null) {
            _mirrorState.value = MirrorUiState.Error("No active connection to ${device.deviceId}")
            return
        }
        TabletDisplayActivity.pendingChannel = channel

        val intent = Intent(context, TabletDisplayActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK
            putExtra(TabletDisplayActivity.EXTRA_SESSION_ID, "tablet-${device.deviceId}")
        }
        context.startActivity(intent)

        _mirrorState.value = MirrorUiState.Starting
        mirrorJob = viewModelScope.launch {
            try {
                val session = mirrorService.startMirror(device, MirrorMode.TABLET_DISPLAY)
                _mirrorState.value = MirrorUiState.Active("Tablet Display")
                session.stateFlow.collect { mirrorState ->
                    when (mirrorState) {
                        com.airbridge.app.core.interfaces.MirrorState.STOPPED,
                        com.airbridge.app.core.interfaces.MirrorState.ERROR -> {
                            _mirrorState.value = if (mirrorState == com.airbridge.app.core.interfaces.MirrorState.ERROR)
                                MirrorUiState.Error("Tablet display session ended with an error.")
                            else
                                MirrorUiState.Idle
                        }
                        else -> Unit
                    }
                }
            } catch (e: Exception) {
                _mirrorState.value = MirrorUiState.Error(e.message ?: "Failed to start tablet display")
            }
        }
    }

    /** Stops the currently active mirror session. */
    fun stopMirror() {
        mirrorJob?.cancel()
        viewModelScope.launch {
            mirrorService.getActiveSessions().forEach { session ->
                runCatching { session.stop() }
            }
        }
        _mirrorState.value = MirrorUiState.Idle
    }

    override fun onCleared() {
        super.onCleared()
        stopMirror()
    }
}
