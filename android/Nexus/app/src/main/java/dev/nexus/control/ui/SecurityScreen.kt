package dev.nexus.control.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.protocol.SecurityState

/**
 * The security state, and the two controls that change it.
 *
 * The protocol values are shown as human phrases, but the wire spelling stays underneath them
 * unchanged — the app never invents a state name and never sends one PC Core would reject. Only Arm
 * and Disarm exist here because they are the only transitions PC Core accepts from a caller; there
 * is no "This was me", no notification, no recording, and nothing that pretends the machine is
 * watching for anything yet.
 */
@Composable
fun SecurityScreen(
    modifier: Modifier = Modifier,
    viewModel: SecurityViewModel = viewModel(factory = SecurityViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(Unit) { viewModel.refresh() }

    Column(
        modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
    ) {
        Text("Security", style = MaterialTheme.typography.titleLarge)
        Spacer(Modifier.height(20.dp))

        StateCard(state)

        Spacer(Modifier.height(20.dp))

        state.error?.let {
            Panel {
                Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            }
            Spacer(Modifier.height(16.dp))
        }

        when (state.state) {
            SecurityState.OWNER_PRESENT -> Button(
                onClick = viewModel::arm,
                enabled = !state.busy,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("Arm")
            }

            SecurityState.ARMED, SecurityState.SECURITY_SESSION -> Button(
                onClick = viewModel::disarm,
                enabled = !state.busy,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("Disarm")
            }

            null -> Unit
        }

        Spacer(Modifier.height(12.dp))

        OutlinedButton(
            onClick = viewModel::refresh,
            enabled = !state.busy,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Text("Refresh")
        }

        Spacer(Modifier.height(28.dp))

        Panel(title = "What these mean") {
            Explanation(
                "Owner Present",
                "Normal use. Security incident capture is not active.",
            )
            Spacer(Modifier.height(12.dp))
            Explanation(
                "Armed",
                "You have indicated you are away. Future activity detection will use this state.",
            )
            Spacer(Modifier.height(12.dp))
            Explanation(
                "Security Session",
                "The system considers a security incident active. Only the PC starts one; you can " +
                    "stand it down from here.",
            )
        }

        Spacer(Modifier.height(16.dp))

        Text(
            "This state updates live while this screen is open — changes made on the PC appear here " +
                "without refreshing.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun StateCard(state: SecurityUiState) {
    val accent = when (state.state) {
        SecurityState.OWNER_PRESENT -> MaterialTheme.colorScheme.primary
        SecurityState.ARMED -> MaterialTheme.colorScheme.secondary
        SecurityState.SECURITY_SESSION -> MaterialTheme.colorScheme.error
        null -> MaterialTheme.colorScheme.outline
    }

    Panel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(12.dp)
                    .clip(CircleShape)
                    .background(accent),
            )

            Spacer(Modifier.size(12.dp))

            Column {
                Text(
                    label(state),
                    style = MaterialTheme.typography.headlineSmall,
                    color = accent,
                )

                // The protocol value, shown deliberately: this screen is about a machine state, and
                // the exact name is what appears in PC Core's logs.
                Text(
                    state.state?.wire ?: state.unknownState ?: "—",
                    style = MaterialTheme.typography.bodySmall.copy(
                        fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace,
                    ),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        if (state.since.isNotBlank()) {
            Spacer(Modifier.height(12.dp))
            DetailRow("Since", state.since.replace('T', ' ').take(19))
        }
    }
}

private fun label(state: SecurityUiState): String = when (state.state) {
    SecurityState.OWNER_PRESENT -> "Owner Present"
    SecurityState.ARMED -> "Armed"
    SecurityState.SECURITY_SESSION -> "Security Session"
    null -> if (state.unknownState != null) "Unrecognised state" else "Unknown"
}

@Composable
private fun Explanation(title: String, body: String) {
    Column {
        Text(title, style = MaterialTheme.typography.titleSmall)
        Text(
            body,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
