package com.airbridge.app.core.pairing

import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.transport.interfaces.IMessageChannel
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
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
    val pinFlow: StateFlow<String?> = _pinFlow

    // ── IPairingService ────────────────────────────────────────────────────

    override suspend fun requestPairing(remoteDevice: DeviceInfo): PairingResult {
        if (keyStore.hasRemoteKey(remoteDevice.deviceId)) return PairingResult.ALREADY_PAIRED
        return PairingResult.ERROR // channel injection handled by higher layer
    }

    /**
     * Full pairing flow when a channel is available.
     * Called by the transport layer after a connection is established.
     */
    suspend fun requestPairingOnChannel(
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
                var response: ProtocolMessage? = null
                channel.incomingMessages.collect { msg ->
                    if (msg.type == MessageType.PAIRING_RESPONSE) {
                        response = msg
                        return@collect
                    }
                }
                val (accepted, remoteKey) = parseResponsePayload(response?.payload ?: return@withTimeout PairingResult.ERROR)
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
