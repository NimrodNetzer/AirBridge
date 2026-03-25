package com.airbridge.app.mirror.di

import com.airbridge.app.mirror.MirrorService
import com.airbridge.app.mirror.interfaces.IMirrorService
import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

/**
 * Hilt module that binds [IMirrorService] to [MirrorService].
 *
 * Installed in [SingletonComponent] so the same service instance is shared
 * across the app lifecycle.
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class MirrorModule {

    @Binds
    @Singleton
    abstract fun bindMirrorService(impl: MirrorService): IMirrorService
}
