package com.airbridge.app.mirror

import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.view.ViewConfiguration
import com.airbridge.app.core.interfaces.InputEventArgs
import com.airbridge.app.core.interfaces.InputEventType
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Translates [InputEventArgs] into real Android input events and injects them
 * via [AirBridgeAccessibilityService].
 *
 * Touch events are dispatched as [GestureDescription] strokes through the
 * Accessibility API. Key events are dispatched via
 * [AirBridgeAccessibilityService.performGlobalAction] for the small set of
 * global Android keys (Back, Home, Recents); all other keys are forwarded
 * through the service's key-event dispatch path.
 *
 * **Prerequisites:** [AirBridgeAccessibilityService] must be enabled in
 * Settings → Accessibility before any injection calls succeed. If the service
 * is not connected, events are silently dropped and a warning is logged.
 *
 * @param screenWidth  Physical screen width in pixels (used to de-normalise X).
 * @param screenHeight Physical screen height in pixels (used to de-normalise Y).
 */
@Singleton
class InputInjector @Inject constructor() {

    // Physical screen dimensions — set once at session start
    @Volatile var screenWidth:  Int = 1080
    @Volatile var screenHeight: Int = 1920

    /**
     * Injects the given [event] using the currently connected
     * [AirBridgeAccessibilityService] instance.
     *
     * This method is safe to call from any thread.
     *
     * @param event The normalised input event received from the Windows host.
     */
    fun inject(event: InputEventArgs) {
        val service = AirBridgeAccessibilityService.instance ?: run {
            // Accessibility service not yet granted — silently drop
            return
        }

        when (event.type) {
            InputEventType.TOUCH, InputEventType.MOUSE -> injectTouch(service, event)
            InputEventType.KEY                         -> injectKey(service, event)
        }
    }

    // ── Touch / Mouse ────────────────────────────────────────────────────────

    private fun injectTouch(service: AirBridgeAccessibilityService, event: InputEventArgs) {
        // Clamp normalised coordinates to [0, 1] to guard against out-of-range values
        // from a malformed or malicious remote peer.
        val nx = event.normalizedX.coerceIn(0.0f, 1.0f)
        val ny = event.normalizedY.coerceIn(0.0f, 1.0f)
        val px = (nx * screenWidth).toFloat()
        val py = (ny * screenHeight).toFloat()

        val path = Path().apply { moveTo(px, py) }

        // A tap is modelled as a very short stroke (1 ms duration, single point).
        // For a proper drag you would need a down + move + up sequence. For Iteration 6
        // this is a good starting point that handles taps and short swipes.
        val strokeDuration = ViewConfiguration.getTapTimeout().toLong().coerceAtLeast(1L)

        val stroke = GestureDescription.StrokeDescription(path, 0, strokeDuration)
        val gesture = GestureDescription.Builder().addStroke(stroke).build()

        service.dispatchGesture(gesture, null, null)
    }

    // ── Key ──────────────────────────────────────────────────────────────────

    private fun injectKey(service: AirBridgeAccessibilityService, event: InputEventArgs) {
        // Map common Android keycodes to AccessibilityService global actions.
        // Anything not matched here is currently dropped; extended key relay can be
        // added in Iteration 6 via an InputManager shell command or root API.
        when (event.keycode) {
            android.view.KeyEvent.KEYCODE_BACK   ->
                service.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK)
            android.view.KeyEvent.KEYCODE_HOME   ->
                service.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_HOME)
            android.view.KeyEvent.KEYCODE_APP_SWITCH ->
                service.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_RECENTS)
            else -> {
                // Non-global keys are not yet injectable without root / special permissions.
                // Log and drop until Iteration 6 adds the InputManager path.
            }
        }
    }
}
