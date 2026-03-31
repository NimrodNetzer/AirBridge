package com.airbridge.app.ui.viewmodels

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.airbridge.app.core.DeviceConnectionService
import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.interfaces.TransferState
import com.airbridge.app.transfer.TransferNotificationManager
import com.airbridge.app.transfer.interfaces.IFileTransferService
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * UI state snapshot for a single transfer session shown in the list.
 *
 * @property sessionId         Unique transfer identifier.
 * @property fileName          Name of the file being transferred.
 * @property totalBytes        Total file size in bytes.
 * @property transferredBytes  Bytes transferred so far.
 * @property state             Current [TransferState].
 * @property speedBytesPerSec  Current transfer speed in bytes per second, or null if unknown.
 * @property etaSeconds        Estimated seconds remaining, or null if unknown.
 * @property errorMessage      Human-readable error description when [state] is [TransferState.FAILED].
 * @property sourceUri         The original [Uri] for re-sending on retry; null for received files.
 */
data class TransferSessionUiState(
    val sessionId: String,
    val fileName: String,
    val totalBytes: Long,
    val transferredBytes: Long,
    val state: TransferState,
    val speedBytesPerSec: Long? = null,
    val etaSeconds: Long? = null,
    val errorMessage: String? = null,
    val sourceUri: Uri? = null,
)

/**
 * ViewModel for the Transfer screen.
 *
 * Bridges the file-picker [Uri] to the [IFileTransferService] and maintains
 * the [activeSessions] list for UI observation.
 * Also exposes [reconnectState] from [DeviceConnectionService] so the Transfer screen
 * can show a "Reconnecting…" banner when the connection drops.
 */
@HiltViewModel
class TransferViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val transferService: IFileTransferService,
    private val deviceRegistry: IDeviceRegistry,
    private val notificationManager: TransferNotificationManager,
    private val deviceConnectionService: DeviceConnectionService,
) : ViewModel() {

    private val _activeSessions = MutableStateFlow<List<TransferSessionUiState>>(emptyList())
    val activeSessions: StateFlow<List<TransferSessionUiState>> = _activeSessions.asStateFlow()

    private val _errorMessage = MutableStateFlow<String?>(null)
    val errorMessage: StateFlow<String?> = _errorMessage.asStateFlow()

    /**
     * The device ID of the currently connected device, or null when no session is open.
     * Derived from [DeviceConnectionService.connectedDeviceIds] and updated reactively.
     */
    private val _connectedDeviceId = MutableStateFlow<String?>(null)
    val connectedDeviceId: StateFlow<String?> = _connectedDeviceId.asStateFlow()

    /**
     * Mirrors [DeviceConnectionService.reconnectState] for UI consumption.
     * Non-null while the transport layer is trying to reconnect.
     */
    val reconnectState = deviceConnectionService.reconnectState

    /**
     * Emits a non-null error message when a connection attempt fails permanently.
     * The UI should show this as a dismissible error banner with a Retry button.
     */
    private val _connectionErrorMessage = MutableStateFlow<String?>(null)
    val connectionErrorMessage: StateFlow<String?> = _connectionErrorMessage.asStateFlow()

    /** Tracks which device IDs we have already registered a receive handler for. */
    private val _handlerRegisteredFor = mutableSetOf<String>()

    /** Tracks the last URI sent per session so that failed transfers can be retried. */
    private val _pendingRetryUris = mutableMapOf<String, Uri>()

    init {
        // Observe connected device IDs from the session manager and surface the first one.
        // Also register the inbound file-transfer handler when a new device connects.
        viewModelScope.launch {
            deviceConnectionService.connectedDeviceIds.collect { ids ->
                _connectedDeviceId.value = ids.firstOrNull()

                // Register receive handler for any newly connected device.
                for (id in ids) {
                    if (_handlerRegisteredFor.add(id)) {
                        transferService.registerReceiveHandler(id)
                    }
                }
            }
        }

        // Surface permanent connection failures as a dismissible error message.
        viewModelScope.launch {
            deviceConnectionService.connectionFailedEvent.collect { deviceId ->
                _connectionErrorMessage.value =
                    "Could not connect to device \u201c$deviceId\u201d. Check that the PC is reachable."
            }
        }
    }

    /** Dismisses the current connection error banner. */
    fun dismissConnectionError() {
        _connectionErrorMessage.value = null
    }

    /**
     * Sends the file identified by [uri] to the first device with an active session.
     * Falls back to the first paired device in the registry if no session is open.
     * No-ops silently if neither is available (the UI should guard against this).
     */
    fun sendFile(uri: Uri) {
        val activeId = _connectedDeviceId.value
        val device = if (activeId != null)
            deviceRegistry.getAllDevices().find { it.deviceId == activeId }
                ?: deviceRegistry.getPairedDevices().firstOrNull()
        else
            deviceRegistry.getPairedDevices().firstOrNull()
        device ?: return
        val filePath = resolveFilePath(uri) ?: return

        viewModelScope.launch {
            val session = try {
                transferService.sendFile(filePath, device)
            } catch (e: Exception) {
                android.util.Log.e("AirBridge/Transfer", "sendFile failed: ${e.message}", e)
                _connectionErrorMessage.value = "Send failed: ${e.message}"
                return@launch
            }
            val uiState = TransferSessionUiState(
                sessionId = session.sessionId,
                fileName = session.fileName,
                totalBytes = session.totalBytes,
                transferredBytes = 0L,
                state = TransferState.PENDING,
                sourceUri = uri,
            )
            addOrUpdateSession(uiState)
            _pendingRetryUris[session.sessionId] = uri

            // Track speed and ETA using timestamps between progress updates.
            var lastProgressBytes = 0L
            var lastProgressTimeMs = System.currentTimeMillis()

            launch {
                session.progressFlow.collect { transferred ->
                    val nowMs = System.currentTimeMillis()
                    val elapsedMs = nowMs - lastProgressTimeMs
                    val bytesDelta = transferred - lastProgressBytes

                    val speed: Long?
                    val eta: Long?
                    if (elapsedMs > 0 && bytesDelta >= 0) {
                        speed = (bytesDelta * 1000L) / elapsedMs
                        val remaining = session.totalBytes - transferred
                        eta = if (speed > 0) remaining / speed else null
                    } else {
                        speed = null
                        eta = null
                    }

                    lastProgressBytes = transferred
                    lastProgressTimeMs = nowMs

                    updateSessionProgress(session.sessionId, transferred, speed, eta)

                    val percent = if (session.totalBytes > 0)
                        ((transferred * 100) / session.totalBytes).toInt()
                    else 0
                    notificationManager.showProgress(session.fileName, percent)
                }
            }

            session.stateFlow.collect { state ->
                val errMsg = if (state == TransferState.FAILED)
                    "Transfer of \u201c${session.fileName}\u201d failed. Tap Retry to try again."
                else null
                updateSessionState(session.sessionId, state, errMsg)
                when (state) {
                    TransferState.COMPLETED -> notificationManager.showComplete(session.fileName)
                    TransferState.FAILED, TransferState.CANCELLED -> notificationManager.cancel()
                    else -> Unit
                }
            }
        }
    }

    /**
     * Retries a previously failed transfer.
     * Looks up the original [Uri] stored during [sendFile] and re-submits it.
     * No-ops if the session is not in [TransferState.FAILED] state or the URI is unavailable.
     */
    fun retryTransfer(sessionId: String) {
        val session = _activeSessions.value.find { it.sessionId == sessionId } ?: return
        if (session.state != TransferState.FAILED) return
        val uri = session.sourceUri ?: _pendingRetryUris[sessionId] ?: return

        // Remove the failed session from the list before re-sending so it gets a fresh entry.
        _activeSessions.value = _activeSessions.value.filter { it.sessionId != sessionId }
        sendFile(uri)
    }

    private fun addOrUpdateSession(uiState: TransferSessionUiState) {
        val current = _activeSessions.value.toMutableList()
        val index = current.indexOfFirst { it.sessionId == uiState.sessionId }
        if (index >= 0) current[index] = uiState else current.add(0, uiState)
        _activeSessions.value = current
    }

    private fun updateSessionProgress(
        sessionId: String,
        transferred: Long,
        speed: Long?,
        eta: Long?,
    ) {
        val current = _activeSessions.value.toMutableList()
        val index = current.indexOfFirst { it.sessionId == sessionId }
        if (index >= 0) {
            current[index] = current[index].copy(
                transferredBytes = transferred,
                speedBytesPerSec = speed,
                etaSeconds = eta,
            )
            _activeSessions.value = current
        }
    }

    private fun updateSessionState(sessionId: String, state: TransferState, errorMessage: String?) {
        val current = _activeSessions.value.toMutableList()
        val index = current.indexOfFirst { it.sessionId == sessionId }
        if (index >= 0) {
            current[index] = current[index].copy(state = state, errorMessage = errorMessage)
            _activeSessions.value = current
        }
    }

    /** Resolves a content [Uri] to an absolute file-system path. */
    private fun resolveFilePath(uri: Uri): String? {
        if (uri.scheme == "file") return uri.path
        // For content:// URIs, copy to a cache file so TransferSession can read it.
        val fileName = queryDisplayName(uri) ?: "transfer_file"

        val cacheFile = java.io.File(context.cacheDir, fileName)
        return try {
            context.contentResolver.openInputStream(uri)?.use { input ->
                cacheFile.outputStream().use { output -> input.copyTo(output) }
            }
            cacheFile.absolutePath
        } catch (e: Exception) {
            null
        }
    }

    private fun queryDisplayName(uri: Uri): String? {
        return context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (nameIndex >= 0 && cursor.moveToFirst()) cursor.getString(nameIndex) else null
        }
    }
}
