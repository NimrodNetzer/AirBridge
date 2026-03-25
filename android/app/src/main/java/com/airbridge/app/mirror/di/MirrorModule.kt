package com.airbridge.app.mirror.di

import com.airbridge.app.mirror.InputInjector
import com.airbridge.app.mirror.MirrorService
import com.airbridge.app.mirror.interfaces.IMirrorService
import dagger.Binds
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

/**
 * Hilt module that binds [IMirrorService] to [MirrorService] and provides
 * the singleton [InputInjector] used for touch and key relay.
 *
 * Installed in [SingletonComponent] so the same instances are shared
 * across the app lifecycle.
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class MirrorModule {

    @Binds
    @Singleton
    abstract fun bindMirrorService(impl: MirrorService): IMirrorService

    companion object {

        /**
         * Provides the singleton [InputInjector].
         *
         * [InputInjector] is annotated with [@Singleton][Singleton] and [@Inject][javax.inject.Inject]
         * so Hilt can also construct it automatically; this explicit [Provides] method is included
         * for clarity and to allow test modules to swap in a no-op stub.
         */
        @Provides
        @Singleton
        fun provideInputInjector(): InputInjector = InputInjector()
    }
}
