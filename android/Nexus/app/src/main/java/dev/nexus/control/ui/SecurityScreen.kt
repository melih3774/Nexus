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
import androidx.activity.compose.LocalActivity
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.fragment.app.FragmentActivity
import dev.nexus.control.security.ActiveIncidentPanel
import dev.nexus.control.security.BiometricOwnerConfirmation
import dev.nexus.control.security.EvidenceDialog
import dev.nexus.control.security.HistoryRow
import dev.nexus.control.security.SecurityAlertToggle
import dev.nexus.control.security.SecurityIncidentViewModel
import dev.nexus.control.security.SessionDetailDialog
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
    onViewLive: () -> Unit = {},
    viewModel: SecurityViewModel = viewModel(factory = SecurityViewModel.Factory),
    incidentViewModel: SecurityIncidentViewModel = viewModel(factory = SecurityIncidentViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val incidents by incidentViewModel.state.collectAsStateWithLifecycle()
    val viewedEvidence by incidentViewModel.evidence.collectAsStateWithLifecycle()

    // BiometricPrompt needs the hosting activity. Built here rather than injected so the view model
    // stays free of Android UI types and can be tested with a scripted prompt.
    val activity = LocalActivity.current as? FragmentActivity
    val confirmation = remember(activity) {
        activity?.let { BiometricOwnerConfirmation(it) }
    }

    LaunchedEffect(Unit) {
        viewModel.refresh()
        incidentViewModel.refresh()
    }

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

        incidents.error?.let {
            Panel {
                Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            }
            Spacer(Modifier.height(16.dp))
        }

        incidents.confirmationMessage?.let {
            Panel {
                Column {
                    Text(it, style = MaterialTheme.typography.bodyMedium)
                    TextButton(onClick = incidentViewModel::dismissMessage) { Text("OK") }
                }
            }
            Spacer(Modifier.height(16.dp))
        }

        incidents.active?.let { active ->
            ActiveIncidentPanel(
                session = active,
                busy = incidents.busy,
                onViewLive = onViewLive,
                onThisWasMe = {
                    confirmation?.let(incidentViewModel::thisWasMe)
                },
                onNotMe = incidentViewModel::notMe,
                onViewEvidence = { incidentViewModel.viewEvidence(active.summary.sessionId, it) },
            )

            Spacer(Modifier.height(20.dp))
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

        Spacer(Modifier.height(24.dp))

        SecurityAlertToggle()

        Spacer(Modifier.height(24.dp))

        Text("History", style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(4.dp))

        if (incidents.history.isEmpty()) {
            Text(
                "No security incidents recorded.",
                style = MaterialTheme.typography.bodySmall,
            )
        } else {
            for (session in incidents.history) {
                HistoryRow(session, onOpen = { incidentViewModel.open(session.sessionId) })
                HorizontalDivider()
            }
        }
    }

    incidents.opened?.let { opened ->
        SessionDetailDialog(
            session = opened,
            onClose = incidentViewModel::closeOpened,
            onViewEvidence = { incidentViewModel.viewEvidence(opened.summary.sessionId, it) },
        )
    }

    viewedEvidence?.let { EvidenceDialog(it, onClose = incidentViewModel::closeEvidence) }
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
