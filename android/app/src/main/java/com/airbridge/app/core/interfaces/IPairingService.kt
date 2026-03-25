package com.airbridge.app.core.interfaces

import com.airbridge.app.core.models.DeviceInfo

enum class PairingResult { SUCCESS, REJECTED_BY_USER, TIMEOUT, ALREADY_PAIRED, ERROR }

/**
 * Handles the TOFU pairing handshake.
 * Generates and stores Ed25519 key pairs; verifies remote keys via PIN confirmation.
 */
interface IPairingService {
    suspend fun requestPairing(remoteDevice: DeviceInfo): PairingResult
    suspend fun acceptPairing(remoteDeviceId: String, remotePublicKey: ByteArray, pin: String): PairingResult
    suspend fun revokePairing(deviceId: String)
    fun getLocalPublicKey(): ByteArray
}
