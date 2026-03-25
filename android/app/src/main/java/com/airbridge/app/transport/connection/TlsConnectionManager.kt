package com.airbridge.app.transport.connection

import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IMessageChannel
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
import java.net.InetAddress
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicBoolean
import javax.inject.Inject
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
 * All socket I/O is dispatched to [Dispatchers.IO].
 */
class TlsConnectionManager @Inject constructor() : IConnectionManager {

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

            TlsMessageChannel(socket = socket, remoteDeviceId = remoteDevice.deviceId)
        }

    /**
     * Starts a TLS server socket on [ProtocolMessage.DEFAULT_PORT] and launches an accept loop.
     * Each accepted connection is wrapped in a [TlsMessageChannel] and emitted on [incomingConnections].
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
                        val remoteId = client.inetAddress.hostAddress ?: "unknown"
                        val channel = TlsMessageChannel(socket = client, remoteDeviceId = remoteId)
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
}
