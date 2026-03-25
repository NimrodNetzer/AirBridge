package com.airbridge.app.core.di

import com.airbridge.app.core.interfaces.IPairingService
import com.airbridge.app.core.pairing.PairingService
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
}
