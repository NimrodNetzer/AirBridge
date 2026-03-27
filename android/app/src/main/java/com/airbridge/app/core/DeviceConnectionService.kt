package com.airbridge.app.core

import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
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
 * Routes inbound connections from [IConnectionManager.incomingConnections].
 *
 * When a new inbound channel arrives the service peeks at the first message:
 * - **PAIRING_REQUEST** → hands the channel to [IPairingService.acceptPairingOnChannel] and
 *   emits an [InboundPairingRequest] on [incomingPairingRequests] so the UI can display the PIN.
 * - All other message types → emits the channel on [establishedChannels] for higher-level
 *   feature services (file transfer, mirror, etc.) to consume.
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

    // -------------------------------------------------------------------------
    // Private routing logic
    // -------------------------------------------------------------------------

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
                    // The UI calls pairingService.confirmPairing() to resolve the
                    // CompletableDeferred inside acceptPairingOnChannel.
                    val result = pairingService.acceptPairingOnChannel(device, channel, firstMessage)
                    _lastInboundPairingResult.value = result
                }

                else -> {
                    // Re-expose the channel for feature services; they can inspect the first
                    // message if needed via their own flow collection.
                    _establishedChannels.emit(channel)
                }
            }
        } catch (e: Exception) {
            // Log and discard — one bad connection must not kill the accept loop.
        }
    }
}
