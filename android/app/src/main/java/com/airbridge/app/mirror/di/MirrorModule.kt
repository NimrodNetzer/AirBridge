package com.airbridge.app.mirror.di

import android.view.Surface
import com.airbridge.app.mirror.InputInjector
import com.airbridge.app.mirror.MirrorService
import com.airbridge.app.mirror.TabletDisplaySession
import com.airbridge.app.mirror.interfaces.IMirrorService
import com.airbridge.app.transport.interfaces.IMessageChannel
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

/**
 * Hilt DI module for the mirror feature.
 *
 * Provides:
 * - [InputInjector] singleton for touch/key relay during phone-mirror sessions.
 * - [IMirrorService] bound to [MirrorService].
 * - [TabletDisplaySessionFactory] for creating tablet-display sessions.
 */
@Module
@InstallIn(SingletonComponent::class)
object MirrorModule {

    /**
     * Provides the singleton [InputInjector] used for touch and key relay.
     * Can be replaced in tests via a custom [dagger.hilt.testing.TestInstallIn] module.
     */
    @Provides
    @Singleton
    fun provideInputInjector(): InputInjector = InputInjector()

    /**
     * Provides [MirrorService] as [IMirrorService].
     */
    @Provides
    @Singleton
    fun provideMirrorService(inputInjector: InputInjector): IMirrorService =
        MirrorService(inputInjector)

    /**
     * Provides the [TabletDisplaySessionFactory] for creating [TabletDisplaySession] instances.
     *
     * The factory is called by [com.airbridge.app.display.TabletDisplayActivity] at runtime
     * once the [Surface] is available from its [android.view.SurfaceView].
     */
    @Provides
    @Singleton
    fun provideTabletDisplaySessionFactory(): TabletDisplaySessionFactory =
        object : TabletDisplaySessionFactory {
            override fun create(
                sessionId: String,
                channel: IMessageChannel,
                outputSurface: Surface,
            ): TabletDisplaySession = TabletDisplaySession(sessionId, channel, outputSurface)
        }

    /**
     * A factory interface for creating [TabletDisplaySession] instances.
     */
    interface TabletDisplaySessionFactory {
        /**
         * Creates a new [TabletDisplaySession].
         *
         * @param sessionId     Unique identifier for this session.
         * @param channel       Authenticated TLS channel to the Windows host.
         * @param outputSurface [Surface] to which decoded frames are rendered.
         */
        fun create(
            sessionId: String,
            channel: IMessageChannel,
            outputSurface: Surface,
        ): TabletDisplaySession
    }
}
