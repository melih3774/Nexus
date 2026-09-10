package dev.nexus.control.security

import android.media.MediaPlayer
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewGroup
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.delay
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * Plays one piece of evidence from a local file.
 *
 * **Why the platform `MediaPlayer` and not a media library.** The two formats that reach this are
 * the two PC Core records — MP4 video and MP4/AAC audio — and Android has decoded both since long
 * before `minSdk`. Adding a player library would be several megabytes and a new dependency to keep
 * building years from now, in exchange for features this screen does not have: no streaming
 * protocol, no playlist, no track selection, one file at a time.
 *
 * **Why a file rather than a stream.** The bytes arrive over the Nexus WebSocket, which is not
 * something `MediaPlayer` can read. [EvidenceCache] lands them in `cacheDir` first and this plays
 * from there.
 *
 * The player is released when the composable leaves, which matters more here than usual: a
 * `MediaPlayer` holding a decoder open is a system-wide resource, and this one is showing a
 * recording of the owner's home.
 */
@Composable
fun EvidencePlayer(
    file: File,
    isVideo: Boolean,
    modifier: Modifier = Modifier,
) {
    val player = remember(file.absolutePath) { MediaPlayer() }

    var prepared by remember(file.absolutePath) { mutableStateOf(false) }
    var failed by remember(file.absolutePath) { mutableStateOf<String?>(null) }
    var playing by remember(file.absolutePath) { mutableStateOf(false) }
    var durationMs by remember(file.absolutePath) { mutableIntStateOf(0) }
    var positionMs by remember(file.absolutePath) { mutableIntStateOf(0) }
    var aspect by remember(file.absolutePath) { mutableFloatStateOf(16f / 9f) }

    // Scrubbing holds the slider still: reading position from the player while a finger is on it
    // makes the thumb fight the user.
    var scrubbing by remember(file.absolutePath) { mutableStateOf(false) }

    DisposableEffect(file.absolutePath) {
        try {
            player.setDataSource(file.absolutePath)

            player.setOnPreparedListener {
                durationMs = it.duration.coerceAtLeast(0)
                prepared = true
            }

            player.setOnVideoSizeChangedListener { _, width, height ->
                if (width > 0 && height > 0) aspect = width.toFloat() / height.toFloat()
            }

            player.setOnCompletionListener {
                playing = false
                positionMs = durationMs
            }

            player.setOnErrorListener { _, what, extra ->
                failed = "This clip could not be played ($what/$extra)."
                true
            }

            player.prepareAsync()
        } catch (e: Exception) {
            failed = "This clip could not be opened."
        }

        onDispose {
            try {
                player.setOnPreparedListener(null)
                player.setOnCompletionListener(null)
                player.setOnErrorListener(null)
                player.setOnVideoSizeChangedListener(null)
                if (player.isPlaying) player.stop()
            } catch (e: IllegalStateException) {
                // Already gone; release still has to happen.
            }

            player.release()
        }
    }

    // Position is polled rather than observed: MediaPlayer has no position callback, and four
    // updates a second is smooth enough for a six-second clip and cheap enough to ignore.
    LaunchedEffect(prepared, playing, scrubbing) {
        while (prepared && playing && !scrubbing) {
            positionMs = try {
                player.currentPosition
            } catch (e: IllegalStateException) {
                positionMs
            }

            delay(250)
        }
    }

    Column(modifier) {
        failed?.let {
            Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            return@Column
        }

        if (isVideo) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .aspectRatio(aspect.coerceIn(0.4f, 3f))
                    .background(Color.Black),
                contentAlignment = Alignment.Center,
            ) {
                AndroidView(
                    modifier = Modifier.fillMaxWidth(),
                    factory = { context ->
                        SurfaceView(context).apply {
                            layoutParams = ViewGroup.LayoutParams(
                                ViewGroup.LayoutParams.MATCH_PARENT,
                                ViewGroup.LayoutParams.MATCH_PARENT,
                            )

                            holder.addCallback(object : SurfaceHolder.Callback {
                                override fun surfaceCreated(holder: SurfaceHolder) {
                                    try {
                                        player.setDisplay(holder)
                                    } catch (e: IllegalStateException) {
                                        // The player was released while the surface was arriving.
                                    }
                                }

                                override fun surfaceChanged(h: SurfaceHolder, f: Int, w: Int, ht: Int) = Unit

                                override fun surfaceDestroyed(holder: SurfaceHolder) {
                                    try {
                                        player.setDisplay(null)
                                    } catch (e: IllegalStateException) {
                                        // Same.
                                    }
                                }
                            })
                        }
                    },
                )

                if (!prepared) CircularProgressIndicator()
            }
        } else {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(64.dp),
                contentAlignment = Alignment.Center,
            ) {
                if (prepared) {
                    Text("Audio clip", style = MaterialTheme.typography.bodyMedium)
                } else {
                    CircularProgressIndicator()
                }
            }
        }

        Slider(
            value = if (durationMs <= 0) 0f else positionMs.toFloat() / durationMs,
            onValueChange = {
                scrubbing = true
                positionMs = (it * durationMs).toInt()
            },
            onValueChangeFinished = {
                try {
                    player.seekTo(positionMs)
                } catch (e: IllegalStateException) {
                    // Nothing to seek.
                }
                scrubbing = false
            },
            enabled = prepared && durationMs > 0,
            modifier = Modifier.fillMaxWidth(),
        )

        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(
                enabled = prepared,
                onClick = {
                    try {
                        if (player.isPlaying) {
                            player.pause()
                            playing = false
                        } else {
                            // Replays from the start rather than sitting at the end doing nothing.
                            if (durationMs > 0 && positionMs >= durationMs) player.seekTo(0)
                            player.start()
                            playing = true
                        }
                    } catch (e: IllegalStateException) {
                        failed = "This clip could not be played."
                    }
                },
            ) {
                Text(if (playing) "Pause" else "Play")
            }

            Text(
                "${clock(positionMs)} / ${clock(durationMs)}",
                style = MaterialTheme.typography.labelMedium,
                modifier = Modifier.padding(end = 8.dp),
            )
        }
    }
}

private fun clock(ms: Int): String {
    val total = ms.coerceAtLeast(0).toLong()
    val minutes = TimeUnit.MILLISECONDS.toMinutes(total)
    val seconds = TimeUnit.MILLISECONDS.toSeconds(total) % 60
    return "%d:%02d".format(minutes, seconds)
}
