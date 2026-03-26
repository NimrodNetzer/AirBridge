package com.airbridge.app.core.di

import com.airbridge.app.core.interfaces.IDeviceRegistry
import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.pairing.PairingService
import com.airbridge.app.core.registry.InMemoryDeviceRegistry
import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
abstract class CoreModule {

    @Binds
    @Singleton
    abstract fun bindPairingService(impl: PairingService): IPairingService

    @Binds
    @Singleton
    abstract fun bindDeviceRegistry(impl: InMemoryDeviceRegistry): IDeviceRegistry
}
