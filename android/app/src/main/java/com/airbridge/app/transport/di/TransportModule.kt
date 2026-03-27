package com.airbridge.app.transport.di

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import com.airbridge.app.transport.connection.TlsConnectionManager
import com.airbridge.app.transport.discovery.NsdDiscoveryService
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IDiscoveryService
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import java.util.UUID
import javax.inject.Singleton

/**
 * Hilt DI module that wires the transport layer implementations to their interfaces.
 *
 * Both bindings are [Singleton]-scoped so the same service instance is shared
 * across the entire application lifetime.
 *
 * [TlsConnectionManager] requires a stable device ID and device name for the
 * HANDSHAKE protocol exchange.  The device ID is persisted in [SharedPreferences]
 * (created once on first launch); the device name uses [Build.MODEL].
 */
@Module
@InstallIn(SingletonComponent::class)
object TransportModule {

    private const val PREFS_NAME   = "airbridge_transport"
    private const val KEY_DEVICE_ID = "device_id"

    /**
     * Returns the stable device ID for this Android device, creating and persisting
     * a new random UUID if none exists yet.
     */
    @Provides
    @Singleton
    @LocalDeviceId
    fun provideLocalDeviceId(@ApplicationContext context: Context): String {
        val prefs: SharedPreferences =
            context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val existing = prefs.getString(KEY_DEVICE_ID, null)
        if (!existing.isNullOrEmpty()) return existing
        val newId = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_DEVICE_ID, newId).apply()
        return newId
    }

    /**
     * Provides the [TlsConnectionManager] singleton wired with the local device identity.
     */
    @Provides
    @Singleton
    fun provideTlsConnectionManager(
        @LocalDeviceId deviceId: String,
    ): TlsConnectionManager =
        TlsConnectionManager(
            localDeviceId   = deviceId,
            localDeviceName = Build.MODEL,
        )

    /**
     * Binds [TlsConnectionManager] as the [IConnectionManager] singleton.
     */
    @Provides
    @Singleton
    fun bindConnectionManager(impl: TlsConnectionManager): IConnectionManager = impl

    /**
     * Provides the [NsdDiscoveryService] singleton.
     * NsdDiscoveryService is constructed by Hilt via its @Inject constructor.
     */
    @Provides
    @Singleton
    fun bindDiscoveryService(impl: NsdDiscoveryService): IDiscoveryService = impl
}
