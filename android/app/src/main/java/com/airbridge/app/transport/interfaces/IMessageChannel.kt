package com.airbridge.app.transport.interfaces

import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.flow.Flow

/**
 * Framed message channel over a TLS TCP connection.
 * Send is safe to call from multiple coroutines concurrently.
 */
interface IMessageChannel {
    val remoteDeviceId: String
    val isConnected: Boolean

    /** Incoming message stream. Completes when the channel closes cleanly. */
    val incomingMessages: Flow<ProtocolMessage>

    suspend fun send(message: ProtocolMessage)
    suspend fun close()
}
