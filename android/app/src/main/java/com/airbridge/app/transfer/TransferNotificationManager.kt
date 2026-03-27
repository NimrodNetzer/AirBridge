package com.airbridge.app.transfer

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import androidx.core.app.NotificationCompat
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Posts and updates a persistent notification for active file transfers.
 *
 * Uses [NotificationManager] with a progress-style notification for in-progress
 * transfers and a summary notification on completion.
 */
@Singleton
class TransferNotificationManager @Inject constructor(
    @ApplicationContext private val context: Context,
) {
    companion object {
        const val CHANNEL_ID = "airbridge_transfer"
        const val NOTIFICATION_ID = 1001
    }

    init {
        createChannel()
    }

    private fun createChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "File Transfers",
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = "Progress for AirBridge file transfers"
        }
        notificationManager().createNotificationChannel(channel)
    }

    /** Shows or updates the in-progress transfer notification with [progressPercent] (0–100). */
    fun showProgress(fileName: String, progressPercent: Int) {
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Transferring $fileName")
            .setProgress(100, progressPercent, false)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
        notificationManager().notify(NOTIFICATION_ID, notification)
    }

    /** Replaces the progress notification with a "complete" summary and auto-dismisses. */
    fun showComplete(fileName: String) {
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setContentTitle("Transfer complete")
            .setContentText(fileName)
            .setAutoCancel(true)
            .build()
        notificationManager().notify(NOTIFICATION_ID + 1, notification)
        cancel()
    }

    /** Cancels the ongoing transfer notification. */
    fun cancel() = notificationManager().cancel(NOTIFICATION_ID)

    private fun notificationManager(): NotificationManager =
        context.getSystemService(NotificationManager::class.java)
}
