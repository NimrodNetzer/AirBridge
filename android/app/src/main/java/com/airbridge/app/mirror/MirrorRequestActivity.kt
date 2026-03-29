package com.airbridge.app.mirror

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import com.airbridge.app.core.AirBridgeLog

/**
 * Transparent trampoline Activity that presents the system MediaProjection permission dialog
 * in response to an incoming [com.airbridge.app.transport.protocol.MessageType.MIRROR_START]
 * request from the Windows host.
 *
 * Launched by [com.airbridge.app.core.AirBridgeConnectionService] when
 * [com.airbridge.app.core.DeviceConnectionService.mirrorStartRequests] emits.
 * On permission grant, starts [PhoneCaptureService] as a foreground service carrying the
 * MediaProjection token. On denial (or if the device ID is missing), finishes silently.
 */
class MirrorRequestActivity : ComponentActivity() {

    private val launcher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val deviceId = intent.getStringExtra(EXTRA_DEVICE_ID)
        if (result.resultCode == Activity.RESULT_OK && result.data != null && deviceId != null) {
            AirBridgeLog.info("[MirrorReq] MediaProjection granted — starting PhoneCaptureService for $deviceId")
            val serviceIntent = PhoneCaptureService.startIntent(
                context    = this,
                resultCode = result.resultCode,
                data       = result.data!!,
                deviceId   = deviceId,
            )
            startForegroundService(serviceIntent)
        } else {
            AirBridgeLog.info("[MirrorReq] MediaProjection denied or missing device — aborting mirror")
        }
        finish()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        AirBridgeLog.info("[MirrorReq] Requesting MediaProjection permission")
        val pm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        launcher.launch(pm.createScreenCaptureIntent())
    }

    companion object {
        const val EXTRA_DEVICE_ID = "deviceId"

        /**
         * Builds the launch [Intent] for [MirrorRequestActivity].
         *
         * @param context  Caller context.
         * @param deviceId The ID of the paired Windows device that sent the MIRROR_START.
         */
        fun buildIntent(context: Context, deviceId: String): Intent =
            Intent(context, MirrorRequestActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK
                putExtra(EXTRA_DEVICE_ID, deviceId)
            }
    }
}
