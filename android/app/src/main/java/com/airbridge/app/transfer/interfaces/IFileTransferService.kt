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

    /**
     * Registers an inbound file-transfer message handler on [DeviceConnectionService] for the
     * given [deviceId]. Must be called once a session is established so received files are saved.
     */
    fun registerReceiveHandler(deviceId: String)
}
