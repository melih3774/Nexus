package dev.nexus.control.security

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.viewinterop.AndroidView
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import dev.nexus.control.protocol.SetMediaQualityParams
import dev.nexus.control.remote.QualitySettingsPanel
import dev.nexus.control.remote.QualityToggle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import dev.nexus.control.protocol.LiveSensorKind
import dev.nexus.control.ui.Panel

/**
 * Live camera and microphone, in the Security area.
 *
 * **The card says what the PC is showing.** While a sensor is live the PC draws a badge on its own
 * screen and turns its tray icon red; this repeats that here so the owner is never in any doubt that
 * anyone standing at the PC can see the monitoring is happening. It is a deliberate design choice
 * that this cannot be turned off from either end.
 */
@Composable
fun LiveMonitorCard(
    state: LiveMonitorUiState,
    onToggle: (LiveSensorKind) -> Unit,
    onStopAll: () -> Unit,
    modifier: Modifier = Modifier,
    onCameraSurface: (Surface?) -> Unit = {},
    onCameraQuality: (SetMediaQualityParams) -> Unit = {},
) {
    var qualityVisible by remember { mutableStateOf(false) }
    val currentOnSurface by rememberUpdatedState(onCameraSurface)

    Panel(title = "Live monitor", modifier = modifier) {
        Text(
            "See and hear the room your PC is in. Nothing is recorded — these frames are shown and "
                + "discarded.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        Spacer(Modifier.height(12.dp))

        if (state.anyLive) {
            LiveBanner(state)
            Spacer(Modifier.height(12.dp))
        }

        if (state.camera) {
            // Shaped to the stream itself once it is known, so a 16:9 webcam is not letterboxed into
            // a 4:3 box and a portrait one is not stretched.
            val ratio = if (state.cameraWidth > 0 && state.cameraHeight > 0) {
                state.cameraWidth.toFloat() / state.cameraHeight
            } else {
                16f / 9f
            }

            Box(
                Modifier
                    .fillMaxWidth()
                    .aspectRatio(ratio)
                    .clip(RoundedCornerShape(12.dp))
                    .background(Color.Black),
                contentAlignment = Alignment.Center,
            ) {
                // The video surface is always there while the camera is on, so the decoder has
                // somewhere to draw the moment the first keyframe arrives.
                AndroidView(
                    factory = { context ->
                        SurfaceView(context).apply {
                            holder.addCallback(object : SurfaceHolder.Callback {
                                override fun surfaceCreated(holder: SurfaceHolder) = currentOnSurface(holder.surface)

                                override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

                                override fun surfaceDestroyed(holder: SurfaceHolder) = currentOnSurface(null)
                            })
                        }
                    },
                    modifier = Modifier.fillMaxWidth().aspectRatio(ratio),
                )

                val image = state.image

                if (image != null && !state.cameraVideo) {
                    Image(
                        bitmap = image,
                        contentDescription = "Live camera",
                        modifier = Modifier.fillMaxWidth(),
                        contentScale = ContentScale.Fit,
                    )
                } else if (state.framesRendered == 0L) {
                    // Waiting rather than broken: the first frame takes as long as the camera takes
                    // to open, which on some webcams is a second or two.
                    CircularProgressIndicator()
                }
            }

            state.cameraStats?.let {
                Spacer(Modifier.height(4.dp))
                Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            Spacer(Modifier.height(8.dp))
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            SensorButton(
                label = if (state.camera) "Stop camera" else "Live camera",
                live = state.camera,
                enabled = !state.busy && state.cameraAvailable,
                onClick = { onToggle(LiveSensorKind.CAMERA) },
                modifier = Modifier.weight(1f),
            )

            SensorButton(
                label = if (state.microphone) "Stop mic" else "Live mic",
                live = state.microphone,
                enabled = !state.busy && state.microphoneAvailable,
                onClick = { onToggle(LiveSensorKind.MICROPHONE) },
                modifier = Modifier.weight(1f),
            )
        }

        if (state.anyLive) {
            Spacer(Modifier.height(8.dp))

            OutlinedButton(onClick = onStopAll, modifier = Modifier.fillMaxWidth()) {
                Text("Stop everything")
            }
        }

        state.quality?.let { settings ->
            QualityToggle(expanded = qualityVisible, onToggle = { qualityVisible = !qualityVisible })

            if (qualityVisible) {
                QualitySettingsPanel(
                    title = "Camera quality",
                    view = settings.camera,
                    encoder = settings.encoder,
                    onChange = onCameraQuality,
                )
            }
        }

        if (!state.cameraAvailable || !state.microphoneAvailable) {
            Spacer(Modifier.height(8.dp))

            Text(
                when {
                    !state.cameraAvailable && !state.microphoneAvailable ->
                        "This PC has no camera or microphone Nexus can use."

                    !state.cameraAvailable -> "This PC has no camera Nexus can use."
                    else -> "This PC has no microphone Nexus can use."
                },
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        state.message?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
    }
}

/** The same words the PC puts on its own screen, so the two cannot disagree. */
@Composable
private fun LiveBanner(state: LiveMonitorUiState) {
    val text = when {
        state.camera && state.microphone -> "Camera + Microphone LIVE"
        state.camera -> "Camera LIVE"
        else -> "Microphone LIVE"
    }

    Box(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(MaterialTheme.colorScheme.errorContainer)
            .padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Column {
            Text(
                "⏺  $text",
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onErrorContainer,
            )

            Text(
                "Your PC is showing this on its screen and in its tray.",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onErrorContainer,
            )
        }
    }
}

@Composable
private fun SensorButton(
    label: String,
    live: Boolean,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (live) {
        Button(
            onClick = onClick,
            enabled = enabled,
            modifier = modifier,
            colors = ButtonDefaults.buttonColors(
                containerColor = MaterialTheme.colorScheme.error,
                contentColor = MaterialTheme.colorScheme.onError,
            ),
        ) {
            Text(label)
        }
    } else {
        OutlinedButton(onClick = onClick, enabled = enabled, modifier = modifier) {
            Text(label)
        }
    }
}
