package com.airbridge.app.transfer

import com.airbridge.app.core.interfaces.ITransferSession
import com.airbridge.app.core.interfaces.TransferState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Holds and processes [ITransferSession] objects, running up to [concurrency]
 * sessions in parallel.
 *
 * Sessions are dispatched in enqueue order within each concurrency slot.
 * [pauseAll] pauses every active session.
 * [cancelAll] cancels every session that has not yet reached a terminal state.
 *
 * @param concurrency Maximum number of sessions that may run simultaneously. Defaults to 1.
 * @param scope       Optional [CoroutineScope] to run sessions in. Defaults to a private
 *                    IO-dispatcher scope that is cancelled when [close] is called.
 */
class TransferQueue(
    val concurrency: Int = 1,
    scope: CoroutineScope? = null,
) : AutoCloseable {

    init {
        require(concurrency >= 1) { "concurrency must be >= 1, was $concurrency" }
    }

    private val _scope = scope ?: CoroutineScope(Dispatchers.IO + SupervisorJob())

    /** Bounded channel used as the work queue (one slot per pending item). */
    private val _channel = Channel<ITransferSession>(capacity = Channel.UNLIMITED)

    private val _mutex    = Mutex()
    private val _sessions = mutableListOf<ITransferSession>()

    /** Snapshot of all sessions that have been enqueued (regardless of state). */
    val allSessions: List<ITransferSession> get() = _sessions.toList()

    init {
        // Launch [concurrency] worker coroutines that drain the channel.
        repeat(concurrency) {
            _scope.launch {
                for (session in _channel) {
                    runSession(session)
                }
            }
        }
    }

    /**
     * Adds [session] to the queue.  Returns the [Job] for the enqueue operation
     * (not the session itself — state tracking is via [ITransferSession.stateFlow]).
     */
    suspend fun enqueue(session: ITransferSession): Job {
        _mutex.withLock { _sessions.add(session) }
        _channel.send(session)
        return _scope.coroutineContext[Job]!!
    }

    /**
     * Pauses all sessions that are currently [TransferState.ACTIVE].
     */
    suspend fun pauseAll() {
        val snapshot = _mutex.withLock { _sessions.toList() }
        for (s in snapshot) {
            if (s.stateFlow.replayOrNull() == TransferState.ACTIVE) s.pause()
        }
    }

    /**
     * Cancels every session that has not yet reached a terminal state.
     */
    suspend fun cancelAll() {
        val snapshot = _mutex.withLock { _sessions.toList() }
        for (s in snapshot) {
            when (s.stateFlow.replayOrNull()) {
                TransferState.PENDING,
                TransferState.ACTIVE,
                TransferState.PAUSED -> s.cancel()
                else -> Unit
            }
        }
    }

    // ── AutoCloseable ──────────────────────────────────────────────────────

    override fun close() {
        _channel.close()
        _scope.cancel()
    }

    // ── Private ────────────────────────────────────────────────────────────

    private suspend fun runSession(session: ITransferSession) {
        try {
            session.start()
        } catch (_: Exception) {
            // Session state is already set to FAILED or CANCELLED.
            // Errors are observable via stateFlow — do not propagate here so that
            // the worker keeps processing subsequent sessions.
        }
    }
}

// ── Extension helpers ──────────────────────────────────────────────────────────

/**
 * Returns the most-recently emitted value of this [kotlinx.coroutines.flow.StateFlow],
 * or null if this is not a StateFlow (graceful fallback for test mocks).
 */
private fun <T> kotlinx.coroutines.flow.Flow<T>.replayOrNull(): T? =
    (this as? kotlinx.coroutines.flow.StateFlow<T>)?.value
