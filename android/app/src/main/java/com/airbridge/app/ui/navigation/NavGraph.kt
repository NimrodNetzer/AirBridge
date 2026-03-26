package com.airbridge.app.ui.navigation

/**
 * Sealed hierarchy of navigation destinations in the app.
 * Each object/class carries the route string used by the NavHost.
 */
sealed class Screen(val route: String) {
    /** Device discovery and listing screen — the start destination. */
    object Devices : Screen("devices")

    /** Pairing handshake screen; requires the target [deviceId] as a path argument. */
    object Pairing : Screen("pairing/{deviceId}") {
        /** Constructs the concrete route by substituting [deviceId]. */
        fun createRoute(deviceId: String) = "pairing/$deviceId"
    }

    /** Active and completed file transfer list. */
    object Transfer : Screen("transfer")

    /** Phone-mirror and tablet-display controls. */
    object Mirror : Screen("mirror")

    /** App settings — paired devices, accessibility, about. */
    object Settings : Screen("settings")
}
