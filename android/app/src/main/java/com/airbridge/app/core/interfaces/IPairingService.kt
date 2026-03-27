package com.airbridge.app.core.interfaces

import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IMessageChannel
import kotlinx.coroutines.flow.StateFlow

enum class PairingResult { SUCCESS, REJECTED_BY_USER, TIMEOUT, ALREADY_PAIRED, ERROR }

/**
 * Handles the TOFU pairing handshake.
 * Generates and stores Ed25519 key pairs; verifies remote keys via PIN confirmation.
 */
interface IPairingService {
    /** Emits the current 6-digit PIN during an active pairing attempt; null otherwise. */
    val pinFlow: StateFlow<String?>

    suspend fun requestPairing(remoteDevice: DeviceInfo): PairingResult
    suspend fun acceptPairing(remoteDeviceId: String, remotePublicKey: ByteArray, pin: String): PairingResult
    suspend fun revokePairing(deviceId: String)
    fun getLocalPublicKey(): ByteArray

    /**
     * Outbound pairing: sends a PAIRING_REQUEST on [channel], emits the PIN via [pinFlow],
     * and waits for a PAIRING_RESPONSE from the remote device.
     *
     * @param device Metadata for the remote device.
     * @param channel The already-established message channel to the remote device.
     */
    suspend fun requestPairingOnChannel(device: DeviceInfo, channel: IMessageChannel): PairingResult

    /**
     * Inbound pairing: reads a PAIRING_REQUEST from [channel] sent by the remote initiator,
     * displays the PIN via [pinFlow], waits for [confirmPairing], then sends a PAIRING_RESPONSE.
     *
     * If [firstMessage] is non-null it is treated as the PAIRING_REQUEST and the channel's
     * [IMessageChannel.incomingMessages] stream is NOT read for the first message.  This lets
     * a higher-level router pre-read one message for routing purposes without losing it.
     *
     * @param device       Metadata for the remote device (used when storing its key).
     * @param channel      The already-established message channel from the remote device.
     * @param firstMessage Optional pre-read PAIRING_REQUEST message (payload only used).
     */
    suspend fun acceptPairingOnChannel(
        device: DeviceInfo,
        channel: IMessageChannel,
        firstMessage: com.airbridge.app.transport.protocol.ProtocolMessage? = null,
    ): PairingResult

    /**
     * Called by the UI (or a test) to confirm or reject a pending inbound pairing request
     * started by [acceptPairingOnChannel].
     *
     * @param accepted true if the user confirmed the PIN; false to reject.
     */
    suspend fun confirmPairing(accepted: Boolean)
}
