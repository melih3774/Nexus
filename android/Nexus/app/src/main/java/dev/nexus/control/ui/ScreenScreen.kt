package dev.nexus.control.ui

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.protocol.CaptureScreenResult

/**
 * One screenshot of the PC, fitted to the phone.
 *
 * `ContentScale.Fit` rather than `Crop`: a desktop is wide and a phone is tall, and cropping would
 * silently hide the parts of the screen the owner is most likely checking on. Letterboxing is the
 * honest presentation.
 */
@Composable
fun ScreenScreen(
    modifier: Modifier = Modifier,
    viewModel: ScreenViewModel = viewModel(factory = ScreenViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    // A width the phone can actually display. Asking for the full 4K desktop would mean megabytes
    // of base64 over a phone link to be scaled down on arrival.
    val requestWidth = 1600

    LaunchedEffect(Unit) {
        if (state.value == null && !state.loading) viewModel.capture(requestWidth)
    }

    Column(modifier.fillMaxSize()) {
        Box(
            Modifier
                .weight(1f)
                .fillMaxWidth()
                .background(Color.Black),
            contentAlignment = Alignment.Center,
        ) {
            when {
                state.loading && state.value == null ->
                    CenteredMessage("Capturing the desktop…", busy = true)

                state.error != null && state.value == null ->
                    CenteredMessage(
                        state.error!!,
                        actionLabel = "Try again",
                        onAction = { viewModel.capture(requestWidth) },
                    )

                state.value != null -> Screenshot(state.value!!)
            }
        }

        Column(Modifier.padding(16.dp)) {
            state.value?.let { shot ->
                Row(Modifier.fillMaxWidth()) {
                    Text(
                        "${shot.sourceWidth} × ${shot.sourceHeight} · ${shot.display}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.weight(1f),
                    )
                }
            }

            if (state.error != null && state.value != null) {
                Text(
                    state.error!!,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                )
            }

            Button(
                onClick = { viewModel.capture(requestWidth) },
                enabled = !state.loading,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 12.dp),
            ) {
                Text(if (state.loading) "Refreshing…" else "Refresh")
            }
        }
    }
}

@Composable
private fun Screenshot(result: CaptureScreenResult) {
    // Standard base64 here, not base64url: PC Core encodes the PNG with Convert.ToBase64String.
    val bitmap = remember(result.capturedAtUtc, result.data) {
        runCatching {
            val bytes = Base64.decode(result.data, Base64.DEFAULT)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        }.getOrNull()
    }

    if (bitmap == null) {
        CenteredMessage("The screenshot could not be decoded.")
        return
    }

    Image(
        bitmap = bitmap.asImageBitmap(),
        contentDescription = "The PC's desktop",
        modifier = Modifier.fillMaxSize(),
        contentScale = ContentScale.Fit,
    )
}
