package com.airbridge.app

import android.app.Application
import dagger.hilt.android.HiltAndroidApp

/**
 * Application entry point. Hilt DI is initialized here.
 * All feature modules register their bindings via Hilt modules.
 */
@HiltAndroidApp
class AirBridgeApp : Application()
