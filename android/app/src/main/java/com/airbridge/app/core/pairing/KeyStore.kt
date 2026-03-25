package com.airbridge.app.core.pairing

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import dagger.hilt.android.qualifiers.ApplicationContext
import java.security.KeyPairGenerator
import java.security.KeyStore as JavaKeyStore
import javax.inject.Inject
import javax.inject.Singleton

private const val KEYSTORE_ALIAS = "airbridge_local_key"
private const val ANDROID_KEYSTORE = "AndroidKeyStore"
private const val PREFS_FILE = "airbridge_paired_keys"

/**
 * Manages the local EC key pair (via Android Keystore) and persists
 * paired device public keys in EncryptedSharedPreferences.
 *
 * The local private key never leaves the hardware-backed Keystore.
 * Remote public keys are stored encrypted on disk.
 */
@Singleton
class KeyStore @Inject constructor(
    @ApplicationContext private val context: Context
) {
    private val prefs by lazy {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            PREFS_FILE,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }

    // ── Local key pair ─────────────────────────────────────────────────────

    /** Returns the local public key bytes (X.509 SubjectPublicKeyInfo format). */
    fun getLocalPublicKey(): ByteArray {
        ensureLocalKeyExists()
        val ks = JavaKeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        return ks.getCertificate(KEYSTORE_ALIAS).publicKey.encoded
    }

    private fun ensureLocalKeyExists() {
        val ks = JavaKeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        if (!ks.containsAlias(KEYSTORE_ALIAS)) {
            val spec = KeyGenParameterSpec.Builder(
                KEYSTORE_ALIAS,
                KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY
            )
                .setDigests(KeyProperties.DIGEST_SHA256)
                .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
                .build()

            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEYSTORE)
                .apply { initialize(spec) }
                .generateKeyPair()
        }
    }

    // ── Remote key storage ─────────────────────────────────────────────────

    /** Stores or updates the public key for a paired remote device. */
    fun storeRemoteKey(deviceId: String, publicKeyBytes: ByteArray) {
        prefs.edit()
            .putString(deviceId, android.util.Base64.encodeToString(publicKeyBytes, android.util.Base64.NO_WRAP))
            .apply()
    }

    /** Returns the stored public key bytes for a device, or null if not paired. */
    fun getRemoteKey(deviceId: String): ByteArray? {
        val b64 = prefs.getString(deviceId, null) ?: return null
        return android.util.Base64.decode(b64, android.util.Base64.NO_WRAP)
    }

    /** Removes a paired device's stored key. */
    fun removeRemoteKey(deviceId: String) {
        prefs.edit().remove(deviceId).apply()
    }

    /** Returns true if a key is stored for [deviceId]. */
    fun hasRemoteKey(deviceId: String): Boolean = prefs.contains(deviceId)
}
