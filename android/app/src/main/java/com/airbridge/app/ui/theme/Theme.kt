package com.airbridge.app.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

private val DarkColors = darkColorScheme(
    primary = Color(0xFF6DD3FF),
    secondary = Color(0xFF90CAF9),
)

private val LightColors = lightColorScheme(
    primary = Color(0xFF0078D4),
    secondary = Color(0xFF1565C0),
)

/**
 * AirBridge Material 3 theme.
 *
 * Uses dynamic color on Android 12+ (API 31+) and falls back to the brand
 * palette on older devices. Dark-by-default for the futuristic aesthetic.
 *
 * @param darkTheme    Whether to apply the dark color scheme.
 * @param dynamicColor Whether to use Monet-generated dynamic color (API 31+).
 * @param content      The composable tree to theme.
 */
@Composable
fun AirBridgeTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit,
) {
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
            val context = LocalContext.current
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        }
        darkTheme -> DarkColors
        else -> LightColors
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content,
    )
}
