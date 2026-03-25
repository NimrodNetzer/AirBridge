package com.airbridge.app.mirror.di

import android.view.Surface
import com.airbridge.app.mirror.TabletDisplaySession
import com.airbridge.app.transport.interfaces.IMessageChannel
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.android.components.ActivityComponent

/**
 * Hilt DI module for the mirror feature.
 *
 * Provides a factory function for [TabletDisplaySession] so Activities can
 * request a session scoped to the current [IMessageChannel] and [Surface]
 * without knowing the concrete type.
 *
 * ## Usage
 * ```kotlin
 * @Inject lateinit var tabletDisplaySessionFactory: TabletDisplaySessionFactory
 * // ...
 * val session = tabletDisplaySessionFactory.create(sessionId, channel, surface)
 * ```
 *
 * The factory is [ActivityComponent]-scoped because [TabletDisplayActivity]
 * needs to create sessions on demand with the Surface obtained from its
 * [android.view.SurfaceView].  The [IMessageChannel] is passed in at creation
 * time rather than injected here because the channel is established dynamically
 * after transport / pairing completes.
 */
@Module
@InstallIn(ActivityComponent::class)
object MirrorModule {

    /**
     * A factory interface for creating [TabletDisplaySession] instances.
     * Exposed so callers can swap the implementation in tests.
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
            sessionId:     String,
            channel:       IMessageChannel,
            outputSurface: Surface,
        ): TabletDisplaySession
    }

    /**
     * Provides the default [TabletDisplaySessionFactory] that creates real
     * [TabletDisplaySession] instances.
     *
     * In instrumented tests, replace this binding with a mock factory via a
     * custom [dagger.hilt.testing.TestInstallIn] module.
     */
    @Suppress("unused") // Consumed by Hilt
    fun provideTabletDisplaySessionFactory(): TabletDisplaySessionFactory =
        object : TabletDisplaySessionFactory {
            override fun create(
                sessionId:     String,
                channel:       IMessageChannel,
                outputSurface: Surface,
            ): TabletDisplaySession = TabletDisplaySession(sessionId, channel, outputSurface)
        }
}
