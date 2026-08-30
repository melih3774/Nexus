package dev.nexus.control.security

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import dev.nexus.control.protocol.EvidenceSummary
import dev.nexus.control.protocol.SecuritySessionSummary
import dev.nexus.control.protocol.SessionDetail
import dev.nexus.control.protocol.SessionResolution

/**
 * The active incident, and what the owner can do about it.
 *
 * The two answers are given equal visual weight on purpose. Making `This was me` the obvious button
 * would train the owner to tap it without reading, which is exactly the habit that makes the whole
 * feature useless the one time it matters.
 */
@Composable
fun ActiveIncidentPanel(
    session: SessionDetail,
    busy: Boolean,
    onViewLive: () -> Unit,
    onThisWasMe: () -> Unit,
    onNotMe: () -> Unit,
    onViewEvidence: (EvidenceSummary) -> Unit,
    modifier: Modifier = Modifier,
) {
    val summary = session.summary
    val alert = summary.highAlert

    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(
                if (alert) {
                    MaterialTheme.colorScheme.errorContainer
                } else {
                    MaterialTheme.colorScheme.surfaceVariant
                },
            )
            .padding(16.dp),
    ) {
        Text(
            if (alert) "HIGH ALERT" else "SECURITY SESSION",
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.Bold,
            color = if (alert) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
        )

        Spacer(Modifier.height(6.dp))

        Text(summary.trigger.label, style = MaterialTheme.typography.titleMedium)

        Text(
            "Started ${shortTime(summary.startedAtUtc)} · ${summary.duration}",
            style = MaterialTheme.typography.bodySmall,
        )

        if (alert) {
            Spacer(Modifier.height(8.dp))
            Text(
                "You marked this as not you. The session stays open and evidence keeps being recorded. " +
                    "Nexus will not shut down, lock, or delete anything on its own.",
                style = MaterialTheme.typography.bodySmall,
            )
        }

        Spacer(Modifier.height(14.dp))

        Button(onClick = onViewLive, modifier = Modifier.fillMaxWidth()) { Text("View live") }

        Spacer(Modifier.height(14.dp))

        EvidenceRow(summary)

        if (session.evidence.isNotEmpty()) {
            Spacer(Modifier.height(10.dp))
            EvidenceStrip(session.evidence, onViewEvidence)
        }

        Spacer(Modifier.height(14.dp))
        Timeline(session)

        Spacer(Modifier.height(16.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
            OutlinedButton(
                onClick = onThisWasMe,
                enabled = !busy,
                modifier = Modifier.weight(1f),
            ) {
                Text("This was me")
            }

            Button(
                onClick = onNotMe,
                enabled = !busy && !alert,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                modifier = Modifier.weight(1f),
            ) {
                Text("Not me")
            }
        }

        if (busy) {
            Spacer(Modifier.height(10.dp))
            CircularProgressIndicator(Modifier.size(20.dp))
        }
    }
}

@Composable
private fun EvidenceRow(summary: SecuritySessionSummary) {
    Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
        Count("Screen", summary.screenshotCount)
        Count("Camera", summary.cameraCount)
        Count("Audio", summary.audioCount)
    }
}

@Composable
private fun Count(label: String, value: Int) {
    Column {
        Text("$value", style = MaterialTheme.typography.titleMedium)
        Text(label, style = MaterialTheme.typography.labelSmall)
    }
}

@Composable
private fun EvidenceStrip(evidence: List<EvidenceSummary>, onView: (EvidenceSummary) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        for (item in evidence.takeLast(8).reversed()) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { onView(item) }
                    .padding(vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(item.kind.label, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))

                Text(
                    "${item.sizeLabel} · ${item.shortDigest}",
                    style = MaterialTheme.typography.labelSmall,
                )
            }
        }
    }
}

/**
 * The timeline, newest first.
 *
 * Everything here is written by the PC to be read months later, and it never contains content — that
 * keyboard activity occurred, never what was typed.
 */
@Composable
private fun Timeline(session: SessionDetail, limit: Int = 12) {
    Column {
        Text("Timeline", style = MaterialTheme.typography.labelLarge)
        Spacer(Modifier.height(6.dp))

        for (entry in session.timeline.takeLast(limit).reversed()) {
            Row(Modifier.padding(vertical = 2.dp)) {
                Text(
                    shortTime(entry.atUtc),
                    style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.width(56.dp),
                )

                Text(entry.message, style = MaterialTheme.typography.bodySmall)
            }
        }

        if (session.timeline.size > limit) {
            Text(
                "+ ${session.timeline.size - limit} earlier",
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
    }
}

/** One row in the history list. */
@Composable
fun HistoryRow(session: SecuritySessionSummary, onOpen: () -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier
            .fillMaxWidth()
            .clickable(onClick = onOpen)
            .padding(vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(shortDateTime(session.startedAtUtc), style = MaterialTheme.typography.bodyMedium)

            Text(
                "${session.duration} · ${session.evidenceCount} item(s)",
                style = MaterialTheme.typography.labelSmall,
            )
        }

        if (session.highAlert) {
            Text(
                "High alert",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.error,
                fontWeight = FontWeight.Bold,
            )
        } else {
            Text(session.resolution.label, style = MaterialTheme.typography.labelMedium)
        }
    }
}

/** One incident's full record, opened from the history. */
@Composable
fun SessionDetailDialog(
    session: SessionDetail,
    onClose: () -> Unit,
    onViewEvidence: (EvidenceSummary) -> Unit,
) {
    AlertDialog(
        onDismissRequest = onClose,
        confirmButton = { TextButton(onClick = onClose) { Text("Close") } },
        title = { Text(session.summary.trigger.label) },
        text = {
            Column(Modifier.heightIn(max = 460.dp)) {
                Text(
                    "${shortDateTime(session.summary.startedAtUtc)} · ${session.summary.duration}",
                    style = MaterialTheme.typography.bodySmall,
                )

                Text(
                    if (session.summary.highAlert) "High alert · ${session.summary.resolution.label}"
                    else session.summary.resolution.label,
                    style = MaterialTheme.typography.labelLarge,
                    color = if (session.summary.highAlert) {
                        MaterialTheme.colorScheme.error
                    } else {
                        MaterialTheme.colorScheme.onSurface
                    },
                )

                Spacer(Modifier.height(12.dp))
                HorizontalDivider()
                Spacer(Modifier.height(12.dp))

                EvidenceRow(session.summary)

                if (session.evidence.isNotEmpty()) {
                    Spacer(Modifier.height(10.dp))
                    EvidenceStrip(session.evidence, onViewEvidence)
                }

                Spacer(Modifier.height(12.dp))
                Timeline(session, limit = 40)
            }
        },
    )
}

/**
 * One piece of evidence, shown.
 *
 * Images render. Video and audio show their metadata and digest and say plainly that playback is not
 * in this version — a half-working player would be worse than an honest gap.
 */
@Composable
fun EvidenceDialog(viewed: ViewedEvidence, onClose: () -> Unit) {
    AlertDialog(
        onDismissRequest = onClose,
        confirmButton = { TextButton(onClick = onClose) { Text("Close") } },
        title = { Text(viewed.summary.kind.label) },
        text = {
            Column {
                Text(
                    "${shortDateTime(viewed.summary.createdAtUtc)} · ${viewed.summary.sizeLabel}",
                    style = MaterialTheme.typography.bodySmall,
                )

                Text(
                    "SHA-256 ${viewed.summary.shortDigest}",
                    style = MaterialTheme.typography.labelSmall,
                )

                Spacer(Modifier.height(12.dp))

                when {
                    viewed.loading -> CircularProgressIndicator(Modifier.size(28.dp))

                    viewed.error != null -> Text(
                        viewed.error,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )

                    viewed.image != null -> Image(
                        bitmap = viewed.image,
                        contentDescription = viewed.summary.kind.label,
                        modifier = Modifier.fillMaxWidth().heightIn(max = 380.dp),
                        contentScale = ContentScale.Fit,
                    )
                }
            }
        },
    )
}

/** "18:42" from an ISO timestamp, without pulling in a formatter for one field. */
internal fun shortTime(iso: String): String {
    val t = iso.indexOf('T')
    return if (t >= 0 && iso.length >= t + 6) iso.substring(t + 1, t + 6) else iso
}

/** "2026-08-30 18:42". */
internal fun shortDateTime(iso: String): String {
    val t = iso.indexOf('T')
    return if (t >= 0 && iso.length >= t + 6) "${iso.substring(0, t)} ${iso.substring(t + 1, t + 6)}" else iso
}
