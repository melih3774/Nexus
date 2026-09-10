package dev.nexus.control.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.FailureReason
import dev.nexus.control.security.LockReason
import dev.nexus.control.security.SessionGateState

/**
 * What Nexus shows when it is locked.
 *
 * **It deliberately shows almost nothing.** The PC's name is not here, the security state is not
 * here, the file roots are not here. A locked Nexus is a Nexus in somebody else's hand until proved
 * otherwise, and the only things worth showing are that the PC is reachable and that the way in is
 * the phone's own authentication.
 *
 * **It is a lock, not a wall.** The app stays open, the connection stays up, and cancelling the
 * prompt leaves this screen rather than closing anything — so the owner can try again, and so the
 * refusal is visibly a refusal rather than a crash.
 */
@Composable
fun LockScreen(
    state: SessionGateState,
    connection: ConnectionState,
    onUnlock: () -> Unit,
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

        Text(
            headline(state),
            style = MaterialTheme.typography.titleMedium,
            textAlign = TextAlign.Center,
        )

        Spacer(Modifier.height(10.dp))

        Text(
            explanation(state, connection),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )

        Spacer(Modifier.height(28.dp))

        if (state is SessionGateState.Authenticating) {
            CircularProgressIndicator(strokeWidth = 2.dp)
        } else {
            Button(onClick = onUnlock, modifier = Modifier.fillMaxWidth(0.7f)) {
                Text("Unlock Nexus")
            }
        }
    }
}

private fun headline(state: SessionGateState): String = when (state) {
    is SessionGateState.Authenticating -> "Waiting for you"
    is SessionGateState.Locked -> when (state.reason) {
        LockReason.NotAuthenticated -> "Nexus is locked"
        LockReason.ScreenLocked -> "Locked when the screen went off"
        LockReason.AwayTooLong -> "Locked while you were away"
        LockReason.SessionEnded -> "Your PC ended this session"
        LockReason.Started -> "Nexus is locked"
    }

    else -> "Nexus is locked"
}

private fun explanation(state: SessionGateState, connection: ConnectionState): String {
    (state as? SessionGateState.Locked)?.message?.let { return it }

    if (state is SessionGateState.Authenticating) {
        return "Confirm with your fingerprint, face or screen lock."
    }

    // A refused credential is not an unreachable PC, and saying so sends the owner to check
    // Tailscale and their PC's power settings for a problem that is neither. It is also not
    // something unlocking fixes, so this returns early rather than inviting a fingerprint.
    if (connection is ConnectionState.Failed
        && connection.reason == FailureReason.AuthenticationFailed
    ) {
        return "Your PC answered but did not accept this phone's saved credential, so it is not " +
            "this phone's lock that is in the way. Unlocking will not change that: the pairing " +
            "itself has to be repaired on the PC."
    }

    val about = when (connection) {
        is ConnectionState.Connected -> "Your PC is reachable."
        is ConnectionState.Connecting, ConnectionState.Authenticating -> "Reaching your PC..."
        is ConnectionState.Failed -> "Your PC is not reachable right now."
        ConnectionState.Idle -> "Not connected to your PC."
    }

    return "$about Unlock with your fingerprint, face or screen lock to control it. " +
        "Nexus never sees any of them — Android answers the question and Nexus only hears yes or no."
}
