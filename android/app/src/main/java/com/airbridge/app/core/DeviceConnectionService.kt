package com.airbridge.app.core

import android.content.Context
import android.os.PowerManager
import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import android.util.Log
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Represents an inbound pairing request received from a remote (typically Windows) device.
 *
 * @param device    Synthetic [DeviceInfo] built from the connection's remote address.
 * @param channel   The live [IMessageChannel] on which the PAIRING_REQUEST was received.
 */
data class InboundPairingRequest(
    val device: DeviceInfo,
    val channel: IMessageChannel,
)

/**
 * Routes inbound connections from [IConnectionManager.incomingConnections] and tracks the
 * currently active [IMessageChannel] for each connected device.
 *
 * Inbound channel routing is done in a **single flow collection** to avoid the
 * double-collection race that would occur if the first message were peeked with
 * [kotlinx.coroutines.flow.first] and then the flow were collected again in a separate
 * coroutine.  Both the routing decision (pairing vs. reconnect) and all subsequent
 * message dispatching happen inside one [collect] call per channel.
 *
 * A [PowerManager.PARTIAL_WAKE_LOCK] is held for the lifetime of each active session so
 * that Android does not throttle [Dispatchers.IO] coroutines while the device is idle,
 * which would prevent timely PONG responses to Windows keepalive PINGs.
 */
@Singleton
class DeviceConnectionService @Inject constructor(
    @ApplicationContext private val context: Context,
    private val connectionManager: IConnectionManager,
    private val pairingService: IPairingService,
) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val TAG = "AirBridge/ConnSvc"

    // ── Active session registry ───────────────────────────────────────────

    private val _sessions = ConcurrentHashMap<String, IMessageChannel>()

    /** Returns the active [IMessageChannel] for [deviceId], or null if none is open. */
    fun getActiveSession(deviceId: String): IMessageChannel? = _sessions[deviceId]

    /**
     * Reactive set of device IDs that currently have an open session.
     * Updates whenever a session is opened or closed.
     */
    private val _connectedDeviceIds = MutableStateFlow<Set<String>>(emptySet())
    val connectedDeviceIds: StateFlow<Set<String>> = _connectedDeviceIds.asStateFlow()

    // ── WakeLock ──────────────────────────────────────────────────────────

    private val wakeLock: PowerManager.WakeLock by lazy {
        val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
        pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AirBridge:Connection")
    }

    // ── Public flows ──────────────────────────────────────────────────────

    /** Emits whenever a remote device initiates a pairing handshake with this device. */
    private val _incomingPairingRequests = MutableSharedFlow<InboundPairingRequest>()
    val incomingPairingRequests: SharedFlow<InboundPairingRequest> =
        _incomingPairingRequests.asSharedFlow()

    /** Emits channels for already-paired connections (non-pairing first messages). */
    private val _establishedChannels = MutableSharedFlow<IMessageChannel>()
    val establishedChannels: SharedFlow<IMessageChannel> = _establishedChannels.asSharedFlow()

    /** Tracks the result of the most-recent inbound pairing attempt for the UI. */
    private val _lastInboundPairingResult = MutableStateFlow<PairingResult?>(null)
    val lastInboundPairingResult: StateFlow<PairingResult?> = _lastInboundPairingResult.asStateFlow()

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /**
     * Starts listening for inbound TLS connections and routing them.
     * Idempotent — safe to call multiple times.
     */
    fun start() {
        scope.launch {
            connectionManager.startListening()
            connectionManager.incomingConnections.collect { channel ->
                scope.launch { routeChannel(channel) }
            }
        }
    }

    /**
     * Connects to an already-paired [device] without running the pairing handshake,
     * then registers the resulting channel as the active session.
     *
     * The TCP connect attempt is bounded by a 15-second timeout.
     * On disconnect, retries with exponential back-off (base 2 s, max 30 s, up to 5 attempts).
     * The listen side (server) never calls this — only the initiating (client) side reconnects.
     */
    suspend fun connectToPairedDevice(device: DeviceInfo) {
        val connectTimeoutMs = 15_000L
        val maxAttempts      = 5
        val backoffBase      = 2.0
        val backoffMaxMs     = 30_000L

        var attempt = 1
        while (attempt <= maxAttempts) {
            val channel = try {
                withTimeout(connectTimeoutMs) {
                    connectionManager.connect(device)
                }
            } catch (e: TimeoutCancellationException) {
                if (attempt >= maxAttempts) throw e
                val delayMs = minOf(
                    (Math.pow(backoffBase, (attempt - 1).toDouble()) * 1000).toLong(),
                    backoffMaxMs
                )
                delay(delayMs)
                attempt++
                continue
            } catch (e: Exception) {
                if (attempt >= maxAttempts) throw e
                val delayMs = minOf(
                    (Math.pow(backoffBase, (attempt - 1).toDouble()) * 1000).toLong(),
                    backoffMaxMs
                )
                delay(delayMs)
                attempt++
                continue
            }

            // Connected — register session and wait for disconnect before retrying.
            registerSession(device.deviceId, channel)
            _establishedChannels.emit(channel)

            // Block until this session ends, then loop back to reconnect.
            channel.incomingMessages.collect { /* drain until flow completes */ }

            // Reset attempt counter on a successful connection that later dropped.
            attempt = 1
        }
    }

    // ── Message dispatcher ────────────────────────────────────────────────

    private val _messageHandlers =
        ConcurrentHashMap<String, CopyOnWriteArrayList<suspend (ProtocolMessage) -> Unit>>()

    /**
     * Registers a handler invoked for every incoming message on [deviceId]'s session.
     * Multiple handlers may be registered; each message is delivered to all in order.
     * Handlers are removed automatically when the session closes.
     */
    fun addMessageHandler(deviceId: String, handler: suspend (ProtocolMessage) -> Unit) {
        _messageHandlers.getOrPut(deviceId) { CopyOnWriteArrayList() }.add(handler)
    }

    /** Removes all message handlers for [deviceId]. */
    fun removeMessageHandlers(deviceId: String) {
        _messageHandlers.remove(deviceId)
    }

    /**
     * Registers [channel] as the active session for [deviceId] and starts a coroutine that
     * collects [IMessageChannel.incomingMessages] and fans each message out to registered handlers.
     *
     * Use this for **outbound** connections (i.e. from [connectToPairedDevice]).  Inbound
     * connections are handled by [routeChannel], which reuses the same collection loop.
     */
    fun registerSession(deviceId: String, channel: IMessageChannel) {
        startSession(deviceId, channel)
        scope.launch {
            runMessageLoop(deviceId, channel, firstMessage = null)
        }
    }

    // ── Private routing + session management ─────────────────────────────

    /**
     * Records the session in the registry and acquires the connection WakeLock.
     * Must be matched by [endSession] in a finally block.
     */
    private fun startSession(deviceId: String, channel: IMessageChannel) {
        _sessions[deviceId] = channel
        _connectedDeviceIds.value = _connectedDeviceIds.value + deviceId
        if (!wakeLock.isHeld) wakeLock.acquire()
        Log.i(TAG, "Session registered: $deviceId")
    }

    /** Removes the session from the registry and releases the WakeLock if no sessions remain. */
    private fun endSession(deviceId: String, channel: IMessageChannel) {
        _sessions.remove(deviceId, channel)
        _messageHandlers.remove(deviceId)
        _connectedDeviceIds.value = _connectedDeviceIds.value - deviceId
        if (_sessions.isEmpty() && wakeLock.isHeld) wakeLock.release()
        Log.i(TAG, "Session closed: $deviceId")
    }

    /**
     * Routes an inbound [channel] in a **single** flow collection.
     *
     * The first message determines the path:
     * - [MessageType.PAIRING_REQUEST] → drive the pairing handshake; on success continue
     *   dispatching subsequent messages as a normal session.
     * - Anything else → already-paired reconnect; dispatch this and all subsequent messages.
     *
     * Using a single collection eliminates the double-collection race where a prior
     * [kotlinx.coroutines.flow.first] call could cancel the upstream producer and leave bytes
     * stranded in the [kotlinx.coroutines.flow.flowOn] internal channel buffer.
     */
    private suspend fun routeChannel(channel: IMessageChannel) {
        var sessionStarted = false
        var firstSeen = false
        try {
            channel.incomingMessages.collect { message ->
                if (!firstSeen) {
                    // ── First message: routing decision ───────────────────
                    firstSeen = true
                    when (message.type) {
                        MessageType.PAIRING_REQUEST -> {
                            val device = DeviceInfo(
                                deviceId   = channel.remoteDeviceId,
                                deviceName = channel.remoteDeviceId,
                                deviceType = DeviceType.UNKNOWN,
                                ipAddress  = channel.remoteDeviceId,
                                port       = 0,
                                isPaired   = false,
                            )
                            _incomingPairingRequests.emit(InboundPairingRequest(device, channel))

                            val result = pairingService.acceptPairingOnChannel(device, channel, message)
                            _lastInboundPairingResult.value = result

                            if (result == PairingResult.SUCCESS || result == PairingResult.ALREADY_PAIRED) {
                                startSession(channel.remoteDeviceId, channel)
                                sessionStarted = true
                            } else {
                                channel.close()
                            }
                        }

                        else -> {
                            // Already-paired device reconnecting.
                            startSession(channel.remoteDeviceId, channel)
                            sessionStarted = true
                            _establishedChannels.emit(channel)
                            dispatchMessage(channel.remoteDeviceId, message)
                        }
                    }
                } else {
                    // ── Subsequent messages: normal dispatch ──────────────
                    dispatchMessage(channel.remoteDeviceId, message)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "routeChannel error for ${channel.remoteDeviceId}: ${e.javaClass.simpleName}: ${e.message}", e)
        } finally {
            if (sessionStarted) endSession(channel.remoteDeviceId, channel)
        }
    }

    /**
     * Collects [IMessageChannel.incomingMessages] and dispatches each message to all registered
     * handlers for [deviceId].  Used by [registerSession] for outbound connections.
     *
     * @param firstMessage An already-read message to dispatch before starting the collect loop
     *                     (used when the caller has consumed a message outside this loop).
     */
    private suspend fun runMessageLoop(
        deviceId: String,
        channel: IMessageChannel,
        firstMessage: ProtocolMessage?,
    ) {
        try {
            if (firstMessage != null) dispatchMessage(deviceId, firstMessage)
            channel.incomingMessages.collect { message ->
                dispatchMessage(deviceId, message)
            }
        } catch (e: Exception) {
            Log.e(TAG, "[$deviceId] Transport error: ${e.javaClass.simpleName}: ${e.message}", e)
        } finally {
            endSession(deviceId, channel)
        }
    }

    private suspend fun dispatchMessage(deviceId: String, message: ProtocolMessage) {
        Log.d(TAG, "[$deviceId] RX type=${message.type} len=${message.payload.size}")
        val handlers = _messageHandlers[deviceId] ?: return
        for (handler in handlers) {
            try {
                handler(message)
            } catch (e: Exception) {
                Log.e(TAG, "[$deviceId] Handler error for type=${message.type}: ${e.message}", e)
            }
        }
    }
}
