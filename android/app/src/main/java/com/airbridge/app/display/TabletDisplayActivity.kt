package com.airbridge.app.display

import android.app.Activity
import android.os.Bundle
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.Window
import android.view.WindowInsetsController
import android.view.WindowManager
import com.airbridge.app.mirror.TabletDisplaySession
import com.airbridge.app.transport.interfaces.IMessageChannel
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * Full-screen activity that renders the Windows virtual display stream on the
 * Android tablet. Hosts a [SurfaceView] that fills the entire screen (no title
 * bar, no navigation bar, no status bar).
 *
 * ## Lifecycle
 * 1. Activity is started (e.g. via an Intent from [com.airbridge.app.ui.MainActivity]
 *    once a paired Windows host begins a [TabletDisplay][com.airbridge.app.core.interfaces.MirrorMode.TABLET_DISPLAY]
 *    session).
 * 2. When the [SurfaceView]'s [Surface][android.view.Surface] is ready
 *    ([SurfaceHolder.Callback.surfaceCreated]), a [TabletDisplaySession] is
 *    created and started.
 * 3. When the surface is destroyed (rotation, activity finish), the session is
 *    stopped and the decoder is released.
 *
 * ## Manifest entry (in AndroidManifest.xml)
 * ```xml
 * <activity
 *     android:name=".display.TabletDisplayActivity"
 *     android:exported="false"
 *     android:screenOrientation="landscape"
 *     android:theme="@android:style/Theme.NoTitleBar.Fullscreen"
 *     android:configChanges="orientation|screenSize|keyboardHidden" />
 * ```
 *
 * ## Injected dependency
 * The [IMessageChannel] is injected by Hilt. In production it is the live TLS
 * channel to the paired Windows host; in tests it can be replaced with a mock.
 */
@AndroidEntryPoint
class TabletDisplayActivity : Activity(), SurfaceHolder.Callback {

    @Inject
    lateinit var channel: IMessageChannel

    // ── Internal ───────────────────────────────────────────────────────────

    private val activityScope = CoroutineScope(Dispatchers.Main)
    private var session: TabletDisplaySession? = null
    private lateinit var surfaceView: SurfaceView

    // ── Activity lifecycle ─────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Remove title bar and go full-screen
        requestWindowFeature(Window.FEATURE_NO_TITLE)
        window.setFlags(
            WindowManager.LayoutParams.FLAG_FULLSCREEN,
            WindowManager.LayoutParams.FLAG_FULLSCREEN
        )

        // Hide system bars (Android 11+)
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
            window.insetsController?.apply {
                hide(android.view.WindowInsets.Type.systemBars())
                systemBarsBehavior =
                    WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        }

        // Keep screen on while mirroring
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // Create a SurfaceView that fills the window
        surfaceView = SurfaceView(this)
        setContentView(surfaceView)
        surfaceView.holder.addCallback(this)
    }

    override fun onDestroy() {
        super.onDestroy()
        activityScope.cancel()
    }

    // ── SurfaceHolder.Callback ─────────────────────────────────────────────

    /**
     * Called when the [Surface] is ready. Creates and starts a
     * [TabletDisplaySession] bound to this surface.
     */
    override fun surfaceCreated(holder: SurfaceHolder) {
        val surface = holder.surface
        val sid     = intent.getStringExtra(EXTRA_SESSION_ID) ?: generateSessionId()

        val newSession = TabletDisplaySession(
            sessionId     = sid,
            channel       = channel,
            outputSurface = surface,
        )
        session = newSession

        activityScope.launch {
            newSession.start()
        }
    }

    /**
     * Called when the surface size changes (e.g. rotation). The H.264 stream
     * resolution is set by the Windows host and does not change mid-session,
     * so no action is needed here.
     */
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // No action required — stream resolution is fixed per session
    }

    /**
     * Called when the surface is about to be destroyed. Stops the
     * [TabletDisplaySession] so the decoder releases its reference to the
     * surface before it disappears.
     */
    override fun surfaceDestroyed(holder: SurfaceHolder) {
        val currentSession = session ?: return
        session = null
        activityScope.launch {
            currentSession.stop()
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private fun generateSessionId(): String =
        "tablet-display-${System.currentTimeMillis()}"

    companion object {
        /** Optional Intent extra: pre-assigned session ID from the host. */
        const val EXTRA_SESSION_ID = "com.airbridge.app.extra.SESSION_ID"
    }
}
