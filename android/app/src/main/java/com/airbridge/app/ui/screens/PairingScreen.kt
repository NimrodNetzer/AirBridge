package com.airbridge.app.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Error
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavController
import com.airbridge.app.ui.navigation.Screen
import com.airbridge.app.ui.viewmodels.PairingState
import com.airbridge.app.ui.viewmodels.PairingViewModel
import kotlinx.coroutines.delay

/**
 * Pairing screen — drives the TOFU handshake and displays the 6-digit PIN
 * the user must confirm on both devices.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairingScreen(
    navController: NavController,
    deviceId: String,
    viewModel: PairingViewModel = hiltViewModel(),
) {
    val pairingState by viewModel.pairingState.collectAsStateWithLifecycle()

    LaunchedEffect(deviceId) {
        viewModel.startPairing(deviceId)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Pair Device") },
                navigationIcon = {
                    IconButton(
                        onClick = {
                            viewModel.cancel()
                            navController.popBackStack()
                        },
                    ) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "Back",
                        )
                    }
                },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(32.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            when (val state = pairingState) {
                is PairingState.Idle -> {
                    CircularProgressIndicator()
                    Spacer(Modifier.height(16.dp))
                    Text(
                        text = "Initializing…",
                        style = MaterialTheme.typography.bodyLarge,
                    )
                }

                is PairingState.Connecting -> {
                    CircularProgressIndicator()
                    Spacer(Modifier.height(16.dp))
                    Text(
                        text = "Connecting…",
                        style = MaterialTheme.typography.bodyLarge,
                    )
                }

                is PairingState.WaitingForPin -> {
                    Text(
                        text = "Confirm PIN",
                        style = MaterialTheme.typography.headlineSmall,
                    )
                    Spacer(Modifier.height(24.dp))
                    Text(
                        text = state.pin.chunked(1).joinToString("  "),
                        style = MaterialTheme.typography.displayMedium.copy(
                            fontFamily = FontFamily.Monospace,
                            letterSpacing = 4.sp,
                        ),
                        color = MaterialTheme.colorScheme.primary,
                    )
                    Spacer(Modifier.height(16.dp))
                    Text(
                        text = "Confirm this PIN on your Windows PC",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center,
                    )
                }

                is PairingState.Success -> {
                    Icon(
                        imageVector = Icons.Default.CheckCircle,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(64.dp),
                    )
                    Spacer(Modifier.height(16.dp))
                    Text(
                        text = "Paired!",
                        style = MaterialTheme.typography.headlineSmall,
                    )
                    LaunchedEffect(Unit) {
                        delay(800)
                        navController.navigate(Screen.Transfer.route) {
                            popUpTo(Screen.Devices.route)
                        }
                    }
                }

                is PairingState.Failed -> {
                    Icon(
                        imageVector = Icons.Default.Error,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.error,
                        modifier = Modifier.size(48.dp),
                    )
                    Spacer(Modifier.height(16.dp))
                    Text(
                        text = "Pairing Failed",
                        style = MaterialTheme.typography.headlineSmall,
                    )
                    Text(
                        text = state.message,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.padding(top = 8.dp),
                    )
                    Spacer(Modifier.height(24.dp))
                    Button(onClick = { navController.popBackStack() }) {
                        Text("Go Back")
                    }
                }
            }
        }
    }
}
