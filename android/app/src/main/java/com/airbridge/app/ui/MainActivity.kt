package com.airbridge.app.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.airbridge.app.ui.navigation.AirBridgeNavHost
import com.airbridge.app.ui.navigation.Screen
import com.airbridge.app.ui.theme.AirBridgeTheme
import dagger.hilt.android.AndroidEntryPoint

/**
 * Single-activity host for the AirBridge Compose UI.
 *
 * Hosts the bottom navigation bar and delegates rendering to [AirBridgeNavHost].
 * All Hilt ViewModels are resolved per-composable via [hiltViewModel()].
 */
@AndroidEntryPoint
class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            AirBridgeTheme {
                val navController = rememberNavController()
                Scaffold(
                    bottomBar = {
                        NavigationBar {
                            val navBackStackEntry by navController.currentBackStackEntryAsState()
                            val currentRoute = navBackStackEntry?.destination?.route

                            listOf(
                                Triple(Screen.Devices, Icons.Default.Devices, "Devices"),
                                Triple(Screen.Transfer, Icons.Default.SwapHoriz, "Transfer"),
                                Triple(Screen.Mirror, Icons.Default.Cast, "Mirror"),
                                Triple(Screen.Settings, Icons.Default.Settings, "Settings"),
                            ).forEach { (screen, icon, label) ->
                                NavigationBarItem(
                                    icon = { Icon(icon, contentDescription = label) },
                                    label = { Text(label) },
                                    selected = currentRoute == screen.route,
                                    onClick = {
                                        navController.navigate(screen.route) {
                                            popUpTo(navController.graph.startDestinationId) {
                                                saveState = true
                                            }
                                            launchSingleTop = true
                                            restoreState = true
                                        }
                                    },
                                )
                            }
                        }
                    },
                ) { innerPadding ->
                    AirBridgeNavHost(
                        navController = navController,
                        modifier = Modifier.padding(innerPadding),
                    )
                }
            }
        }
    }
}
