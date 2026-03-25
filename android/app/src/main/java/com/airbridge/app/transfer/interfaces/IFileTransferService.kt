package com.airbridge.app.transfer.interfaces

import com.airbridge.app.core.interfaces.ITransferSession
import com.airbridge.app.core.models.DeviceInfo

/**
 * High-level file transfer service.
 * Implemented in Iteration 4.
 */
interface IFileTransferService {
    suspend fun sendFile(filePath: String, destination: DeviceInfo): ITransferSession
    suspend fun receiveFile(sessionId: String, destinationDirectory: String): ITransferSession
    fun getActiveSessions(): List<ITransferSession>
}
