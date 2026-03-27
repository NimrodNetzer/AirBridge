package com.airbridge.app.core.pairing

import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeout
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.security.SecureRandom
import javax.inject.Inject
import javax.inject.Singleton

/**
 * TOFU pairing service. Exchanges EC public keys over an [IMessageChannel]
 * and persists trusted keys in [KeyStore].
 *
 * PIN is 6 cryptographically-random digits shown on both devices for confirmation.
 * The PIN is exposed via [pinFlow] for the UI layer to observe (Iteration 8).
 */
@Singleton
class PairingService @Inject constructor(
    private val keyStore: KeyStore
) : IPairingService {

    /** Emits the current PIN during an active pairing attempt; null otherwise. */
    private val _pinFlow = MutableStateFlow<String?>(null)
    override val pinFlow: StateFlow<String?> = _pinFlow

    /**
     * Holds a [CompletableDeferred] for the duration of an inbound pairing handshake.
     * Completed by [confirmPairing] when the user taps Accept or Reject.
     */
    private var pendingConfirmation: CompletableDeferred<Boolean>? = null

    // ── IPairingService ────────────────────────────────────────────────────

    override suspend fun requestPairing(remoteDevice: DeviceInfo): PairingResult {
        if (keyStore.hasRemoteKey(remoteDevice.deviceId)) return PairingResult.ALREADY_PAIRED
        return PairingResult.ERROR // channel injection handled by higher layer
    }

    /**
     * Full outbound pairing flow when a channel is available.
     * Called by the transport layer after a connection is established.
     */
    override suspend fun requestPairingOnChannel(
        remoteDevice: DeviceInfo,
        channel: IMessageChannel
    ): PairingResult {
        if (keyStore.hasRemoteKey(remoteDevice.deviceId)) return PairingResult.ALREADY_PAIRED

        val pin = generatePin()
        _pinFlow.value = pin

        return try {
            val payload = buildRequestPayload(pin)
            channel.send(ProtocolMessage(MessageType.PAIRING_REQUEST, payload))

            withTimeout(60_000L) {
                val response = channel.incomingMessages
                    .first { it.type == MessageType.PAIRING_RESPONSE }
                val (accepted, remoteKey) = parseResponsePayload(response.payload)
                if (!accepted) return@withTimeout PairingResult.REJECTED_BY_USER
                keyStore.storeRemoteKey(remoteDevice.deviceId, remoteKey)
                PairingResult.SUCCESS
            }
        } catch (e: kotlinx.coroutines.TimeoutCancellationException) {
            PairingResult.TIMEOUT
        } catch (e: Exception) {
            PairingResult.ERROR
        } finally {
            _pinFlow.value = null
        }
    }

    override suspend fun acceptPairing(
        remoteDeviceId: String,
        remotePublicKey: ByteArray,
        pin: String
    ): PairingResult {
        if (pin.length != 6 || !pin.all { it.isDigit() }) return PairingResult.ERROR
        _pinFlow.value = pin
        keyStore.storeRemoteKey(remoteDeviceId, remotePublicKey)
        _pinFlow.value = null
        return PairingResult.SUCCESS
    }

    override suspend fun revokePairing(deviceId: String) {
        keyStore.removeRemoteKey(deviceId)
    }

    override fun getLocalPublicKey(): ByteArray = keyStore.getLocalPublicKey()

    /**
     * Inbound pairing: waits for a [MessageType.PAIRING_REQUEST] on [channel], shows the PIN
     * from the request via [_pinFlow], then waits for the user to call [confirmPairing].
     * Sends a [MessageType.PAIRING_RESPONSE] and stores the remote key if accepted.
     */
    override suspend fun acceptPairingOnChannel(
        device: DeviceInfo,
        channel: IMessageChannel,
        firstMessage: com.airbridge.app.transport.protocol.ProtocolMessage?,
    ): PairingResult {
        return try {
            withTimeout(60_000L) {
                // 1. Use the pre-read message if supplied, otherwise wait for PAIRING_REQUEST.
                val request = firstMessage?.takeIf { it.type == MessageType.PAIRING_REQUEST }
                    ?: channel.incomingMessages.first { it.type == MessageType.PAIRING_REQUEST }

                // 2. Parse payload: [2-byte key length][key bytes][6 ASCII PIN bytes]
                val (remoteKey, pin) = parseRequestPayload(request.payload)

                // 3. Show the PIN in the UI.
                _pinFlow.value = pin

                // 4. Wait for user confirmation.
                val deferred = CompletableDeferred<Boolean>()
                pendingConfirmation = deferred
                val accepted = deferred.await()
                pendingConfirmation = null

                // 5. Send PAIRING_RESPONSE.
                val responsePayload = buildResponsePayload(accepted, keyStore.getLocalPublicKey())
                channel.send(ProtocolMessage(MessageType.PAIRING_RESPONSE, responsePayload))

                // 6. Persist key if accepted.
                if (accepted) {
                    keyStore.storeRemoteKey(device.deviceId, remoteKey)
                    PairingResult.SUCCESS
                } else {
                    PairingResult.REJECTED_BY_USER
                }
            }
        } catch (e: kotlinx.coroutines.TimeoutCancellationException) {
            PairingResult.TIMEOUT
        } catch (e: Exception) {
            PairingResult.ERROR
        } finally {
            _pinFlow.value = null
            pendingConfirmation = null
        }
    }

    /**
     * Resolves the [CompletableDeferred] created by [acceptPairingOnChannel].
     * Should be called from the UI after the user taps Accept or Reject.
     */
    override suspend fun confirmPairing(accepted: Boolean) {
        pendingConfirmation?.complete(accepted)
    }

    // ── Protocol helpers ───────────────────────────────────────────────────

    private fun buildRequestPayload(pin: String): ByteArray {
        val localKey = keyStore.getLocalPublicKey()
        val baos = ByteArrayOutputStream()
        DataOutputStream(baos).use { dos ->
            dos.writeShort(localKey.size)
            dos.write(localKey)
            dos.write(pin.toByteArray(Charsets.US_ASCII))
        }
        return baos.toByteArray()
    }

    private fun parseRequestPayload(payload: ByteArray): Pair<ByteArray, String> {
        return DataInputStream(ByteArrayInputStream(payload)).use { dis ->
            val keyLen = dis.readUnsignedShort()
            val remoteKey = ByteArray(keyLen).also { dis.readFully(it) }
            val pinBytes = ByteArray(6).also { dis.readFully(it) }
            val pin = String(pinBytes, Charsets.US_ASCII)
            Pair(remoteKey, pin)
        }
    }

    private fun parseResponsePayload(payload: ByteArray): Pair<Boolean, ByteArray> {
        return try {
            DataInputStream(ByteArrayInputStream(payload)).use { dis ->
                val accepted = dis.readBoolean()
                val keyLen = dis.readUnsignedShort()
                val remoteKey = ByteArray(keyLen).also { dis.readFully(it) }
                Pair(accepted, remoteKey)
            }
        } catch (e: Exception) {
            Pair(false, ByteArray(0))
        }
    }

    companion object {
        /** Generates a 6-digit cryptographically-random PIN. */
        fun generatePin(): String {
            val rng = SecureRandom()
            return (rng.nextInt(1_000_000)).toString().padStart(6, '0')
        }

        fun buildResponsePayload(accepted: Boolean, localPublicKey: ByteArray): ByteArray {
            val baos = ByteArrayOutputStream()
            DataOutputStream(baos).use { dos ->
                dos.writeBoolean(accepted)
                dos.writeShort(localPublicKey.size)
                dos.write(localPublicKey)
            }
            return baos.toByteArray()
        }
    }
}
