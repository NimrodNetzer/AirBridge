package com.airbridge.app.ui.screens

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AssistChipDefaults
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavController
import com.airbridge.app.core.interfaces.TransferState
import com.airbridge.app.ui.viewmodels.TransferSessionUiState
import com.airbridge.app.ui.viewmodels.TransferViewModel

/**
 * File Transfer screen.
 *
 * Shows:
 * - A "Reconnecting…" banner while the transport layer is attempting to reconnect.
 * - A "Connection failed" banner when all reconnect attempts are exhausted.
 * - The list of active and completed sessions with real-time progress bars,
 *   transfer speed, ETA, and a Retry button for failed transfers.
 * - A FAB that opens the system file-picker to initiate a new send.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TransferScreen(
    navController: NavController,
    viewModel: TransferViewModel = hiltViewModel(),
) {
    val sessions            by viewModel.activeSessions.collectAsStateWithLifecycle()
    val connectedDevice     by viewModel.connectedDeviceId.collectAsStateWithLifecycle()
    val reconnectState      by viewModel.reconnectState.collectAsStateWithLifecycle()
    val connectionError     by viewModel.connectionErrorMessage.collectAsStateWithLifecycle()

    val filePicker = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.GetContent(),
    ) { uri ->
        uri?.let { viewModel.sendFile(it) }
    }

    // On API 26-28 we need WRITE_EXTERNAL_STORAGE to save received files to Downloads/AirBridge.
    // API 29+ uses scoped storage and needs no explicit permission.
    val writePermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission(),
    ) { /* permission result — file picker opens regardless; receive will fail gracefully if denied */ }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(
                            text = "File Transfer",
                            style = MaterialTheme.typography.headlineMedium,
                        )
                        Text(
                            text = if (connectedDevice != null)
                                "Connected to: $connectedDevice"
                            else
                                "No device connected",
                            style = MaterialTheme.typography.bodySmall,
                            color = if (connectedDevice != null)
                                MaterialTheme.colorScheme.primary
                            else
                                MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                },
            )
        },
        floatingActionButton = {
            FloatingActionButton(onClick = {
                // Request WRITE_EXTERNAL_STORAGE on API < 29 so received files can be saved.
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
                    writePermissionLauncher.launch(Manifest.permission.WRITE_EXTERNAL_STORAGE)
                }
                filePicker.launch("*/*")
            }) {
                Icon(Icons.Default.Add, contentDescription = "Send file")
            }
        },
    ) { padding ->
        Column(modifier = Modifier.padding(padding)) {
            // ── Reconnecting banner ──────────────────────────────────────────
            AnimatedVisibility(
                visible = reconnectState != null,
                enter = expandVertically() + fadeIn(),
                exit = shrinkVertically() + fadeOut(),
            ) {
                reconnectState?.let { state ->
                    ReconnectingBanner(
                        attempt = state.attempt,
                        maxAttempts = state.maxAttempts,
                    )
                }
            }

            // ── Connection-failed error banner ───────────────────────────────
            AnimatedVisibility(
                visible = connectionError != null,
                enter = expandVertically() + fadeIn(),
                exit = shrinkVertically() + fadeOut(),
            ) {
                connectionError?.let { message ->
                    ConnectionErrorBanner(
                        message = message,
                        onDismiss = { viewModel.dismissConnectionError() },
                    )
                }
            }

            if (sessions.isEmpty()) {
                EmptyTransferPlaceholder(modifier = Modifier.weight(1f))
            } else {
                LazyColumn(
                    modifier = Modifier.weight(1f),
                    contentPadding = PaddingValues(16.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    items(sessions, key = { it.sessionId }) { session ->
                        TransferSessionCard(
                            session = session,
                            onRetry = { viewModel.retryTransfer(session.sessionId) },
                        )
                    }
                }
            }
        }
    }
}

// ── Status banners ─────────────────────────────────────────────────────────────

@Composable
private fun ReconnectingBanner(attempt: Int, maxAttempts: Int) {
    Surface(
        color = MaterialTheme.colorScheme.secondaryContainer,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            modifier = Modifier
                .padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            CircularProgressIndicator(
                modifier = Modifier.size(16.dp),
                strokeWidth = 2.dp,
                color = MaterialTheme.colorScheme.onSecondaryContainer,
            )
            Text(
                text = "Reconnecting… (attempt $attempt of $maxAttempts)",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSecondaryContainer,
                modifier = Modifier.weight(1f),
            )
        }
    }
}

@Composable
private fun ConnectionErrorBanner(message: String, onDismiss: () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            modifier = Modifier
                .padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Icon(
                imageVector = Icons.Default.ErrorOutline,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onErrorContainer,
                modifier = Modifier.size(18.dp),
            )
            Text(
                text = message,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onErrorContainer,
                modifier = Modifier.weight(1f),
            )
            FilledTonalButton(
                onClick = onDismiss,
                colors = ButtonDefaults.filledTonalButtonColors(
                    containerColor = MaterialTheme.colorScheme.error,
                    contentColor = MaterialTheme.colorScheme.onError,
                ),
            ) {
                Text("Dismiss", style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

// ── Session card ───────────────────────────────────────────────────────────────

@Composable
private fun TransferSessionCard(session: TransferSessionUiState, onRetry: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceVariant,
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    text = session.fileName,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                AssistChip(
                    onClick = {},
                    label = { Text(session.state.label()) },
                    colors = AssistChipDefaults.assistChipColors(
                        containerColor = session.state.chipColor(),
                    ),
                )
            }

            Spacer(Modifier.height(8.dp))

            LinearProgressIndicator(
                progress = {
                    if (session.totalBytes > 0)
                        (session.transferredBytes.toFloat() / session.totalBytes.toFloat())
                    else 0f
                },
                modifier = Modifier.fillMaxWidth(),
                trackColor = MaterialTheme.colorScheme.surfaceVariant,
            )

            Spacer(Modifier.height(4.dp))

            // Bytes transferred / total
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    text = "${session.transferredBytes.toHumanReadable()} / ${session.totalBytes.toHumanReadable()}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                // Speed and ETA — only shown while the transfer is active
                if (session.state == TransferState.ACTIVE) {
                    val speedText = session.speedBytesPerSec?.let { "${it.toHumanReadable()}/s" }
                    val etaText   = session.etaSeconds?.let { formatEta(it) }
                    val detail = listOfNotNull(speedText, etaText?.let { "ETA $it" })
                        .joinToString("  ·  ")
                    if (detail.isNotEmpty()) {
                        Text(
                            text = detail,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            // Error message + Retry button for failed transfers
            if (session.state == TransferState.FAILED) {
                Spacer(Modifier.height(8.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    Text(
                        text = session.errorMessage ?: "Transfer failed.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.width(8.dp))
                    Button(
                        onClick = onRetry,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = MaterialTheme.colorScheme.error,
                        ),
                    ) {
                        Text("Retry", style = MaterialTheme.typography.labelMedium)
                    }
                }
            }
        }
    }
}

@Composable
private fun EmptyTransferPlaceholder(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Icon(
            imageVector = Icons.Default.SwapHoriz,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(64.dp),
        )
        Spacer(Modifier.height(16.dp))
        Text(
            text = "No transfers yet. Tap + to send a file.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

// ── Helpers ────────────────────────────────────────────────────────────────────

private fun TransferState.label(): String = when (this) {
    TransferState.PENDING   -> "Pending"
    TransferState.ACTIVE    -> "Active"
    TransferState.PAUSED    -> "Paused"
    TransferState.COMPLETED -> "Complete"
    TransferState.FAILED    -> "Failed"
    TransferState.CANCELLED -> "Cancelled"
}

@Composable
private fun TransferState.chipColor(): Color = when (this) {
    TransferState.ACTIVE    -> MaterialTheme.colorScheme.primaryContainer
    TransferState.COMPLETED -> Color(0xFF2E7D32)
    TransferState.FAILED, TransferState.CANCELLED -> MaterialTheme.colorScheme.errorContainer
    else -> MaterialTheme.colorScheme.surfaceVariant
}

private fun Long.toHumanReadable(): String = when {
    this >= 1_073_741_824L -> "%.1f GB".format(this / 1_073_741_824.0)
    this >= 1_048_576L     -> "%.1f MB".format(this / 1_048_576.0)
    this >= 1_024L         -> "%.1f KB".format(this / 1_024.0)
    else                   -> "$this B"
}

private fun formatEta(seconds: Long): String = when {
    seconds < 60   -> "${seconds}s"
    seconds < 3600 -> "${seconds / 60}m ${seconds % 60}s"
    else           -> "${seconds / 3600}h ${(seconds % 3600) / 60}m"
}
