package com.airbridge.app.mirror

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.AccessibilityServiceInfo
import android.view.accessibility.AccessibilityEvent

/**
 * Accessibility service that enables touch and key injection for the mirror input-relay feature.
 *
 * The service must be enabled by the user in **Settings → Accessibility → AirBridge**.
 * Once connected, it exposes itself via the companion [instance] property so that
 * [InputInjector] can route gestures and global actions through it.
 *
 * **Permissions required (declared in `AndroidManifest.xml`):**
 * - `android.permission.BIND_ACCESSIBILITY_SERVICE`
 *
 * **Service configuration** is declared in `res/xml/accessibility_service_config.xml`:
 * - `accessibilityEventTypes="typeAllMask"` — required to keep the service alive
 * - `canPerformGestures="true"` — required for [dispatchGesture]
 * - `canRetrieveWindowContent="false"` — we do not need screen content; minimises permission scope
 *
 * The service emits no telemetry and does not read screen content.
 */
class AirBridgeAccessibilityService : AccessibilityService() {

    // ── Lifecycle ────────────────────────────────────────────────────────────

    override fun onServiceConnected() {
        super.onServiceConnected()
        // Publish this instance so InputInjector can reach it
        instance = this

        // Configure at runtime as a belt-and-suspenders measure in addition to the XML config
        serviceInfo = serviceInfo.apply {
            eventTypes = AccessibilityEvent.TYPES_ALL_MASK
            feedbackType = AccessibilityServiceInfo.FEEDBACK_GENERIC
            flags = AccessibilityServiceInfo.FLAG_REQUEST_TOUCH_EXPLORATION_MODE or
                    AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        }
    }

    override fun onInterrupt() {
        // No persistent state to clean up
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // We do not consume accessibility events — this service is used for injection only
    }

    override fun onDestroy() {
        super.onDestroy()
        // Clear the global reference so InputInjector stops attempting injection
        if (instance === this) instance = null
    }

    // ── Companion ────────────────────────────────────────────────────────────

    companion object {
        /**
         * The currently connected service instance, or `null` if the user has not yet
         * granted accessibility permission or the service has been destroyed.
         *
         * Accessed from [InputInjector]; do not hold a reference beyond a single call site.
         */
        @Volatile
        var instance: AirBridgeAccessibilityService? = null
            internal set
    }
}
