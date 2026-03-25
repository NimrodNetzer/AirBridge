package com.airbridge.app.core.interfaces

import kotlinx.coroutines.flow.Flow

enum class TransferState { PENDING, ACTIVE, PAUSED, COMPLETED, FAILED, CANCELLED }

/**
 * Represents a single file transfer session (send or receive).
 * [progressFlow] emits bytes-transferred updates for UI binding.
 */
interface ITransferSession {
    val sessionId: String
    val fileName: String
    val totalBytes: Long
    val isSender: Boolean
    val stateFlow: Flow<TransferState>
    val progressFlow: Flow<Long>  // bytes transferred so far

    suspend fun start()
    suspend fun pause()
    suspend fun resume()
    suspend fun cancel()
}
