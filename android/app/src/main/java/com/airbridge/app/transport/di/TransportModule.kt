package com.airbridge.app.transport.di

import com.airbridge.app.transport.connection.TlsConnectionManager
import com.airbridge.app.transport.discovery.NsdDiscoveryService
import com.airbridge.app.transport.interfaces.IConnectionManager
import com.airbridge.app.transport.interfaces.IDiscoveryService
import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

/**
 * Hilt DI module that wires the transport layer implementations to their interfaces.
 *
 * Both bindings are [Singleton]-scoped so the same service instance is shared
 * across the entire application lifetime.
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class TransportModule {

    /**
     * Binds [NsdDiscoveryService] as the [IDiscoveryService] singleton.
     * NsdDiscoveryService is constructed by Hilt via its @Inject constructor.
     */
    @Binds
    @Singleton
    abstract fun bindDiscoveryService(impl: NsdDiscoveryService): IDiscoveryService

    /**
     * Binds [TlsConnectionManager] as the [IConnectionManager] singleton.
     * TlsConnectionManager is constructed by Hilt via its @Inject constructor.
     */
    @Binds
    @Singleton
    abstract fun bindConnectionManager(impl: TlsConnectionManager): IConnectionManager
}
