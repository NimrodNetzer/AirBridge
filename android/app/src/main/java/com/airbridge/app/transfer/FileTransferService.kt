package com.airbridge.app.transfer

import android.content.Context
import android.os.Environment
import com.airbridge.app.core.DeviceConnectionService
import com.airbridge.app.core.interfaces.ITransferSession
import com.airbridge.app.core.interfaces.TransferState
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transfer.interfaces.IFileTransferService
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import android.util.Log
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Production implementation of [IFileTransferService].
 *
 * Sends files to a paired [DeviceInfo] over the active [IMessageChannel] held by
 * [DeviceConnectionService]. Registers a message handler on [DeviceConnectionService]
 * to receive inbound file transfer messages and save them to the AirBridge Downloads folder.
 *
 * Wire protocol (payload of each protocol message):
 * - [MessageType.FILE_TRANSFER_START]  → [FileStartMessage.toBytes()]
 * - [MessageType.FILE_CHUNK]           → [FileChunkMessage.toBytes()]
 * - [MessageType.FILE_TRANSFER_END]    → [FileEndMessage.toBytes()]
 */
@Singleton
class FileTransferService @Inject constructor(
    @ApplicationContext private val context: Context,
    private val deviceConnectionService: DeviceConnectionService,
) : IFileTransferService {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val TAG = "AirBridge/Transfer"

    private val _activeSessions = CopyOnWriteArrayList<ITransferSession>()

    /** Destination directory for received files. */
    private val receiveDir: File by lazy {
        val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        File(downloads, "AirBridge").also { it.mkdirs() }
    }

    /**
     * Sends [filePath] to [destination] using the active session channel.
     *
     * Protocol sequence (sender side):
     * 1. Send FILE_TRANSFER_START with [FileStartMessage] payload.
     * 2. Read file in 64 KB chunks; send each as FILE_CHUNK with [FileChunkMessage] payload.
     * 3. Send FILE_TRANSFER_END with [FileEndMessage] payload (includes SHA-256).
     *
     * Returns an [ITransferSession] whose [stateFlow] and [progressFlow] can be observed
     * for real-time progress updates.
     */
    override suspend fun sendFile(filePath: String, destination: DeviceInfo): ITransferSession {
        val channel = deviceConnectionService.getActiveSession(destination.deviceId)
            ?: error("No active session for device ${destination.deviceId}. " +
                     "Ensure the device is connected before calling sendFile.")

        val file = File(filePath)
        require(file.exists()) { "File not found: $filePath" }

        val sessionId = UUID.randomUUID().toString()

        // Use a mutable session so the coroutine can push state/progress updates out via flows.
        val session = ReceiveProgressSession(
            sessionId  = sessionId,
            fileName   = file.name,
            totalBytes = file.length(),
            isSender   = true,
        )
        _activeSessions.add(session)

        scope.launch {
            try {
                Log.i(TAG, "sendFile START: ${file.name} (${file.length()} bytes) → ${destination.deviceId}")
                session.updateState(TransferState.ACTIVE)

                // Send FILE_TRANSFER_START
                val startPayload = FileStartMessage(sessionId, file.name, file.length()).toBytes()
                channel.send(ProtocolMessage(MessageType.FILE_TRANSFER_START, startPayload))

                // Stream chunks directly to avoid buffering the whole file in memory.
                val digest = java.security.MessageDigest.getInstance("SHA-256")
                val buf = ByteArray(TransferSession.CHUNK_SIZE)
                var offset = 0L
                file.inputStream().use { src ->
                    var bytesRead: Int
                    while (src.read(buf).also { bytesRead = it } != -1) {
                        val chunk = buf.copyOf(bytesRead)
                        digest.update(chunk)
                        val chunkPayload = FileChunkMessage(offset, chunk).toBytes()
                        channel.send(ProtocolMessage(MessageType.FILE_CHUNK, chunkPayload))
                        offset += bytesRead
                        session.updateProgress(offset)
                    }
                }

                // Send FILE_TRANSFER_END
                val hash = digest.digest()
                val endPayload = FileEndMessage(offset, hash).toBytes()
                channel.send(ProtocolMessage(MessageType.FILE_TRANSFER_END, endPayload))

                Log.i(TAG, "sendFile DONE: ${file.name} ($offset bytes sent)")
                session.updateState(TransferState.COMPLETED)
            } catch (e: Exception) {
                Log.e(TAG, "sendFile FAILED: ${file.name} — ${e.javaClass.simpleName}: ${e.message}", e)
                session.updateState(TransferState.FAILED)
                // Best-effort error message to remote peer.
                try {
                    val errPayload = TransferErrorMessage(e.message ?: "transfer error").toBytes()
                    channel.send(ProtocolMessage(MessageType.ERROR, errPayload))
                } catch (_: Exception) { /* ignore send failure on error path */ }
            }
        }

        return session
    }

    /**
     * Prepares a receive session for an inbound transfer identified by [sessionId].
     * In normal operation the receive loop is driven by [registerReceiveHandler]; this
     * method is provided for callers that want to register a receive session manually.
     */
    override suspend fun receiveFile(
        sessionId: String,
        destinationDirectory: String,
    ): ITransferSession {
        val session = TransferSession(
            sessionId = sessionId,
            fileName = "incoming",
            totalBytes = 0L,
            isSender = false,
            dataStream = ByteArrayOutputStream(),
            networkStream = ByteArrayInputStream(ByteArray(0)),
        )
        _activeSessions.add(session)
        return session
    }

    override fun getActiveSessions(): List<ITransferSession> = _activeSessions.toList()

    // ── Inbound handler ───────────────────────────────────────────────────

    /**
     * Registers a message handler on [DeviceConnectionService] for [deviceId] that
     * handles incoming FILE_TRANSFER_START / FILE_CHUNK / FILE_TRANSFER_END messages
     * and saves received files to [receiveDir].
     *
     * Should be called once a session is established (e.g. from
     * [DeviceConnectionService.connectToPairedDevice] or after pairing completes).
     */
    override fun registerReceiveHandler(deviceId: String) {
        // State machine for the current in-progress receive.
        var currentSessionId: String? = null
        var currentFileName: String? = null
        var currentTotalBytes: Long = 0L
        var currentOutFile: File? = null
        var currentOutputStream: java.io.OutputStream? = null
        var currentDigest: java.security.MessageDigest? = null
        var currentReceiveSession: ReceiveProgressSession? = null

        deviceConnectionService.addMessageHandler(deviceId) { message ->
            when (message.type) {
                MessageType.FILE_TRANSFER_START -> {
                    val start = FileStartMessage.fromBytes(message.payload)
                    Log.i(TAG, "receiveFile START: ${start.fileName} (${start.totalBytes} bytes)")
                    currentSessionId  = start.sessionId
                    currentFileName   = start.fileName
                    currentTotalBytes = start.totalBytes

                    val outFile = File(receiveDir, start.fileName)
                    currentOutFile     = outFile
                    currentOutputStream = outFile.outputStream().buffered()
                    currentDigest      = java.security.MessageDigest.getInstance("SHA-256")

                    val receiveSession = ReceiveProgressSession(
                        sessionId  = start.sessionId,
                        fileName   = start.fileName,
                        totalBytes = start.totalBytes,
                    )
                    currentReceiveSession = receiveSession
                    _activeSessions.add(receiveSession)
                    receiveSession.updateState(TransferState.ACTIVE)
                }

                MessageType.FILE_CHUNK -> {
                    val chunk = FileChunkMessage.fromBytes(message.payload)
                    currentOutputStream?.let { out ->
                        currentDigest?.update(chunk.data)
                        out.write(chunk.data)
                        currentReceiveSession?.updateProgress(chunk.offset + chunk.data.size)
                    }
                }

                MessageType.FILE_TRANSFER_END -> {
                    val end = FileEndMessage.fromBytes(message.payload)
                    currentOutputStream?.let { out ->
                        out.flush()
                        out.close()
                    }
                    val actualHash = currentDigest?.digest() ?: ByteArray(0)
                    if (actualHash.contentEquals(end.sha256Hash)) {
                        Log.i(TAG, "receiveFile DONE: $currentFileName — hash OK")
                        currentReceiveSession?.updateState(TransferState.COMPLETED)
                    } else {
                        Log.e(TAG, "receiveFile FAILED: $currentFileName — SHA-256 mismatch!")
                        currentOutFile?.delete()
                        currentReceiveSession?.updateState(TransferState.FAILED)
                    }
                    // Reset state machine
                    currentSessionId    = null
                    currentFileName     = null
                    currentTotalBytes   = 0L
                    currentOutFile      = null
                    currentOutputStream = null
                    currentDigest       = null
                    currentReceiveSession = null
                }

                MessageType.ERROR -> {
                    currentOutputStream?.close()
                    currentOutFile?.delete()
                    currentReceiveSession?.updateState(TransferState.FAILED)
                    currentSessionId    = null
                    currentReceiveSession = null
                }

                else -> { /* not a file transfer message; ignore */ }
            }
        }
    }
}

/**
 * Lightweight [ITransferSession] whose progress and state are updated externally.
 * Used for both send and receive sides by [FileTransferService].
 */
internal class ReceiveProgressSession(
    override val sessionId: String,
    override val fileName: String,
    override val totalBytes: Long,
    override val isSender: Boolean = false,
) : ITransferSession {

    private val _stateFlow    = MutableStateFlow(TransferState.PENDING)
    private val _progressFlow = MutableStateFlow(0L)

    override val stateFlow    = _stateFlow.asStateFlow()
    override val progressFlow = _progressFlow.asStateFlow()

    fun updateState(state: TransferState)    { _stateFlow.value    = state }
    fun updateProgress(transferred: Long)   { _progressFlow.value = transferred }

    override suspend fun start()  { /* driven externally */ }
    override suspend fun pause()  { _stateFlow.value = TransferState.PAUSED }
    override suspend fun resume() { _stateFlow.value = TransferState.ACTIVE }
    override suspend fun cancel() { _stateFlow.value = TransferState.CANCELLED }
}
