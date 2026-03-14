package com.fccmiddleware.edge.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// Puma brand colours
val PumaGreen = Color(0xFF007A33)
val PumaRed = Color(0xFFE30613)
val PumaDarkRed = Color(0xFFB8050F)

// Status colours
val StatusGreen = Color(0xFF2E7D32)
val StatusBlue = Color(0xFF1565C0)
val StatusYellow = Color(0xFFF9A825)
val StatusRed = Color(0xFFC62828)
val StatusDispensing = Color(0xFFF57F17)

// Text colours
val TextPrimary = Color(0xFF1A1A1A)
val TextLabel = Color(0xFF4A4A4A)
val TextGray = Color(0xFF9E9E9E)
val TextOverride = Color(0xFFFF6F00)

// Divider
val DividerColor = Color(0xFFE0E0E0)

private val FccColorScheme = lightColorScheme(
    primary = PumaRed,
    secondary = PumaGreen,
    background = Color.White,
    surface = Color.White,
)

@Composable
fun FccEdgeAgentTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = FccColorScheme,
        content = content,
    )
}
