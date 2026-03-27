package com.airbridge.app.transport.connection

import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * TLS 1.3 connection manager for AirBridge Android transport.
 *
 * ## Security note
 * The current [TrustManager] accepts all certificates (trust-all scaffold).
 * **Iteration 3 replaces this with TOFU (Trust On First Use) key pinning** — the certificate
 * fingerprint is stored on first connection and verified on all subsequent ones.
 *
 * ## Role
 * - **Client**: [connect] opens an outbound TLS socket to a [DeviceInfo] peer (Windows host).
 * - **Server**: [startListening] accepts inbound connections and emits them on [incomingConnections].
 *
 * Immediately after every TLS handshake, a HANDSHAKE message (type 0x01) is exchanged so each
 * side learns the peer's stable device ID.  A PING/PONG keepalive loop is then started on the
 * channel.
 *
 * @param localDeviceId   Stable UUID identifying this Android device.
 * @param localDeviceName Human-readable name of this device (e.g. [android.os.Build.MODEL]).
 */
class TlsConnectionManager(
    private val localDeviceId: String,
    private val localDeviceName: String,
) : IConnectionManager {

    private val _incomingConnections = MutableSharedFlow<IMessageChannel>()
    override val incomingConnections: Flow<IMessageChannel> = _incomingConnections.asSharedFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var serverSocket: SSLServerSocket? = null
    private var acceptJob: Job? = null
    private val listening = AtomicBoolean(false)

    // -------------------------------------------------------------------------
    // SSL context — trust-all (Iteration 3: replace with TOFU key pinning)
    // -------------------------------------------------------------------------

    /**
     * Trust-all [X509TrustManager].
     *
     * TODO (Iteration 3): Replace with a TOFU key-pinning trust manager that:
     *  1. On first connection, stores the peer's certificate fingerprint.
     *  2. On subsequent connections, rejects any certificate whose fingerprint differs.
     */
    private val trustAllManager = object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    private val sslContext: SSLContext = SSLContext.getInstance("TLSv1.3").apply {
        init(null, arrayOf<TrustManager>(trustAllManager), SecureRandom())
    }

    // -------------------------------------------------------------------------
    // IConnectionManager
    // -------------------------------------------------------------------------

    /**
     * Opens an outbound TLS 1.3 connection to [remoteDevice] and returns a [TlsMessageChannel].
     * Performs the HANDSHAKE exchange and starts keepalive before returning.
     *
     * @param remoteDevice The target peer (must have a valid [DeviceInfo.ipAddress] and [DeviceInfo.port]).
     * @return A ready-to-use [IMessageChannel] wrapping the established TLS socket.
     */
    override suspend fun connect(remoteDevice: DeviceInfo): IMessageChannel =
        withContext(Dispatchers.IO) {
            val socket = sslContext.socketFactory
                .createSocket(remoteDevice.ipAddress, remoteDevice.port) as SSLSocket

            socket.apply {
                enabledProtocols = arrayOf("TLSv1.3")
                // Initiate TLS handshake explicitly before handing off to channel
                startHandshake()
            }

            // Exchange HANDSHAKE frames using the raw socket streams BEFORE wrapping in
            // TlsMessageChannel.  This ensures no bytes are buffered ahead by the channel's
            // internal BufferedInputStream before the HANDSHAKE is consumed.
            val peerId = exchangeHandshakeFrames(socket) ?: remoteDevice.deviceId

            val channel = TlsMessageChannel(socket = socket, remoteDeviceId = peerId)
            channel.startKeepalive(scope)
            channel
        }

    /**
     * Starts a TLS server socket on [ProtocolMessage.DEFAULT_PORT] and launches an accept loop.
     * Each accepted connection has the HANDSHAKE exchanged, is wrapped in a [TlsMessageChannel],
     * and is emitted on [incomingConnections].
     *
     * Idempotent — calling while already listening is a no-op.
     */
    override suspend fun startListening(): Unit = withContext(Dispatchers.IO) {
        if (listening.compareAndSet(false, true)) {
            val ss = sslContext.serverSocketFactory
                .createServerSocket(ProtocolMessage.DEFAULT_PORT) as SSLServerSocket

            ss.apply {
                enabledProtocols = arrayOf("TLSv1.3")
            }
            serverSocket = ss

            acceptJob = scope.launch {
                while (isActive && !ss.isClosed) {
                    try {
                        val client = ss.accept() as SSLSocket
                        client.startHandshake()

                        // Exchange HANDSHAKE frames before wrapping in TlsMessageChannel
                        val peerId = exchangeHandshakeFrames(client)
                            ?: (client.inetAddress.hostAddress ?: "unknown")

                        val channel = TlsMessageChannel(socket = client, remoteDeviceId = peerId)
                        channel.startKeepalive(scope)
                        _incomingConnections.emit(channel)
                    } catch (e: Exception) {
                        // Accept loop continues unless the server socket itself is closed
                        if (ss.isClosed) break
                    }
                }
            }
        }
    }

    /**
     * Stops the accept loop and closes the server socket.
     * Any channels previously emitted remain open until their own [IMessageChannel.close] is called.
     */
    override suspend fun stop(): Unit = withContext(Dispatchers.IO) {
        acceptJob?.cancel()
        acceptJob = null
        runCatching { serverSocket?.close() }
        serverSocket = null
        listening.set(false)
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /**
     * Sends this device's HANDSHAKE frame to [socket] and reads the peer's HANDSHAKE frame.
     *
     * This must be called on the raw [SSLSocket] **before** constructing a [TlsMessageChannel]
     * around it, so that no bytes are consumed into a BufferedInputStream ahead of this read.
     *
     * Both sides write first, then read, to avoid a deadlock where each side blocks
     * waiting for the other to write first.  This is safe because the TLS record layer
     * buffers outgoing data; the kernel send-buffer on a loopback / LAN socket is large
     * enough to hold a HANDSHAKE frame (~200 bytes) without blocking.
     *
     * @return The peer's stable device ID, or null if the HANDSHAKE could not be parsed.
     */
    private fun exchangeHandshakeFrames(socket: SSLSocket): String? {
        val json = JSONObject().apply {
            put("deviceId",   localDeviceId)
            put("deviceName", localDeviceName)
            put("deviceType", "android")
        }.toString()
        val payload = json.toByteArray(StandardCharsets.UTF_8)

        // Use the raw socket streams directly (not buffered) so no bytes are over-read.
        val dataOut = DataOutputStream(socket.outputStream)
        val dataIn  = DataInputStream(socket.inputStream)

        // Write our HANDSHAKE frame: [4-byte payload length][1-byte type][payload]
        dataOut.writeInt(payload.size)
        dataOut.writeByte(MessageType.HANDSHAKE.value.toInt())
        dataOut.write(payload)
        dataOut.flush()

        // Read the peer's HANDSHAKE frame
        val peerPayloadSize = try { dataIn.readInt() } catch (_: EOFException) { return null }
        if (peerPayloadSize < 0 || peerPayloadSize > ProtocolMessage.MAX_PAYLOAD_BYTES) return null

        val peerTypeByte = try { dataIn.readByte() } catch (_: EOFException) { return null }
        val peerType = try {
            MessageType.fromByte(peerTypeByte)
        } catch (_: IllegalArgumentException) {
            return null
        }

        val peerPayload = if (peerPayloadSize > 0) {
            ByteArray(peerPayloadSize).also { dataIn.readFully(it) }
        } else {
            ByteArray(0)
        }

        if (peerType != MessageType.HANDSHAKE || peerPayload.isEmpty()) return null

        return runCatching {
            val peerJson = JSONObject(String(peerPayload, StandardCharsets.UTF_8))
            peerJson.optString("deviceId", "").takeIf { it.isNotEmpty() }
        }.getOrNull()
    }
}
