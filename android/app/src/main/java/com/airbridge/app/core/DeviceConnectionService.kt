package com.airbridge.app.core

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.os.PowerManager
import com.airbridge.app.core.AirBridgeLog
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
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
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
 * Emitted by [DeviceConnectionService.mirrorStartRequests] when the Windows host sends a
 * [MessageType.MIRROR_START] message.  The receiver should start [com.airbridge.app.mirror.PhoneCaptureService]
 * after obtaining a MediaProjection token from the user.
 */
data class MirrorStartRequest(val deviceId: String)

/**
 * Represents the reconnect state for a device that is currently being re-connected to.
 *
 * @property deviceId  The device being reconnected.
 * @property attempt   The current attempt number (1-based).
 * @property maxAttempts Total number of attempts allowed.
 */
data class ReconnectState(
    val deviceId: String,
    val attempt: Int,
    val maxAttempts: Int,
)

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

    // ── WakeLock + WifiLock ───────────────────────────────────────────────
    // PARTIAL_WAKE_LOCK keeps the CPU running (prevents Dispatchers.IO starvation).
    // WIFI_MODE_FULL_HIGH_PERF keeps the Wi-Fi radio active so the OS cannot silently
    // drop the TCP socket while the screen is off or the device enters Doze.

    private val wakeLock: PowerManager.WakeLock by lazy {
        val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
        pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AirBridge:Connection")
    }

    private val wifiLock: WifiManager.WifiLock by lazy {
        val wm = context.getSystemService(Context.WIFI_SERVICE) as WifiManager
        wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "AirBridge:Connection")
    }

    // ── Network monitoring ────────────────────────────────────────────────
    // Watches for Wi-Fi availability changes. When the network comes back after
    // a Doze window or a brief drop, we cancel any backoff delay in active reconnect
    // loops so they retry immediately instead of waiting up to 60 s.

    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    // Backoff delay jobs keyed by device ID — cancelled when network becomes available.
    private val reconnectDelayJobs = ConcurrentHashMap<String, Job>()

    /** Call once from [AirBridgeConnectionService.onCreate] to start monitoring. */
    fun startNetworkMonitoring() {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                AirBridgeLog.info("[ConnSvc] Wi-Fi network available — waking reconnect loops")
                reconnectDelayJobs.values.forEach { it.cancel() }
            }
            override fun onLost(network: Network) {
                AirBridgeLog.warn("[ConnSvc] Wi-Fi network lost")
            }
        }
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .build()
        cm.registerNetworkCallback(request, cb)
        networkCallback = cb
        AirBridgeLog.info("[ConnSvc] Network monitoring started")
    }

    /** Call from [AirBridgeConnectionService.onDestroy]. */
    fun stopNetworkMonitoring() {
        networkCallback?.let {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            runCatching { cm.unregisterNetworkCallback(it) }
            networkCallback = null
            AirBridgeLog.info("[ConnSvc] Network monitoring stopped")
        }
    }

    // ── Public flows ──────────────────────────────────────────────────────

    /** Emits whenever a remote device initiates a pairing handshake with this device. */
    private val _incomingPairingRequests = MutableSharedFlow<InboundPairingRequest>()
    val incomingPairingRequests: SharedFlow<InboundPairingRequest> =
        _incomingPairingRequests.asSharedFlow()

    /** Emits channels for already-paired connections (non-pairing first messages). */
    private val _establishedChannels = MutableSharedFlow<IMessageChannel>()
    val establishedChannels: SharedFlow<IMessageChannel> = _establishedChannels.asSharedFlow()

    /**
     * Emits a [MirrorStartRequest] when the Windows host sends a [MessageType.MIRROR_START]
     * message on any active session. The foreground service ([AirBridgeConnectionService])
     * collects this and launches [com.airbridge.app.mirror.MirrorRequestActivity] to present
     * the MediaProjection permission dialog and start [com.airbridge.app.mirror.PhoneCaptureService].
     */
    private val _mirrorStartRequests = MutableSharedFlow<MirrorStartRequest>(extraBufferCapacity = 1)
    val mirrorStartRequests: SharedFlow<MirrorStartRequest> = _mirrorStartRequests.asSharedFlow()

    /** Tracks the result of the most-recent inbound pairing attempt for the UI. */
    private val _lastInboundPairingResult = MutableStateFlow<PairingResult?>(null)
    val lastInboundPairingResult: StateFlow<PairingResult?> = _lastInboundPairingResult.asStateFlow()

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /**
     * Non-null while an outbound reconnect is in progress; null when connected or idle.
     * The UI can observe this to show a "Reconnecting…" indicator.
     */
    private val _reconnectState = MutableStateFlow<ReconnectState?>(null)
    val reconnectState: StateFlow<ReconnectState?> = _reconnectState.asStateFlow()

    /**
     * Emits the device ID of a connection that failed all reconnect attempts.
     * The UI can observe this to show a "Connection failed" error with a Retry button.
     */
    private val _connectionFailedEvent = MutableSharedFlow<String>()
    val connectionFailedEvent: SharedFlow<String> = _connectionFailedEvent.asSharedFlow()

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
     * Connects to an already-paired [device] and keeps the connection alive indefinitely.
     *
     * The loop runs until the calling coroutine's scope is cancelled (i.e. the user
     * explicitly disconnects or exits the app).  It never gives up on its own:
     * - A 15-second connect timeout prevents a single attempt from hanging.
     * - On failure, exponential back-off grows from 2 s to 60 s, then stays at 60 s.
     * - When the network comes back after a Doze/Wi-Fi drop the [NetworkCallback]
     *   registered in [startNetworkMonitoring] cancels the active delay job so the
     *   next attempt happens immediately rather than waiting the full back-off period.
     * - On a successful connection that later drops, back-off resets to 2 s.
     */
    suspend fun connectToPairedDevice(device: DeviceInfo) {
        var backoffMs = 2_000L
        AirBridgeLog.info("[ConnSvc] Starting persistent connect loop for ${device.deviceId}")

        while (true) {   // exits only when the coroutine scope is cancelled
            val channel = try {
                withTimeout(15_000L) { connectionManager.connect(device) }
            } catch (e: Exception) {
                AirBridgeLog.warn("[ConnSvc] Connect to ${device.deviceId} failed: ${e.javaClass.simpleName}: ${e.message}; retry in ${backoffMs}ms")
                // Sleep with backoff, but allow the NetworkCallback to wake us early.
                val delayJob = scope.launch { delay(backoffMs) }
                reconnectDelayJobs[device.deviceId] = delayJob
                delayJob.join()
                reconnectDelayJobs.remove(device.deviceId)
                backoffMs = minOf(backoffMs * 2, 60_000L)
                continue
            }

            // Connected.
            backoffMs = 2_000L  // reset on success
            AirBridgeLog.info("[ConnSvc] Connected to ${device.deviceId}; registering session")
            registerSession(device.deviceId, channel)
            _establishedChannels.emit(channel)

            // Wait for the session to close.  runMessageLoop (started by registerSession) is the
            // sole reader of incomingMessages — we must NOT add a second .collect() here or we
            // create two concurrent readers on the same DataInputStream, which corrupts the stream
            // and causes immediate PONG timeouts.
            while (channel.isConnected) { delay(200) }

            AirBridgeLog.info("[ConnSvc] Session dropped for ${device.deviceId}; reconnecting immediately")
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
        val wasEmpty = _sessions.isEmpty()
        _sessions[deviceId] = channel
        _connectedDeviceIds.value = _connectedDeviceIds.value + deviceId
        if (!wakeLock.isHeld) wakeLock.acquire()
        if (!wifiLock.isHeld) wifiLock.acquire()
        // Start the foreground service on the first session so the OS cannot kill
        // the process while the app is in the background.
        if (wasEmpty) AirBridgeConnectionService.start(context)
        AirBridgeLog.info("[ConnSvc] Session registered: $deviceId (total=${_sessions.size})")
    }

    /** Removes the session from the registry and releases locks if no sessions remain. */
    private fun endSession(deviceId: String, channel: IMessageChannel) {
        _sessions.remove(deviceId, channel)
        _messageHandlers.remove(deviceId)
        _connectedDeviceIds.value = _connectedDeviceIds.value - deviceId
        if (_sessions.isEmpty()) {
            if (wakeLock.isHeld) wakeLock.release()
            if (wifiLock.isHeld) wifiLock.release()
            // No active sessions — release the foreground service so the OS can reclaim
            // resources if the app stays in the background.
            AirBridgeConnectionService.stop(context)
        }
        AirBridgeLog.info("[ConnSvc] Session closed: $deviceId (remaining=${_sessions.size})")
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
            AirBridgeLog.error("[ConnSvc] routeChannel error for ${channel.remoteDeviceId}", e)
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
            AirBridgeLog.error("[ConnSvc] [$deviceId] Transport error", e)
        } finally {
            endSession(deviceId, channel)
        }
    }

    private suspend fun dispatchMessage(deviceId: String, message: ProtocolMessage) {
        // Note: TlsMessageChannel already logs every received message at DEBUG level.
        // No duplicate log here to avoid doubling the output during file transfers.

        // When Windows requests a mirror session, emit to the dedicated flow so
        // AirBridgeConnectionService can start the MediaProjection permission flow.
        if (message.type == MessageType.MIRROR_START) {
            AirBridgeLog.info("[ConnSvc] [$deviceId] MIRROR_START received from Windows — emitting mirror request")
            _mirrorStartRequests.tryEmit(MirrorStartRequest(deviceId))
        }

        val handlers = _messageHandlers[deviceId] ?: return
        for (handler in handlers) {
            try {
                handler(message)
            } catch (e: Exception) {
                AirBridgeLog.error("[ConnSvc] [$deviceId] Handler error for type=${message.type}", e)
            }
        }
    }
}
