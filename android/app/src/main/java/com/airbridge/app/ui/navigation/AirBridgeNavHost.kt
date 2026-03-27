package com.airbridge.app.ui.navigation

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.navigation.NavHostController
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.navArgument
import com.airbridge.app.ui.screens.DevicesScreen
import com.airbridge.app.ui.screens.MirrorScreen
import com.airbridge.app.ui.screens.PairingScreen
import com.airbridge.app.ui.screens.SettingsScreen
import com.airbridge.app.ui.screens.TransferScreen

/**
 * Root navigation host that wires every [Screen] destination to its composable.
 *
 * @param navController Caller-provided [NavHostController].
 * @param modifier      Optional [Modifier] forwarded to [NavHost].
 */
@Composable
fun AirBridgeNavHost(navController: NavHostController, modifier: Modifier = Modifier) {
    NavHost(
        navController = navController,
        startDestination = Screen.Devices.route,
        modifier = modifier,
    ) {
        composable(Screen.Devices.route) {
            DevicesScreen(navController)
        }
        composable(
            route = Screen.Pairing.route,
            arguments = listOf(navArgument("deviceId") { type = NavType.StringType }),
        ) { backStackEntry ->
            PairingScreen(
                navController = navController,
                deviceId = backStackEntry.arguments?.getString("deviceId") ?: "",
            )
        }
        composable(Screen.Transfer.route) {
            TransferScreen(navController)
        }
        composable(Screen.Mirror.route) {
            MirrorScreen(navController)
        }
        composable(Screen.Settings.route) {
            SettingsScreen(navController)
        }
    }
}
