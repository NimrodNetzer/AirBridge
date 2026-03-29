package com.airbridge.app.transport.connection

import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import android.util.Log
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SSLSocket

/**
 * [IMessageChannel] implementation over a TLS [SSLSocket].
 *
 * ## Wire format
 * Each frame on the wire is:
 * ```
 * ┌───────────────────────┬────────────┬───────────────────────┐
 * │  length  (4 bytes BE) │ type byte  │  payload (length)     │
 * └───────────────────────┴────────────┴───────────────────────┘
 * ```
 * `length` = payload size only (type byte is NOT included).
 *
 * The maximum payload is [ProtocolMessage.MAX_PAYLOAD_BYTES].
 *
 * ## Thread safety
 * [send] is protected by a [Mutex]; multiple coroutines may call it concurrently.
 *
 * ## Keepalive
 * PING (0xF0) and PONG (0xF1) frames are handled internally and never emitted on
 * [incomingMessages].  Call [startKeepalive] after the HANDSHAKE exchange to enable
 * the 30-second keepalive loop.  If no PONG is received within 10 seconds of a PING,
 * the channel is closed automatically.
 *
 * @param socket         The established TLS socket.
 * @param remoteDeviceId Stable device ID of the remote peer, resolved from the HANDSHAKE exchange
 *                       by [TlsConnectionManager] before the channel is constructed.
 */
class TlsMessageChannel(
    private val socket: Socket,
    override val remoteDeviceId: String,
    /** Override keepalive interval — useful in tests to avoid 30-second waits. */
    private val keepaliveIntervalMs: Long = KEEPALIVE_INTERVAL_MS,
    /** Override PONG timeout — useful in tests. */
    private val pongTimeoutMs: Long = PONG_TIMEOUT_MS
) : IMessageChannel {

    private val _connected = AtomicBoolean(true)
    override val isConnected: Boolean get() = _connected.get()

    /** Job for the active keepalive loop — cancelled in [close] so it stops immediately. */
    private var keepaliveJob: Job? = null

    private val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream()))
    private val input  = DataInputStream(BufferedInputStream(socket.getInputStream()))

    private val sendMutex = Mutex()

    // Epoch-millisecond timestamp of the last PONG received from the peer.
    // Initialised to "now" so a freshly-created channel is not immediately considered dead.
    private val lastPongMs = AtomicLong(System.currentTimeMillis())

    companion object {
        private const val TAG                   = "AirBridge/Channel"
        private const val KEEPALIVE_INTERVAL_MS = 30_000L
        private const val PONG_TIMEOUT_MS       = 10_000L
    }

    // -------------------------------------------------------------------------
    // Incoming message stream
    // -------------------------------------------------------------------------

    /**
     * Cold [Flow] that reads framed messages until the connection closes cleanly.
     * Each subscription starts a new read loop — intended to be collected by a single consumer.
     * Completes normally on EOF; throws on protocol violations.
     *
     * PING and PONG messages are handled internally and never emitted to collectors.
     */
    override val incomingMessages: Flow<ProtocolMessage> = flow {
        var cancelledByCollector = false
        try {
            while (_connected.get()) {
                // Read 4-byte payload-size prefix (big-endian, does NOT include type byte)
                val payloadSize = try {
                    input.readInt()
                } catch (e: EOFException) {
                    Log.i(TAG, "[$remoteDeviceId] EOF — channel closed cleanly")
                    break  // clean close
                }

                if (payloadSize < 0) {
                    throw IllegalStateException("Invalid payload size: $payloadSize")
                }
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

                // ── Keepalive intercept ───────────────────────────────────────
                // PING/PONG handled internally; never emitted to collectors.
                when (messageType) {
                    MessageType.PING -> {
                        try { send(ProtocolMessage(MessageType.PONG, ByteArray(0))) } catch (_: Exception) {}
                    }
                    MessageType.PONG -> {
                        lastPongMs.set(System.currentTimeMillis())
                    }
                    else -> {
                        Log.d(TAG, "[$remoteDeviceId] RX type=${messageType} len=${payload.size}")
                        emit(ProtocolMessage(type = messageType, payload = payload))
                    }
                }
            }
        } catch (e: CancellationException) {
            // Normal flow cancellation (e.g. first{} operator used by pairing service).
            // Do NOT mark the channel as disconnected — the socket is still alive.
            cancelledByCollector = true
            throw e
        } catch (e: Exception) {
            Log.e(TAG, "[$remoteDeviceId] Read loop error: ${e.javaClass.simpleName}: ${e.message}", e)
            _connected.set(false)
            throw e
        } finally {
            // For clean EOF exits and real errors, mark disconnected.
            // For collector cancellation (first{} etc.), leave connected so the next
            // collect() call can start a new read loop on the same socket.
            if (!cancelledByCollector) _connected.set(false)
            Log.i(TAG, "[$remoteDeviceId] incomingMessages flow ended, connected=${_connected.get()}")
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
            // 4-byte header = payload size only (type byte is NOT included)
            out.writeInt(payload.size)
            out.writeByte(message.type.value.toInt())
            if (payload.isNotEmpty()) out.write(payload)
            out.flush()
        }
    }

    // -------------------------------------------------------------------------
    // Keepalive
    // -------------------------------------------------------------------------

    /**
     * Starts a background keepalive loop within [scope].
     * Sends a PING every 30 seconds; closes the channel if no PONG arrives within 10 seconds.
     * Should be called once, immediately after the HANDSHAKE exchange completes.
     */
    fun startKeepalive(scope: CoroutineScope) {
        keepaliveJob = scope.launch(Dispatchers.IO) {
            while (isActive && _connected.get()) {
                delay(keepaliveIntervalMs)
                if (!_connected.get()) break

                val pingTime = System.currentTimeMillis()
                val sendOk = try {
                    send(ProtocolMessage(MessageType.PING, ByteArray(0)))
                    Log.d(TAG, "[$remoteDeviceId] PING sent")
                    true
                } catch (e: Exception) {
                    Log.e(TAG, "[$remoteDeviceId] PING send failed: ${e.message}")
                    false
                }
                if (!sendOk) break

                // Wait for PONG (poll at 250ms intervals up to pongTimeoutMs)
                val deadline = pingTime + pongTimeoutMs
                while (System.currentTimeMillis() < deadline) {
                    if (lastPongMs.get() >= pingTime) break
                    delay(250)
                }

                if (lastPongMs.get() < pingTime) {
                    // No PONG received — treat as dead connection
                    Log.w(TAG, "[$remoteDeviceId] PONG timeout — closing channel")
                    close()
                    break
                } else {
                    Log.d(TAG, "[$remoteDeviceId] PONG received OK")
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Close
    // -------------------------------------------------------------------------

    /** Closes the underlying socket, completing [incomingMessages]. */
    override suspend fun close(): Unit = withContext(Dispatchers.IO) {
        _connected.set(false)
        keepaliveJob?.cancel()   // stop the keepalive loop immediately, don't wait for the next delay()
        keepaliveJob = null
        runCatching { socket.close() }
    }
}
