package com.airbridge.app.core

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.airbridge.app.ui.MainActivity
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject

/**
 * Foreground service that keeps the AirBridge process alive while a device session is active.
 *
 * ## Why this is necessary
 * Android aggressively kills background processes to save battery. Without a visible foreground
 * service, the OS can kill the app within ~1 minute of it going to the background, dropping all
 * active TLS connections.  A foreground service — indicated by a persistent notification — signals
 * to the OS that the user is aware of and expects ongoing background activity, preventing process
 * termination.
 *
 * ## Lifecycle
 * - Started by the UI (e.g. [MainActivity]) when the first paired device connects.
 * - Stopped by the user via the "Disconnect" notification action, or programmatically
 *   when the last session closes.
 * - [DeviceConnectionService.startNetworkMonitoring] and [stopNetworkMonitoring] are
 *   called in [onCreate] / [onDestroy] so the network callback is tied to this service's lifetime.
 *
 * ## What it does NOT do
 * This service does not own connections or coroutines. All session logic lives in the
 * Hilt singleton [DeviceConnectionService]. This service is purely a lifecycle anchor.
 */
@AndroidEntryPoint
class AirBridgeConnectionService : Service() {

    @Inject lateinit var deviceConnectionService: DeviceConnectionService

    companion object {
        private const val CHANNEL_ID = "airbridge_connection"
        private const val NOTIF_ID   = 1001
        const val ACTION_STOP        = "com.airbridge.app.ACTION_STOP_CONNECTION"

        /** Starts the foreground service. Call when the first session becomes active. */
        fun start(context: Context) {
            val intent = Intent(context, AirBridgeConnectionService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        /** Stops the foreground service. Call when the last session closes or user disconnects. */
        fun stop(context: Context) {
            context.stopService(Intent(context, AirBridgeConnectionService::class.java))
        }
    }

    // -------------------------------------------------------------------------

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        deviceConnectionService.startNetworkMonitoring()
        AirBridgeLog.info("[FgService] Created — network monitoring started")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            AirBridgeLog.info("[FgService] Stop action received — stopping service")
            stopSelf()
            return START_NOT_STICKY
        }
        startForeground(NOTIF_ID, buildNotification())
        AirBridgeLog.info("[FgService] Started foreground")
        // START_STICKY: if the OS kills us (very unlikely with a fg service), restart with a null intent
        return START_STICKY
    }

    override fun onDestroy() {
        deviceConnectionService.stopNetworkMonitoring()
        AirBridgeLog.info("[FgService] Destroyed — network monitoring stopped")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    // -------------------------------------------------------------------------

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "AirBridge Connection",
                NotificationManager.IMPORTANCE_LOW   // silent; no sound/vibration
            ).apply {
                description = "Keeps AirBridge connected to nearby devices"
                setShowBadge(false)
            }
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    private fun buildNotification(): Notification {
        // Tapping the notification opens the app
        val openAppIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_IMMUTABLE
        )

        // "Disconnect" action stops this service (which the UI observes to close sessions)
        val stopIntent = PendingIntent.getService(
            this, 0,
            Intent(this, AirBridgeConnectionService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("AirBridge — Connected")
            .setContentText("Active session in progress")
            .setSmallIcon(android.R.drawable.ic_menu_share)   // TODO: replace with app icon R.drawable.ic_notification
            .setOngoing(true)         // user cannot swipe away
            .setContentIntent(openAppIntent)
            .addAction(android.R.drawable.ic_delete, "Disconnect", stopIntent)
            .build()
    }
}
