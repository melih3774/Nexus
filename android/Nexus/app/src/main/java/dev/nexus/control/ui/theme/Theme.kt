package dev.nexus.control.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat

/**
 * Nexus is a control panel for one machine, so it is dark by default and stays dark: a status
 * surface reads better against ink, and the accent is reserved for state that matters — connected,
 * armed, refused — rather than spent on decoration.
 *
 * Dynamic colour is deliberately not used. The accent here carries meaning, and letting the
 * wallpaper choose it would make "green" and "amber" mean whatever the wallpaper felt like.
 */
private val Ink = Color(0xFF0B0F14)
private val Surface = Color(0xFF12181F)
private val SurfaceHigh = Color(0xFF1B232C)
private val Signal = Color(0xFF2FD8A8)
private val SignalDim = Color(0xFF12513F)
private val Warn = Color(0xFFF0B429)
private val Alarm = Color(0xFFE5484D)
private val TextPrimary = Color(0xFFE8EDF2)
private val TextMuted = Color(0xFF8A97A6)

private val NexusDark = darkColorScheme(
    primary = Signal,
    onPrimary = Ink,
    primaryContainer = SignalDim,
    onPrimaryContainer = Signal,
    secondary = Warn,
    onSecondary = Ink,
    error = Alarm,
    onError = Ink,
    background = Ink,
    onBackground = TextPrimary,
    surface = Surface,
    onSurface = TextPrimary,
    surfaceVariant = SurfaceHigh,
    onSurfaceVariant = TextMuted,
    outline = Color(0xFF2A343F),
)

/**
 * Light mode exists so the app is usable outdoors, not as a second design. It is the same palette
 * with the ground inverted.
 */
private val NexusLight = lightColorScheme(
    primary = Color(0xFF0E7C60),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFB8EFDF),
    onPrimaryContainer = Color(0xFF042B21),
    secondary = Color(0xFF8A6100),
    error = Color(0xFFB3261E),
    background = Color(0xFFF7F9FB),
    onBackground = Color(0xFF10161C),
    surface = Color.White,
    onSurface = Color(0xFF10161C),
    surfaceVariant = Color(0xFFE7ECF1),
    onSurfaceVariant = Color(0xFF4A5561),
    outline = Color(0xFFC3CCD6),
)

/** Status values are monospaced so a changing reading does not shift the layout under a thumb. */
val MonoStyle = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 13.sp)

private val NexusTypography = Typography().run {
    copy(
        displaySmall = displaySmall.copy(fontWeight = FontWeight.Light, letterSpacing = 6.sp),
        titleLarge = titleLarge.copy(fontWeight = FontWeight.SemiBold),
        labelLarge = labelLarge.copy(fontWeight = FontWeight.Medium, letterSpacing = 0.5.sp),
    )
}

@Composable
fun NexusTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val colors = if (darkTheme) NexusDark else NexusLight
    val view = LocalView.current

    if (!view.isInEditMode) {
        // The activity draws edge to edge, so the bars are transparent and the app paints behind
        // them; all that is left to decide is whether their icons should be dark.
        SideEffect {
            val window = (view.context as Activity).window
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }

    MaterialTheme(colorScheme = colors, typography = NexusTypography, content = content)
}
