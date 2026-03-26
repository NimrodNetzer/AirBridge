package com.airbridge.app.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Tablet
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavController
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.ui.viewmodels.MirrorUiState
import com.airbridge.app.ui.viewmodels.MirrorViewModel

/**
 * Mirror screen — lets the user start a phone-window mirror or a tablet second-monitor session.
 *
 * When a session is active an animated bottom banner shows the mode and a Stop button.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MirrorScreen(
    navController: NavController,
    viewModel: MirrorViewModel = hiltViewModel(),
) {
    val mirrorState by viewModel.mirrorState.collectAsStateWithLifecycle()

    // Placeholder device for demonstration — in production this comes from DeviceRegistry.
    val placeholderDevice = DeviceInfo(
        deviceId = "placeholder",
        deviceName = "Windows PC",
        deviceType = DeviceType.WINDOWS_PC,
        ipAddress = "192.168.1.1",
        port = 5500,
        isPaired = true,
    )

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Text(
                        text = "Mirror",
                        style = MaterialTheme.typography.headlineMedium,
                    )
                },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            // Active session banner
            AnimatedVisibility(
                visible = mirrorState is MirrorUiState.Active || mirrorState is MirrorUiState.Starting,
                enter = slideInVertically { -it },
                exit = slideOutVertically { -it },
            ) {
                ActiveSessionBanner(
                    mirrorState = mirrorState,
                    onStop = { viewModel.stopMirror() },
                )
            }

            // Error banner
            if (mirrorState is MirrorUiState.Error) {
                Surface(
                    modifier = Modifier.fillMaxWidth(),
                    color = MaterialTheme.colorScheme.errorContainer,
                    shape = RoundedCornerShape(12.dp),
                ) {
                    Text(
                        text = (mirrorState as MirrorUiState.Error).message,
                        modifier = Modifier.padding(16.dp),
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }

            // Mode selection cards
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                MirrorModeCard(
                    modifier = Modifier.weight(1f),
                    icon = Icons.Default.PhoneAndroid,
                    title = "Phone Window",
                    description = "Mirror your phone on PC",
                    enabled = mirrorState is MirrorUiState.Idle || mirrorState is MirrorUiState.Error,
                    onStart = { viewModel.startPhoneWindow(placeholderDevice) },
                )
                MirrorModeCard(
                    modifier = Modifier.weight(1f),
                    icon = Icons.Default.Tablet,
                    title = "Tablet Display",
                    description = "Use as second monitor",
                    enabled = mirrorState is MirrorUiState.Idle || mirrorState is MirrorUiState.Error,
                    onStart = { viewModel.startTabletDisplay(placeholderDevice) },
                )
            }

            Spacer(Modifier.height(8.dp))

            Text(
                text = "Both devices must be on the same Wi-Fi network.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }
}

@Composable
private fun MirrorModeCard(
    modifier: Modifier = Modifier,
    icon: ImageVector,
    title: String,
    description: String,
    enabled: Boolean,
    onStart: () -> Unit,
) {
    OutlinedCard(
        modifier = modifier,
        shape = RoundedCornerShape(16.dp),
    ) {
        Column(
            modifier = Modifier.padding(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(40.dp),
            )
            Text(
                text = title,
                style = MaterialTheme.typography.titleSmall,
                textAlign = TextAlign.Center,
            )
            Text(
                text = description,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )
            FilledTonalButton(
                onClick = onStart,
                enabled = enabled,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("Start")
            }
        }
    }
}

@Composable
private fun ActiveSessionBanner(
    mirrorState: MirrorUiState,
    onStop: () -> Unit,
) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.primaryContainer,
        ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (mirrorState is MirrorUiState.Starting) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    strokeWidth = 2.dp,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            } else {
                Icon(
                    imageVector = Icons.Default.Cast,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = when (mirrorState) {
                        is MirrorUiState.Active -> mirrorState.mode
                        else -> "Starting…"
                    },
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
                Text(
                    text = when (mirrorState) {
                        is MirrorUiState.Active -> "Session active"
                        else -> "Please wait…"
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
            Button(
                onClick = onStop,
                colors = ButtonDefaults.buttonColors(
                    containerColor = MaterialTheme.colorScheme.error,
                ),
            ) {
                Icon(Icons.Default.Stop, contentDescription = "Stop", modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(4.dp))
                Text("Stop")
            }
        }
    }
}
