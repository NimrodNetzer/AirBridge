package com.airbridge.app.ui.viewmodels

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import androidx.activity.result.ActivityResult
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.DeviceConnectionService
import com.airbridge.app.core.interfaces.MirrorMode
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.display.TabletDisplayActivity
import com.airbridge.app.mirror.PhoneCaptureService
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
 * For **phone-window** mirroring the flow is:
 * 1. [startPhoneWindow] records the target [DeviceInfo] and emits a [MediaProjection]
 *    permission [Intent] via [pendingProjectionRequest] — the screen launches it and
 *    calls [onMediaProjectionResult] with the result.
 * 2. On approval, [PhoneCaptureService] is started as a foreground service carrying
 *    the MediaProjection token; it starts [ScreenCaptureSession] + [MirrorSession]
 *    and streams H.264 frames to the Windows PC over the active TLS channel.
 * 3. [stopMirror] sends [PhoneCaptureService.ACTION_STOP] to the service and resets state.
 *
 * For **tablet-display** mode the flow is unchanged: [TabletDisplayActivity] is launched
 * first, then a [MirrorMode.TABLET_DISPLAY] session is started via [IMirrorService].
 */
@HiltViewModel
class MirrorViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val mirrorService: IMirrorService,
    private val deviceConnectionService: DeviceConnectionService,
) : ViewModel() {

    private val _mirrorState = MutableStateFlow<MirrorUiState>(MirrorUiState.Idle)
    val mirrorState: StateFlow<MirrorUiState> = _mirrorState.asStateFlow()

    /**
     * Emits the [Intent] the screen should launch via `rememberLauncherForActivityResult`
     * when a MediaProjection permission request is needed (phone-window mode only).
     * Reset to null after the result is delivered.
     */
    private val _pendingProjectionRequest = MutableStateFlow<Intent?>(null)
    val pendingProjectionRequest: StateFlow<Intent?> = _pendingProjectionRequest.asStateFlow()

    private var mirrorJob: Job? = null

    /** Stores the target device until the MediaProjection result arrives. */
    private var pendingDevice: DeviceInfo? = null

    // ── Phone-window flow ──────────────────────────────────────────────────

    /**
     * Initiates a Phone-Window mirror session to the given [device].
     *
     * Emits the MediaProjection permission [Intent] via [pendingProjectionRequest]; the
     * screen must launch it and deliver the result to [onMediaProjectionResult].
     */
    fun startPhoneWindow(device: DeviceInfo) {
        _mirrorState.value = MirrorUiState.Starting
        pendingDevice = device

        val pm = context.getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        _pendingProjectionRequest.value = pm.createScreenCaptureIntent()
    }

    /**
     * Called by the screen after the MediaProjection permission dialog is dismissed.
     * Starts [PhoneCaptureService] on approval; resets to idle on denial.
     *
     * @param result The [ActivityResult] from the system permission dialog.
     */
    fun onMediaProjectionResult(result: ActivityResult) {
        _pendingProjectionRequest.value = null
        val device = pendingDevice ?: run {
            _mirrorState.value = MirrorUiState.Idle
            return
        }
        pendingDevice = null

        if (result.resultCode != Activity.RESULT_OK || result.data == null) {
            _mirrorState.value = MirrorUiState.Error("Screen capture permission denied.")
            return
        }

        // Verify an active channel exists before starting the service.
        if (deviceConnectionService.getActiveSession(device.deviceId) == null) {
            _mirrorState.value = MirrorUiState.Error("No active connection to ${device.deviceName}.")
            return
        }

        // Start the foreground service that owns the MediaProjection token and drives
        // the ScreenCaptureSession + MirrorSession pipeline.
        val serviceIntent = PhoneCaptureService.startIntent(
            context    = context,
            resultCode = result.resultCode,
            data       = result.data!!,
            deviceId   = device.deviceId,
        )
        context.startForegroundService(serviceIntent)

        _mirrorState.value = MirrorUiState.Active("Phone Window")
    }

    // ── Tablet-display flow ────────────────────────────────────────────────

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
                            _mirrorState.value =
                                if (mirrorState == com.airbridge.app.core.interfaces.MirrorState.ERROR)
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

    // ── Stop ───────────────────────────────────────────────────────────────

    /**
     * Stops the currently active mirror session.
     * For phone-window mode, sends [PhoneCaptureService.ACTION_STOP] to the foreground service.
     * For tablet-display mode, stops the session directly via [IMirrorService].
     */
    fun stopMirror() {
        context.startService(PhoneCaptureService.stopIntent(context))

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
