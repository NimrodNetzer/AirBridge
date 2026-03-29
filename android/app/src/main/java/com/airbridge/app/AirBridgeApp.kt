package com.airbridge.app

import android.app.Application
import com.airbridge.app.core.AirBridgeLog
import dagger.hilt.android.HiltAndroidApp
import java.io.File

/**
 * Application entry point. Hilt DI is initialized here.
 * All feature modules register their bindings via Hilt modules.
 */
@HiltAndroidApp
class AirBridgeApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // Black-box log mirrors the Windows AppLog — both write to a local file so
        // the two sides can be compared side-by-side after a failure.
        AirBridgeLog.init(File(filesDir, "airbridge.log"))
    }
}
