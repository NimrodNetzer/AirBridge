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
 */
data class TransferSessionUiState(
    val sessionId: String,
    val fileName: String,
    val totalBytes: Long,
    val transferredBytes: Long,
    val state: TransferState,
)

/**
 * ViewModel for the Transfer screen.
 *
 * Bridges the file-picker [Uri] to the [IFileTransferService] and maintains
 * the [activeSessions] list for UI observation.
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

    /**
     * The device ID of the currently connected device, or null when no session is open.
     * Derived from [DeviceConnectionService.connectedDeviceIds] and updated reactively.
     */
    private val _connectedDeviceId = MutableStateFlow<String?>(null)
    val connectedDeviceId: StateFlow<String?> = _connectedDeviceId.asStateFlow()

    init {
        // Observe connected device IDs from the session manager and surface the first one.
        viewModelScope.launch {
            deviceConnectionService.connectedDeviceIds.collect { ids ->
                _connectedDeviceId.value = ids.firstOrNull()
            }
        }
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
            val session = transferService.sendFile(filePath, device)
            // Observe progress and state
            val uiState = TransferSessionUiState(
                sessionId = session.sessionId,
                fileName = session.fileName,
                totalBytes = session.totalBytes,
                transferredBytes = 0L,
                state = TransferState.PENDING,
            )
            addOrUpdateSession(uiState)

            launch {
                session.progressFlow.collect { transferred ->
                    updateSessionProgress(session.sessionId, transferred)
                    val percent = if (session.totalBytes > 0)
                        ((transferred * 100) / session.totalBytes).toInt()
                    else 0
                    notificationManager.showProgress(session.fileName, percent)
                }
            }

            session.stateFlow.collect { state ->
                updateSessionState(session.sessionId, state)
                when (state) {
                    TransferState.COMPLETED -> notificationManager.showComplete(session.fileName)
                    TransferState.FAILED, TransferState.CANCELLED -> notificationManager.cancel()
                    else -> Unit
                }
            }
        }
    }

    private fun addOrUpdateSession(uiState: TransferSessionUiState) {
        val current = _activeSessions.value.toMutableList()
        val index = current.indexOfFirst { it.sessionId == uiState.sessionId }
        if (index >= 0) current[index] = uiState else current.add(0, uiState)
        _activeSessions.value = current
    }

    private fun updateSessionProgress(sessionId: String, transferred: Long) {
        val current = _activeSessions.value.toMutableList()
        val index = current.indexOfFirst { it.sessionId == sessionId }
        if (index >= 0) {
            current[index] = current[index].copy(transferredBytes = transferred)
            _activeSessions.value = current
        }
    }

    private fun updateSessionState(sessionId: String, state: TransferState) {
        val current = _activeSessions.value.toMutableList()
        val index = current.indexOfFirst { it.sessionId == sessionId }
        if (index >= 0) {
            current[index] = current[index].copy(state = state)
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
