package dev.nexus.control.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.FailureReason

/**
 * The connection banner.
 *
 * It is the first thing on every screen because it is the thing that explains everything else: a
 * blank Files list means something different when the PC is unreachable than when the folder is
 * empty. Each failure names what the owner would have to do, since Nexus cannot fix any of them
 * itself.
 */
@Composable
fun ConnectionBanner(
    state: ConnectionState,
    onRetry: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val (label, detail) = describe(state)

    val dotColor by animateColorAsState(
        targetValue = when {
            state is ConnectionState.Connected -> MaterialTheme.colorScheme.primary
            state is ConnectionState.Connecting || state is ConnectionState.Authenticating ->
                MaterialTheme.colorScheme.secondary
            state is ConnectionState.Failed -> MaterialTheme.colorScheme.error
            else -> MaterialTheme.colorScheme.outline
        },
        label = "connection-dot",
    )

    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(14.dp),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                Modifier
                    .size(9.dp)
                    .clip(CircleShape)
                    .background(dotColor),
            )

            Spacer(Modifier.size(12.dp))

            Column(Modifier.weight(1f)) {
                Text(label, style = MaterialTheme.typography.titleSmall)

                if (detail != null) {
                    Text(
                        detail,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            if (state is ConnectionState.Failed) {
                TextButton(onClick = onRetry) { Text("Retry") }
            }
        }
    }
}

private fun describe(state: ConnectionState): Pair<String, String?> = when (state) {
    ConnectionState.Idle -> "Disconnected" to null
    ConnectionState.Connecting -> "Connecting" to "Reaching the PC over Tailscale"
    ConnectionState.Authenticating -> "Connecting" to "Proving this phone is the paired one"

    is ConnectionState.Connected -> "Connected" to null

    is ConnectionState.Failed -> when (state.reason) {
        FailureReason.NoNetwork ->
            "No network" to "This phone has no internet connection."

        FailureReason.PrivateNetworkUnavailable ->
            "Tailscale not connected" to "Open Tailscale on this phone and connect, then retry."

        FailureReason.PcUnreachable ->
            "PC unreachable" to "The PC did not answer. Check it is awake, that Tailscale is " +
                "connected there, and that PC Core is running."

        FailureReason.AuthenticationFailed ->
            "Authentication failed" to "The PC rejected this phone's credential. It may have been " +
                "unpaired there. Pair again."

        FailureReason.ProtocolError ->
            "Unexpected reply" to "The PC answered with something this app does not understand."
    }
}

/**
 * A home-screen action. The grid is the app's whole navigation, and it is meant to grow: AI, PC
 * Control, Microphone and Camera are cards this component will render unchanged when they exist.
 */
@Composable
fun ActionCard(
    title: String,
    subtitle: String,
    glyph: String,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val content = if (enabled) {
        MaterialTheme.colorScheme.onSurface
    } else {
        MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
    }

    Surface(
        modifier = modifier
            .height(120.dp)
            .clickable(enabled = enabled, onClick = onClick),
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(18.dp),
        border = androidx.compose.foundation.BorderStroke(
            1.dp,
            if (enabled) MaterialTheme.colorScheme.outline else Color.Transparent,
        ),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(16.dp),
            verticalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(glyph, style = MaterialTheme.typography.headlineSmall, color = content)

            Column {
                Text(title, style = MaterialTheme.typography.titleMedium, color = content)
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** Loading, empty and error states share one shape so screens do not each invent their own. */
@Composable
fun CenteredMessage(
    text: String,
    modifier: Modifier = Modifier,
    busy: Boolean = false,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null,
) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        if (busy) {
            CircularProgressIndicator(strokeWidth = 2.dp)
            Spacer(Modifier.height(20.dp))
        }

        Text(
            text,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )

        if (actionLabel != null && onAction != null) {
            Spacer(Modifier.height(20.dp))
            Button(onClick = onAction) { Text(actionLabel) }
        }
    }
}

/** A label/value row, used wherever the app reports facts about the PC. */
@Composable
fun DetailRow(label: String, value: String, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

/** A boxed section with a heading. Keeps screens visually consistent without a layout framework. */
@Composable
fun Panel(
    title: String? = null,
    modifier: Modifier = Modifier,
    content: @Composable androidx.compose.foundation.layout.ColumnScope.() -> Unit,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(16.dp),
    ) {
        Column(Modifier.padding(16.dp)) {
            if (title != null) {
                Text(
                    title.uppercase(),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(10.dp))
            }
            content()
        }
    }
}

/** The wordmark. Deliberately the only branding in the app. */
@Composable
fun NexusWordmark(modifier: Modifier = Modifier) {
    Text(
        "NEXUS",
        modifier = modifier.border(0.dp, Color.Transparent),
        style = MaterialTheme.typography.displaySmall,
        color = MaterialTheme.colorScheme.primary,
    )
}
