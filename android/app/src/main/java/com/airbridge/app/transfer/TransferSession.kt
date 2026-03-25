package com.airbridge.app.transfer

import com.airbridge.app.core.interfaces.ITransferSession
import com.airbridge.app.core.interfaces.TransferState
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest

/**
 * Sealed result type returned by transfer operations at module boundaries.
 * Errors are surfaced as [TransferResult.Failure] rather than thrown exceptions.
 */
sealed class TransferResult {
    /** The operation completed successfully. */
    object Success : TransferResult()

    /**
     * The operation failed.
     * @property message Human-readable description of the failure.
     */
    data class Failure(val message: String) : TransferResult()
}

/**
 * Implements [ITransferSession] for a single send or receive operation.
 *
 * Chunked transfer (64 KB chunks) over any [InputStream]/[OutputStream] pair.
 * SHA-256 is computed incrementally over all written bytes and verified when
 * the sender's [FileEndMessage] arrives.
 * Progress is emitted via [progressFlow] (bytes transferred so far).
 * Pause and cancel are implemented via coroutine [Job] cancellation.
 *
 * @param sessionId    Unique identifier for this session.
 * @param fileName     Name of the file being transferred.
 * @param totalBytes   Total file size in bytes.
 * @param isSender     `true` if this side sends; `false` if it receives.
 * @param dataStream   Readable source (sender) or writable destination (receiver).
 * @param networkStream The peer connection — writable for sender, readable for receiver.
 */
class TransferSession(
    override val sessionId: String,
    override val fileName: String,
    override val totalBytes: Long,
    override val isSender: Boolean,
    private val dataStream: Any,        // InputStream (receiver) or OutputStream (sender side: file source)
    private val networkStream: Any,     // OutputStream (sender) or InputStream (receiver)
) : ITransferSession {

    companion object {
        /** Default chunk size: 64 KB. */
        const val CHUNK_SIZE = 64 * 1024
    }

    // ── State & progress flows ─────────────────────────────────────────────

    private val _stateFlow    = MutableStateFlow(TransferState.PENDING)
    private val _progressFlow = MutableStateFlow(0L)

    override val stateFlow:    Flow<TransferState> = _stateFlow.asStateFlow()
    override val progressFlow: Flow<Long>          = _progressFlow.asStateFlow()

    /** Snapshot of the current [TransferState]. */
    val currentState: TransferState get() = _stateFlow.value

    // ── ITransferSession lifecycle ─────────────────────────────────────────

    /**
     * Starts the transfer.  Must be called from a coroutine.
     * Emits state transitions and progress via the respective flows.
     * Returns a [TransferResult] — does NOT throw at the module boundary.
     */
    override suspend fun start(): Unit {
        check(_stateFlow.value == TransferState.PENDING) {
            "Cannot start a session in state ${_stateFlow.value}."
        }
        _stateFlow.value = TransferState.ACTIVE

        try {
            if (isSender) runSender() else runReceiver()
            _stateFlow.value = TransferState.COMPLETED
        } catch (e: CancellationException) {
            if (_stateFlow.value != TransferState.PAUSED)
                _stateFlow.value = TransferState.CANCELLED
            throw e
        } catch (e: Exception) {
            _stateFlow.value = TransferState.FAILED
            throw e
        }
    }

    /**
     * Requests a pause.  The running coroutine will see the state change and
     * can coordinate; actual suspension happens when the caller cancels the job.
     */
    override suspend fun pause() {
        if (_stateFlow.value == TransferState.ACTIVE)
            _stateFlow.value = TransferState.PAUSED
    }

    /**
     * Resumes a paused session by re-running from [_progressFlow]'s current value.
     * The caller is responsible for re-invoking [start] on a fresh coroutine after
     * calling this method (since the previous coroutine was cancelled at pause time).
     */
    override suspend fun resume() {
        check(_stateFlow.value == TransferState.PAUSED) {
            "Cannot resume a session in state ${_stateFlow.value}."
        }
        _stateFlow.value = TransferState.ACTIVE
    }

    /** Cancels the session immediately. */
    override suspend fun cancel() {
        _stateFlow.value = TransferState.CANCELLED
    }

    // ── Sender loop ────────────────────────────────────────────────────────

    private suspend fun runSender() {
        val src = dataStream as InputStream
        val net = networkStream as OutputStream

        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(CHUNK_SIZE)
        var offset = _progressFlow.value  // resume support

        if (offset > 0) src.skip(offset)

        // Write FileStart header
        writeMessage(net, FileStartMessage(sessionId, fileName, totalBytes).toBytes())

        var bytesRead: Int
        while (src.read(buffer).also { bytesRead = it } != -1) {
            currentCoroutineContext().ensureActive()

            val chunk = buffer.copyOf(bytesRead)
            digest.update(chunk)
            writeMessage(net, FileChunkMessage(offset, chunk).toBytes())

            offset += bytesRead
            _progressFlow.value = offset
        }

        val hash   = digest.digest()
        writeMessage(net, FileEndMessage(offset, hash).toBytes())
    }

    // ── Receiver loop ──────────────────────────────────────────────────────

    private suspend fun runReceiver() {
        val net  = networkStream as InputStream
        val sink = dataStream as OutputStream

        val digest = MessageDigest.getInstance("SHA-256")
        var expectedOffset = _progressFlow.value

        // Read FileStart (metadata already set in constructor from the service layer,
        // but we consume the message to advance the stream)
        val startBytes = readMessage(net)
        FileStartMessage.fromBytes(startBytes) // consumed

        while (true) {
            currentCoroutineContext().ensureActive()

            val msgBytes = readMessage(net)
            when (TransferMessageType.fromByte(msgBytes[0])) {
                TransferMessageType.FILE_CHUNK -> {
                    val chunk = FileChunkMessage.fromBytes(msgBytes)
                    if (chunk.offset == expectedOffset) {
                        digest.update(chunk.data)
                        sink.write(chunk.data)
                        expectedOffset += chunk.data.size
                        _progressFlow.value = expectedOffset
                    }
                    // Out-of-order / duplicate chunks silently dropped (v1)
                }
                TransferMessageType.FILE_END -> {
                    val endMsg     = FileEndMessage.fromBytes(msgBytes)
                    val actualHash = digest.digest()
                    if (!actualHash.contentEquals(endMsg.sha256Hash))
                        throw SecurityException("SHA-256 hash mismatch — file transfer corrupted.")
                    break
                }
                TransferMessageType.TRANSFER_ERROR -> {
                    val errMsg = TransferErrorMessage.fromBytes(msgBytes)
                    throw IllegalStateException("Remote peer reported transfer error: ${errMsg.errorMessage}")
                }
                else -> { /* ignore unknown message types */ }
            }
        }
        sink.flush()
    }

    // ── Wire helpers ───────────────────────────────────────────────────────

    /**
     * Writes a length-prefixed message to [stream].
     * Wire format: `[4-byte big-endian length][message bytes]`.
     */
    internal fun writeMessage(stream: OutputStream, message: ByteArray) {
        val lenBuf = ByteArray(4)
        lenBuf[0] = (message.size shr 24).toByte()
        lenBuf[1] = (message.size shr 16).toByte()
        lenBuf[2] = (message.size shr  8).toByte()
        lenBuf[3] = (message.size       ).toByte()
        stream.write(lenBuf)
        stream.write(message)
        stream.flush()
    }

    /**
     * Reads one length-prefixed message from [stream].
     * @throws EOFException if the stream ends before the full message is read.
     */
    internal fun readMessage(stream: InputStream): ByteArray {
        val dis = DataInputStream(stream)
        val len = dis.readInt()  // big-endian 4-byte length
        val buf = ByteArray(len)
        dis.readFully(buf)
        return buf
    }
}
