package com.airbridge.app.transport.connection

import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.SSLSocket

/**
 * [IMessageChannel] implementation over a TLS [SSLSocket].
 *
 * ## Wire format
 * Each frame on the wire is:
 * ```
 * ┌───────────────────────┬────────────┬───────────────────────┐
 * │  length  (4 bytes BE) │ type byte  │  payload (length - 1) │
 * └───────────────────────┴────────────┴───────────────────────┘
 * ```
 * `length` = 1 (type byte) + payload size.
 *
 * The maximum payload is [ProtocolMessage.MAX_PAYLOAD_BYTES].
 *
 * ## Thread safety
 * [send] is protected by a [Mutex]; multiple coroutines may call it concurrently.
 *
 * @param socket       The established TLS socket.
 * @param remoteDeviceId Stable identifier of the remote peer.
 */
class TlsMessageChannel(
    private val socket: Socket,
    override val remoteDeviceId: String
) : IMessageChannel {

    private val _connected = AtomicBoolean(true)
    override val isConnected: Boolean get() = _connected.get()

    private val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream()))
    private val input  = DataInputStream(BufferedInputStream(socket.getInputStream()))

    private val sendMutex = Mutex()

    // -------------------------------------------------------------------------
    // Incoming message stream
    // -------------------------------------------------------------------------

    /**
     * Cold [Flow] that reads framed messages until the connection closes cleanly.
     * Each subscription starts a new read loop — intended to be collected by a single consumer.
     * Completes normally on EOF; throws on protocol violations.
     */
    override val incomingMessages: Flow<ProtocolMessage> = flow {
        try {
            while (_connected.get()) {
                // Read 4-byte length prefix (big-endian)
                val frameLength = try {
                    input.readInt()
                } catch (e: EOFException) {
                    break  // clean close
                }

                if (frameLength < 1) {
                    throw IllegalStateException("Invalid frame length: $frameLength")
                }

                val payloadSize = frameLength - 1  // subtract the type byte
                if (payloadSize > ProtocolMessage.MAX_PAYLOAD_BYTES) {
                    throw IllegalStateException(
                        "Payload too large: $payloadSize > ${ProtocolMessage.MAX_PAYLOAD_BYTES}"
                    )
                }

                // Read 1-byte message type
                val typeByte = input.readByte()
                val messageType = MessageType.fromByte(typeByte)

                // Read payload
                val payload = if (payloadSize > 0) {
                    ByteArray(payloadSize).also { input.readFully(it) }
                } else {
                    ByteArray(0)
                }

                emit(ProtocolMessage(type = messageType, payload = payload))
            }
        } finally {
            _connected.set(false)
        }
    }.flowOn(Dispatchers.IO)

    // -------------------------------------------------------------------------
    // Send
    // -------------------------------------------------------------------------

    /**
     * Serializes and sends [message] over the TLS socket.
     *
     * Protected by a [Mutex] so concurrent callers are serialized safely.
     * Dispatches all I/O to [Dispatchers.IO].
     */
    override suspend fun send(message: ProtocolMessage): Unit = withContext(Dispatchers.IO) {
        if (!_connected.get()) throw IllegalStateException("Channel is closed")
        val payload = message.payload
        if (payload.size > ProtocolMessage.MAX_PAYLOAD_BYTES) {
            throw IllegalArgumentException(
                "Payload too large: ${payload.size} > ${ProtocolMessage.MAX_PAYLOAD_BYTES}"
            )
        }

        sendMutex.withLock {
            // frameLength = 1 (type byte) + payload size
            val frameLength = 1 + payload.size
            out.writeInt(frameLength)
            out.writeByte(message.type.value.toInt())
            if (payload.isNotEmpty()) out.write(payload)
            out.flush()
        }
    }

    // -------------------------------------------------------------------------
    // Close
    // -------------------------------------------------------------------------

    /** Closes the underlying socket, completing [incomingMessages]. */
    override suspend fun close(): Unit = withContext(Dispatchers.IO) {
        _connected.set(false)
        runCatching { socket.close() }
    }
}
