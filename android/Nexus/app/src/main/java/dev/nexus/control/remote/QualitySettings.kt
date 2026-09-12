package dev.nexus.control.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import dev.nexus.control.protocol.MediaQualityView
import dev.nexus.control.protocol.SetMediaQualityParams

/**
 * The choices, as the PC spells them on the wire and as the owner reads them.
 *
 * Kept as plain lists rather than enums mirroring the PC's: the phone never reasons about what a
 * profile means — the PC resolves it and says what it resolved to — so all this side needs is the
 * vocabulary and a label for each word.
 */
object QualityOptions {
    val profiles = listOf(
        "auto" to "Auto",
        "smooth" to "Smooth",
        "balanced" to "Balanced",
        "quality" to "Quality",
        "custom" to "Custom",
    )

    val resolutions = listOf(
        "auto" to "Auto",
        "360p" to "360p",
        "720p" to "720p",
        "1080p" to "1080p",
        "native" to "Native",
    )

    val customHeights = listOf(360, 540, 720, 1080)

    fun describeProfile(profile: String): String = when (profile) {
        "auto" -> "Adapts to what the link and the encoder are managing."
        "smooth" -> "Frame rate and latency first."
        "balanced" -> "Readable text at a steady frame rate."
        "quality" -> "Small text and detail first, at fewer frames."
        else -> "Exactly the numbers below."
    }
}

/**
 * One target's streaming settings: a profile, a resolution, and — for Custom — the three numbers.
 *
 * **Deliberately small.** Five profiles and five resolutions cover what an owner actually wants to
 * say ("smoother", "sharper", "less data"), and Custom is there for the rest. The line under the
 * chips shows what the PC resolved the choice to, in pixels, frames and megabits, because "Auto" is
 * not an answer to "what am I about to get".
 *
 * Every change is sent as it is made; the running stream re-plans on its next frame, so there is no
 * Apply button and no restart.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun QualitySettingsPanel(
    title: String,
    view: MediaQualityView,
    encoder: String,
    onChange: (SetMediaQualityParams) -> Unit,
    modifier: Modifier = Modifier,
    showResolution: Boolean = true,
) {
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(title, style = MaterialTheme.typography.titleSmall)

        Text("Profile", style = MaterialTheme.typography.labelMedium)

        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            for ((wire, label) in QualityOptions.profiles) {
                FilterChip(
                    selected = view.profile == wire,
                    onClick = { onChange(SetMediaQualityParams(target = "", profile = wire)) },
                    label = { Text(label) },
                )
            }
        }

        Text(
            QualityOptions.describeProfile(view.profile),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        if (showResolution && view.profile != "custom") {
            Text("Resolution", style = MaterialTheme.typography.labelMedium)

            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for ((wire, label) in QualityOptions.resolutions) {
                    FilterChip(
                        selected = view.resolution == wire,
                        onClick = { onChange(SetMediaQualityParams(target = "", resolution = wire)) },
                        label = { Text(label) },
                    )
                }
            }

            if (view.profile == "auto" && view.resolution != "auto") {
                Text(
                    "A resolution you pick stays put; Auto only adjusts frame rate and bitrate.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        if (view.profile == "custom") {
            CustomControls(view, onChange)
        }

        Spacer(Modifier.height(2.dp))

        Text(
            "Now: ${view.plannedWidth}×${view.plannedHeight} · ${view.plannedFps} fps · " +
                "%.1f Mbit/s".format(view.plannedBitrateKbps / 1000.0),
            style = MaterialTheme.typography.bodySmall,
        )

        if (encoder.isNotBlank()) {
            Text(
                "Encoder: $encoder",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun CustomControls(view: MediaQualityView, onChange: (SetMediaQualityParams) -> Unit) {
    Text("Height", style = MaterialTheme.typography.labelMedium)

    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        for (height in QualityOptions.customHeights) {
            FilterChip(
                selected = view.customHeight == height,
                onClick = { onChange(SetMediaQualityParams(target = "", customHeight = height)) },
                label = { Text("${height}p") },
            )
        }
    }

    // Sliders hold their position locally while dragged and send once on release: a request per
    // pixel of drag would rebuild the encoder dozens of times for one decision.
    var fps by remember { mutableFloatStateOf(view.customFps.toFloat()) }
    var kbps by remember { mutableFloatStateOf(view.customBitrateKbps.toFloat()) }

    LaunchedEffect(view.customFps, view.customBitrateKbps) {
        fps = view.customFps.toFloat()
        kbps = view.customBitrateKbps.toFloat()
    }

    LabelledSlider(
        label = "Frame rate",
        value = "${fps.toInt()} fps",
        position = fps,
        range = 5f..60f,
        onMove = { fps = it },
        onDone = { onChange(SetMediaQualityParams(target = "", customFps = fps.toInt())) },
    )

    LabelledSlider(
        label = "Bitrate",
        value = "%.1f Mbit/s".format(kbps / 1000f),
        position = kbps,
        range = 300f..20_000f,
        onMove = { kbps = (it / 100f).toInt() * 100f },
        onDone = { onChange(SetMediaQualityParams(target = "", customBitrateKbps = kbps.toInt())) },
    )
}

@Composable
private fun LabelledSlider(
    label: String,
    value: String,
    position: Float,
    range: ClosedFloatingPointRange<Float>,
    onMove: (Float) -> Unit,
    onDone: () -> Unit,
) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Text(value, style = MaterialTheme.typography.labelMedium)
    }

    Slider(value = position, onValueChange = onMove, valueRange = range, onValueChangeFinished = onDone)
}

/** A compact "Quality" affordance: a button that reveals the panel. Used where space is tight. */
@Composable
fun QualityToggle(expanded: Boolean, onToggle: () -> Unit) {
    TextButton(onClick = onToggle) { Text(if (expanded) "Hide quality" else "Quality") }
}
