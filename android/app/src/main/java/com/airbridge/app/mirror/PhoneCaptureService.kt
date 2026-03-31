package com.airbridge.app.mirror

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.IBinder
import android.util.DisplayMetrics
import android.view.WindowManager
import com.airbridge.app.core.DeviceConnectionService
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.util.UUID
import javax.inject.Inject

/**
 * Foreground service that owns the [MediaProjection] token and drives the phone-mirror pipeline.
 *
 * Lifecycle:
 * 1. Start via [startCapture] with the [MediaProjection] result-code / intent from the Activity.
 * 2. The service acquires the [MediaProjection], starts [ScreenCaptureSession], and wires it to
 *    a [MirrorSession] bound to the active channel for [deviceId].
 * 3. When [stopCapture] is called (or the session ends), the service stops itself.
 *
 * A sticky foreground notification with channel-id [CHANNEL_ID] is shown while active
 * (required by Android for foreground services using `FOREGROUND_SERVICE_MEDIA_PROJECTION`).
 */
@AndroidEntryPoint
class PhoneCaptureService : Service() {

    @Inject
    lateinit var deviceConnectionService: DeviceConnectionService

    @Inject
    lateinit var mirrorService: MirrorService

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var mirrorSession: MirrorSession? = null
    private var captureSession: ScreenCaptureSession? = null
    private var captureJob: Job? = null
    private var mirrorDeviceId: String? = null

    // ── Service lifecycle ──────────────────────────────────────────────────

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        // Satisfy Android's 5-second startForeground() requirement immediately in onCreate.
        // Use DATA_SYNC type here — we don't have a MediaProjection token yet, so we cannot
        // declare MEDIA_PROJECTION type until beginCapture() obtains it from the user.
        // Both types are declared in the manifest so the upgrade in beginCapture() is valid.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFICATION_ID, buildNotification())
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                val data       = intent.getParcelableExtra<Intent>(EXTRA_RESULT_DATA) ?: run {
                    stopSelf(); return START_NOT_STICKY
                }
                val deviceId = intent.getStringExtra(EXTRA_DEVICE_ID) ?: run {
                    stopSelf(); return START_NOT_STICKY
                }

                beginCapture(resultCode, data, deviceId)
            }

            ACTION_STOP -> {
                stopCapture()
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        super.onDestroy()
        stopCapture()
    }

    // ── Capture pipeline ───────────────────────────────────────────────────

    /**
     * Acquires [MediaProjection], starts [ScreenCaptureSession], and wires the encoded
     * frames into a [MirrorSession] on the active channel for [deviceId].
     */
    private fun beginCapture(resultCode: Int, data: Intent, deviceId: String) {
        // Android 14+ requires the foreground service type to be MEDIA_PROJECTION
        // BEFORE calling getMediaProjection() — not after.  Upgrade the type here,
        // before acquiring the token, so the OS constraint is satisfied.
        startForegroundCompat()

        val projectionManager =
            getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val projection: MediaProjection =
            projectionManager.getMediaProjection(resultCode, data)

        val windowManager = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val metrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        windowManager.defaultDisplay.getRealMetrics(metrics)

        val width     = metrics.widthPixels
        val height    = metrics.heightPixels
        val dpi       = metrics.densityDpi
        val fps       = TARGET_FPS
        val sessionId = UUID.randomUUID().toString()

        val channel = deviceConnectionService.getActiveSession(deviceId) ?: run {
            stopSelf(); return
        }

        val capture = ScreenCaptureSession(projection, dpi)
        captureSession = capture
        // If the system revokes MediaProjection externally, propagate to a clean teardown
        // so MIRROR_STOP is always sent to Windows.
        capture.onStopped = { stopCapture() }

        val session = mirrorService.startMirrorWithChannel(
            sessionId      = sessionId,
            channel        = channel,
            captureSession = capture,
            width          = width,
            height         = height,
            fps            = fps,
        )
        mirrorSession   = session
        mirrorDeviceId  = deviceId

        // Register a message handler so INPUT_EVENT and MIRROR_STOP from Windows are
        // forwarded to the session without competing with DeviceConnectionService's
        // sole channel reader.
        val handler = session.createMessageHandler(onStop = { stopCapture() })
        deviceConnectionService.addMessageHandler(deviceId, handler)

        captureJob = scope.launch {
            // Start encoder
            capture.start(sessionId, width, height, fps)

            // Start mirror session (sends MIRROR_START, then streams frames)
            try {
                session.start()
            } finally {
                stopSelf()
            }
        }
    }

    /**
     * Stops the capture pipeline gracefully and releases resources.
     */
    private fun stopCapture() {
        captureJob?.cancel()
        captureSession?.stop()
        // Remove message handler before stopping session to prevent stray INPUT_EVENTs
        mirrorDeviceId?.let { deviceConnectionService.removeMessageHandlers(it) }
        scope.launch {
            try { mirrorSession?.stop() } catch (_: Exception) { }
        }
        mirrorSession  = null
        captureSession = null
        mirrorDeviceId = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    // ── Notification helpers ───────────────────────────────────────────────

    private fun startForegroundCompat() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(NOTIFICATION_ID, buildNotification())
        }
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "Screen Mirroring",
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = "Active while mirroring your phone screen to PC"
        }
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification =
        Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("AirBridge Mirror")
            .setContentText("Mirroring your screen to PC…")
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setOngoing(true)
            .build()

    // ── Companion ─────────────────────────────────────────────────────────

    companion object {
        const val ACTION_START       = "com.airbridge.app.mirror.ACTION_START"
        const val ACTION_STOP        = "com.airbridge.app.mirror.ACTION_STOP"
        const val EXTRA_RESULT_CODE  = "resultCode"
        const val EXTRA_RESULT_DATA  = "resultData"
        const val EXTRA_DEVICE_ID    = "deviceId"
        private const val CHANNEL_ID      = "airbridge_mirror"
        private const val NOTIFICATION_ID = 101
        private const val TARGET_FPS      = 30

        /**
         * Builds a start [Intent] for [PhoneCaptureService].
         *
         * @param context    Caller context.
         * @param resultCode Result code from [android.app.Activity.onActivityResult].
         * @param data       Data intent from [android.app.Activity.onActivityResult].
         * @param deviceId   The paired device ID to stream to.
         */
        fun startIntent(
            context:    Context,
            resultCode: Int,
            data:       Intent,
            deviceId:   String,
        ): Intent = Intent(context, PhoneCaptureService::class.java).apply {
            action = ACTION_START
            putExtra(EXTRA_RESULT_CODE, resultCode)
            putExtra(EXTRA_RESULT_DATA, data)
            putExtra(EXTRA_DEVICE_ID, deviceId)
        }

        /** Builds a stop [Intent] for [PhoneCaptureService]. */
        fun stopIntent(context: Context): Intent =
            Intent(context, PhoneCaptureService::class.java).apply {
                action = ACTION_STOP
            }
    }
}
