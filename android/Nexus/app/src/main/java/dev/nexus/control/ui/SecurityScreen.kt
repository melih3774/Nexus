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
import dev.nexus.control.security.LiveMonitorCard
import dev.nexus.control.security.LiveMonitorViewModel
import dev.nexus.control.security.EvidenceDialog
import dev.nexus.control.security.HistoryRow
import dev.nexus.control.security.SecurityAlertToggle
import dev.nexus.control.security.SecurityIncidentViewModel
import dev.nexus.control.security.SessionDetailDialog
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.protocol.SecurityState

/** What the state control at the foot of the Security screen offers, given where things stand. */
enum class StateControl { ARM, DISARM, ANSWER_INCIDENT, NONE }

/**
 * Which state control belongs on screen.
 *
 * **A function rather than a `when` inside the layout because the wrong answer here is a security
 * bug, not a cosmetic one.** In v0.7.0 the layout showed one button that read "Arm" in
 * OWNER_PRESENT and "Disarm" in both ARMED and SECURITY_SESSION — same place, same size, different
 * meaning. On a real phone that was pressed as if it were Arm during a live incident, and
 * `security.disarm` took SECURITY_SESSION straight back to OWNER_PRESENT: the incident closed, with
 * no biometric prompt and nothing recording that the owner had decided anything.
 *
 * The rule is now stated once, in one place, and asserted in `SecurityStateControlTest`:
 *
 * - an open incident is answered, never armed or disarmed away
 * - ARMED still stands down freely, because that is only a statement about where the owner is
 * - SECURITY_SESSION with nothing left to answer still stands down, so a state the phone cannot
 *   leave is never reachable
 */
fun stateControlFor(state: SecurityState?, hasOpenIncident: Boolean): StateControl = when (state) {
    SecurityState.OWNER_PRESENT -> StateControl.ARM

    SecurityState.ARMED -> StateControl.DISARM

    // The incident owns this state while it is open. Arming or disarming must not answer it.
    SecurityState.SECURITY_SESSION ->
        if (hasOpenIncident) StateControl.ANSWER_INCIDENT else StateControl.DISARM

    null -> StateControl.NONE
}

/**
 * The security state, the incident open against it, and the history behind both.
 *
 * The protocol values are shown as human phrases, but the wire spelling stays underneath them
 * unchanged — the app never invents a state name and never sends one PC Core would reject.
 *
 * **Arm and Disarm are not the whole story, and treating them as if they were is what went wrong.**
 * They move the state between OWNER_PRESENT and ARMED, which is a statement about where the owner
 * is. An open incident is a different kind of thing: it is a question the PC is asking, and it is
 * answered above, by name, through the incident panel. The state control below deliberately offers
 * nothing while that question is outstanding.
 */
@Composable
fun SecurityScreen(
    modifier: Modifier = Modifier,
    onViewLive: () -> Unit = {},
    viewModel: SecurityViewModel = viewModel(factory = SecurityViewModel.Factory),
    incidentViewModel: SecurityIncidentViewModel = viewModel(factory = SecurityIncidentViewModel.Factory),
    liveViewModel: LiveMonitorViewModel = viewModel(factory = LiveMonitorViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val incidents by incidentViewModel.state.collectAsStateWithLifecycle()
    val viewedEvidence by incidentViewModel.evidence.collectAsStateWithLifecycle()
    val liveState by liveViewModel.state.collectAsStateWithLifecycle()

    // BiometricPrompt needs the hosting activity. Built here rather than injected so the view model
    // stays free of Android UI types and can be tested with a scripted prompt.
    val activity = LocalActivity.current as? FragmentActivity
    val confirmation = remember(activity) {
        activity?.let { BiometricOwnerConfirmation(it) }
    }

    LaunchedEffect(Unit) {
        viewModel.refresh()
        incidentViewModel.refresh()
        liveViewModel.refresh()
        liveViewModel.loadQuality()
    }

    // Leaving the Security screen stops live capture. A camera the owner is no longer looking at is
    // a camera nobody asked to leave on, and the PC would otherwise keep streaming to a screen that
    // has gone away.
    DisposableEffect(Unit) {
        onDispose { liveViewModel.stopAll() }
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

        /*
         * The state control, and the one state that deliberately has none.
         *
         * In SECURITY_SESSION this used to show "Disarm" in the same place, at the same size, as
         * "Arm" — one button whose meaning silently changed with the state. On a real phone that
         * read as the Arm control, and pressing it stood a live incident down: `security.disarm`
         * moves SECURITY_SESSION straight to OWNER_PRESENT, with no biometric prompt and no record
         * of the owner having decided anything. An incident answered by accident is an incident
         * that was never answered.
         *
         * So an open incident is now resolved only through the panel above it, where the two
         * answers are named — `This was me`, behind the phone's own owner confirmation, and
         * `Not me`, which keeps the session open and flags it. Neither is a button you press by
         * muscle memory while reaching for something else.
         */
        when (stateControlFor(state.state, hasOpenIncident = incidents.active != null)) {
            StateControl.ARM -> Button(
                onClick = viewModel::arm,
                enabled = !state.busy,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("Arm")
            }

            StateControl.DISARM -> Button(
                onClick = viewModel::disarm,
                enabled = !state.busy,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("Disarm")
            }

            StateControl.ANSWER_INCIDENT -> Text(
                "An incident is open. Answer it above with “This was me” or " +
                    "“Not me” — arming or disarming will not close it.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            StateControl.NONE -> Unit
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
                "You are away. With detection on, a person at the PC's keyboard or mouse opens a " +
                    "security session; " +
                    "your own input through Nexus does not.",
            )
            Spacer(Modifier.height(12.dp))
            Explanation(
                "Security Session",
                "The system considers a security incident active. Only the PC starts one, and it " +
                    "ends only when you answer it — “This was me” confirms it with your " +
                    "fingerprint or screen lock. Arming does not close it.",
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

        LiveMonitorCard(
            state = liveState,
            onToggle = liveViewModel::toggle,
            onStopAll = liveViewModel::stopAll,
            onCameraSurface = liveViewModel::attachCameraSurface,
            onCameraQuality = liveViewModel::setCameraQuality,
        )

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
