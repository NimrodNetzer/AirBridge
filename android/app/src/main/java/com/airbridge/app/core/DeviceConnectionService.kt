package com.airbridge.app.core

import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Represents an inbound pairing request received from a remote (typically Windows) device.
 *
 * @param device    Synthetic [DeviceInfo] built from the connection's remote address.
 * @param channel   The live [IMessageChannel] on which the PAIRING_REQUEST was received.
 * @param result    Outcome after the handshake completes (populated after [confirmPairing] is called).
 */
data class InboundPairingRequest(
    val device: DeviceInfo,
    val channel: IMessageChannel,
)

/**
 * Routes inbound connections from [IConnectionManager.incomingConnections] and tracks the
 * currently active [IMessageChannel] for each connected device.
 *
 * When a new inbound channel arrives the service peeks at the first message:
 * - **PAIRING_REQUEST** → hands the channel to [IPairingService.acceptPairingOnChannel] and
 *   emits an [InboundPairingRequest] on [incomingPairingRequests] so the UI can display the PIN.
 *   On success the channel is registered as the active session.
 * - All other message types → the device is already paired; register the channel directly as
 *   an active session and emit on [establishedChannels] for feature services.
 *
 * The service is a singleton and must be started by calling [start] once the app is ready to
 * accept connections (typically from [MainActivity] or a Hilt entry-point).
 */
@Singleton
class DeviceConnectionService @Inject constructor(
    private val connectionManager: IConnectionManager,
    private val pairingService: IPairingService,
) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val TAG = "AirBridge/ConnSvc"

    // ── Active session registry ───────────────────────────────────────────

    private val _sessions = ConcurrentHashMap<String, IMessageChannel>()

    /**
     * Returns the active [IMessageChannel] for [deviceId], or null if none is open.
     */
    fun getActiveSession(deviceId: String): IMessageChannel? = _sessions[deviceId]

    /**
     * Reactive set of device IDs that currently have an open session.
     * Updates whenever a session is opened or closed.
     */
    private val _connectedDeviceIds = MutableStateFlow<Set<String>>(emptySet())
    val connectedDeviceIds: StateFlow<Set<String>> = _connectedDeviceIds.asStateFlow()

    // ── Existing flows ────────────────────────────────────────────────────

    /** Emits whenever a remote device initiates a pairing handshake with this device. */
    private val _incomingPairingRequests = MutableSharedFlow<InboundPairingRequest>()
    val incomingPairingRequests: SharedFlow<InboundPairingRequest> =
        _incomingPairingRequests.asSharedFlow()

    /** Emits channels for already-paired connections (non-pairing messages). */
    private val _establishedChannels = MutableSharedFlow<IMessageChannel>()
    val establishedChannels: SharedFlow<IMessageChannel> = _establishedChannels.asSharedFlow()

    /** Tracks the result of the most-recent inbound pairing attempt for the UI. */
    private val _lastInboundPairingResult = MutableStateFlow<PairingResult?>(null)
    val lastInboundPairingResult: StateFlow<PairingResult?> = _lastInboundPairingResult.asStateFlow()

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
     */
    suspend fun connectToPairedDevice(device: DeviceInfo) {
        val channel = connectionManager.connect(device)
        registerSession(device.deviceId, channel)
        _establishedChannels.emit(channel)
    }

    // ── Message dispatcher ────────────────────────────────────────────────

    private val _messageHandlers = ConcurrentHashMap<String, CopyOnWriteArrayList<suspend (com.airbridge.app.transport.protocol.ProtocolMessage) -> Unit>>()

    /**
     * Registers a handler that will be invoked for every incoming message on the session
     * for [deviceId]. Multiple handlers may be registered for the same device; each message
     * is delivered to all of them in registration order.
     *
     * The handler is removed automatically when the session for [deviceId] closes.
     *
     * @param deviceId The device whose incoming messages this handler should receive.
     * @param handler  Suspend lambda invoked with each [ProtocolMessage].
     */
    fun addMessageHandler(deviceId: String, handler: suspend (com.airbridge.app.transport.protocol.ProtocolMessage) -> Unit) {
        _messageHandlers.getOrPut(deviceId) { CopyOnWriteArrayList() }.add(handler)
    }

    /**
     * Removes all message handlers for [deviceId].
     */
    fun removeMessageHandlers(deviceId: String) {
        _messageHandlers.remove(deviceId)
    }

    /**
     * Registers [channel] as the active session for [deviceId] and starts a dispatcher
     * coroutine that fans out every incoming message to all registered handlers.
     * Replaces any previous session for the same device.
     *
     * Call this after outbound pairing succeeds to keep the pairing channel alive.
     */
    fun registerSession(deviceId: String, channel: IMessageChannel) {
        _sessions[deviceId] = channel
        _connectedDeviceIds.value = _connectedDeviceIds.value + deviceId
        Log.i(TAG, "Session registered: $deviceId")

        // Dispatch incoming messages to registered handlers; detect disconnect in finally.
        scope.launch {
            try {
                channel.incomingMessages.collect { message ->
                    Log.d(TAG, "[$deviceId] RX type=${message.type} len=${message.payload.size}")
                    val handlers = _messageHandlers[deviceId] ?: return@collect
                    for (handler in handlers) {
                        try {
                            handler(message)
                        } catch (e: Exception) {
                            Log.e(TAG, "[$deviceId] Handler error for type=${message.type}: ${e.message}", e)
                        }
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "[$deviceId] Transport error: ${e.javaClass.simpleName}: ${e.message}", e)
            } finally {
                _sessions.remove(deviceId, channel)
                _messageHandlers.remove(deviceId)
                _connectedDeviceIds.value = _connectedDeviceIds.value - deviceId
                Log.i(TAG, "Session closed: $deviceId")
            }
        }
    }

    // ── Private routing logic ─────────────────────────────────────────────

    private suspend fun routeChannel(channel: IMessageChannel) {
        try {
            val firstMessage = channel.incomingMessages.first()
            when (firstMessage.type) {
                MessageType.PAIRING_REQUEST -> {
                    // Build a synthetic DeviceInfo from the channel's remote device ID
                    // (which is the remote IP address as set by TlsConnectionManager).
                    val device = DeviceInfo(
                        deviceId = channel.remoteDeviceId,
                        deviceName = channel.remoteDeviceId,
                        deviceType = DeviceType.WINDOWS_PC,
                        ipAddress = channel.remoteDeviceId,
                        port = 0,
                        isPaired = false,
                    )

                    // Emit so the UI can subscribe and show the incoming request / PIN.
                    _incomingPairingRequests.emit(InboundPairingRequest(device, channel))

                    // Drive the handshake, passing the already-read first message so
                    // acceptPairingOnChannel doesn't attempt a second read of the same socket.
                    val result = pairingService.acceptPairingOnChannel(device, channel, firstMessage)
                    _lastInboundPairingResult.value = result

                    // On success, keep the channel alive as the active session.
                    if (result == PairingResult.SUCCESS || result == PairingResult.ALREADY_PAIRED) {
                        registerSession(channel.remoteDeviceId, channel)
                    }
                }

                else -> {
                    // Already-paired device reconnecting — register as active session.
                    registerSession(channel.remoteDeviceId, channel)
                    _establishedChannels.emit(channel)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "routeChannel error for ${channel.remoteDeviceId}: ${e.message}", e)
        }
    }
}
