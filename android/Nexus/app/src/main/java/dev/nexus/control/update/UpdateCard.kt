package dev.nexus.control.update

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import dev.nexus.control.net.AppVersion
import dev.nexus.control.ui.NexusWordmark
import dev.nexus.control.ui.Panel

/**
 * The whole of the app-update interface: which version this is, and one action when there is one.
 *
 * [compact] leaves out the version and the "check" button when nothing is happening, for the lock
 * screen, which shows as little as it can.
 */
@Composable
fun UpdateCard(
    state: AppUpdateState,
    installed: AppVersion,
    onCheck: () -> Unit,
    onUpdate: () -> Unit,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
) {
    if (compact && state !is AppUpdateState.Available && state !is AppUpdateState.Downloading
        && state !is AppUpdateState.Installing && state !is AppUpdateState.Failed
    ) {
        return
    }

    Panel(title = "Nexus app", modifier = modifier) {
        if (!compact) {
            Text("This phone: Nexus ${installed.name} (${installed.code})", style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(8.dp))
        }

        when (state) {
            AppUpdateState.Unknown -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Updates are checked when connected to your PC.", style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
            }

            AppUpdateState.Checking -> Text("Checking for updates...", style = MaterialTheme.typography.bodySmall)

            is AppUpdateState.UpToDate -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    if (state.pcHasRelease) "Up to date" else "Your PC has no release published",
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onCheck) { Text("Check for updates") }
            }

            is AppUpdateState.NotOffered -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text(state.message, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                TextButton(onClick = onCheck) { Text("Check again") }
            }

            is AppUpdateState.Updated -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Updated to Nexus ${state.versionName}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.primary, modifier = Modifier.weight(1f))
                TextButton(onClick = onCheck) { Text("Check for updates") }
            }

            is AppUpdateState.Available -> Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Nexus ${label(state.info, installed)} available", style = MaterialTheme.typography.titleSmall)
                    Text(
                        (if (state.required) "Required by your PC. " else "") + megabytes(state.info.sizeBytes),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Button(onClick = onUpdate) { Text("Update") }
            }

            is AppUpdateState.Downloading -> Column {
                Text("Downloading Nexus ${label(state.info, installed)}... ${(state.progress * 100).toInt()}%", style = MaterialTheme.typography.bodySmall)
                Spacer(Modifier.height(8.dp))
                LinearProgressIndicator(progress = { state.progress }, modifier = Modifier.fillMaxWidth())
            }

            is AppUpdateState.Installing -> Text(
                "Installing Nexus ${label(state.info, installed)}. Confirm on Android's prompt; Nexus reopens updated.",
                style = MaterialTheme.typography.bodySmall,
            )

            is AppUpdateState.Failed -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text(state.message, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error, modifier = Modifier.weight(1f))
                TextButton(onClick = if (state.info != null) onUpdate else onCheck) { Text("Try again") }
            }
        }
    }
}

/**
 * What the app shows when the PC serves only newer builds: why, and the way out. Nothing else is
 * reachable from here, because the PC will refuse it.
 */
@Composable
fun UpdateRequiredScreen(
    state: AppUpdateState,
    installed: AppVersion,
    onCheck: () -> Unit,
    onUpdate: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        NexusWordmark()
        Spacer(Modifier.height(28.dp))

        Text("Update Nexus to continue", style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
        Spacer(Modifier.height(10.dp))
        Text(
            "Your PC runs a newer Nexus than this app can talk to. Until the app is updated, updating is the only " +
                "thing your PC will allow from this phone.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(24.dp))

        UpdateCard(state, installed, onCheck, onUpdate)
    }
}

/** The version name, and the build number too when the name alone would not tell the two apart. */
private fun label(info: AndroidUpdateInfo, installed: AppVersion): String =
    if (info.versionName == installed.name) "${info.versionName} (${info.versionCode})" else info.versionName

private fun megabytes(bytes: Long): String = "%.1f MB".format(bytes / (1024.0 * 1024.0))
