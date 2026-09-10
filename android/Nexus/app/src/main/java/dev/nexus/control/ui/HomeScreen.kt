package dev.nexus.control.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import dev.nexus.control.net.ConnectionState

/**
 * The control panel.
 *
 * The phone's purpose is to act on one PC, so the home screen is that PC and the things that can be
 * done to it — not a list of settings with the machine hidden inside one of them. The action grid
 * is the whole navigation, and it is built to take the cards that do not exist yet.
 */
@Composable
fun HomeScreen(
    state: AppUiState,
    onOpen: (Destination) -> Unit,
    onRetry: () -> Unit,
    onForget: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmForget by remember { mutableStateOf(false) }
    val connected = state.connection.isConnected

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
    ) {
        NexusWordmark()
        Spacer(Modifier.height(20.dp))

        ConnectionBanner(state.connection, onRetry)
        Spacer(Modifier.height(16.dp))

        Panel(title = "My PC") {
            val info = state.systemInfo

            DetailRow("Machine", info?.machineName ?: "—")
            DetailRow("Windows", info?.osDescription ?: "—")
            DetailRow("PC Core", info?.pcCoreVersion?.let { "v$it" } ?: "—")
            DetailRow("Paired", state.pairedPc?.pairedAt?.take(10) ?: "—")

            // The identifier PC Core names in its log when it refuses a phone. Showing it is what
            // makes "this installation is paired with <id>" a comparison the owner can actually
            // make, instead of a string with nothing to check it against. It is an identifier, not
            // a credential: it is sent in the clear on every connection attempt.
            state.pairedPc?.deviceId?.let { DetailRow("This phone", it) }

            if (state.connection is ConnectionState.Connected) {
                DetailRow("Link", "Tailscale · ${state.pairedPc?.endpoint ?: "—"}")
            }
        }

        Spacer(Modifier.height(20.dp))

        // Two by two. Adding AI or PC Control later is another pair of ActionCards, nothing more.
        Row(horizontalArrangement = Arrangement.spacedBy(14.dp), modifier = Modifier.fillMaxWidth()) {
            ActionCard(
                title = "Screen",
                subtitle = "See the desktop",
                glyph = "▣",
                enabled = connected,
                onClick = { onOpen(Destination.Screen) },
                modifier = Modifier.weight(1f),
            )
            ActionCard(
                title = "Files",
                subtitle = "Browse allowed folders",
                glyph = "▤",
                enabled = connected,
                onClick = { onOpen(Destination.Files) },
                modifier = Modifier.weight(1f),
            )
        }

        Spacer(Modifier.height(14.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(14.dp), modifier = Modifier.fillMaxWidth()) {
            ActionCard(
                title = "System",
                subtitle = "Machine and processes",
                glyph = "▦",
                enabled = connected,
                onClick = { onOpen(Destination.System) },
                modifier = Modifier.weight(1f),
            )
            ActionCard(
                title = "Security",
                subtitle = "Presence state",
                glyph = "◈",
                enabled = connected,
                onClick = { onOpen(Destination.Security) },
                modifier = Modifier.weight(1f),
            )
        }

        Spacer(Modifier.height(28.dp))

        TextButton(onClick = { confirmForget = true }) {
            Text("Forget this PC", color = MaterialTheme.colorScheme.error)
        }
    }

    if (confirmForget) {
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text("Forget this PC?") },
            text = {
                Text(
                    "This phone will delete its stored credential and stop connecting. The PC keeps " +
                        "its own record until you unpair there, so pairing again means starting a new " +
                        "pairing code on the PC.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    confirmForget = false
                    onForget()
                }) {
                    Text("Forget", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmForget = false }) { Text("Cancel") }
            },
        )
    }
}
