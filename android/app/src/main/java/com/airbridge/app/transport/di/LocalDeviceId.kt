package com.airbridge.app.transport.di

import javax.inject.Qualifier

/**
 * Hilt qualifier for the stable local device ID string
 * provided by [TransportModule.provideLocalDeviceId].
 */
@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class LocalDeviceId
