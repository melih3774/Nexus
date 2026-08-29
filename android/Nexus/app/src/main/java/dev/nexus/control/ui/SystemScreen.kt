package dev.nexus.control.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.protocol.SystemInfo
import java.util.Locale

/**
 * What the PC is and what it is running.
 *
 * Read-only, and it stays that way in v0.5: no ending processes, no launching anything. A list you
 * can act on is a different feature with a different risk profile, and it is not this milestone.
 */
@Composable
fun SystemScreen(
    modifier: Modifier = Modifier,
    viewModel: SystemViewModel = viewModel(factory = SystemViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(Unit) {
        if (state.info == null) viewModel.refresh()
    }

    Column(modifier.fillMaxSize()) {
        Column(Modifier.padding(20.dp)) {
            Text("System", style = MaterialTheme.typography.titleLarge)
            Spacer(Modifier.height(16.dp))

            when {
                state.loading && state.info == null -> CenteredMessage("Asking the PC…", busy = true)

                state.error != null && state.info == null ->
                    CenteredMessage(state.error!!, actionLabel = "Try again", onAction = viewModel::refresh)

                state.info != null -> MachinePanel(state.info!!)
            }
        }

        if (state.info != null) {
            OutlinedTextField(
                value = state.filter,
                onValueChange = viewModel::filter,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 20.dp),
                label = { Text("Filter ${state.processes.size} processes") },
                singleLine = true,
            )

            Spacer(Modifier.height(12.dp))

            LazyColumn(Modifier.fillMaxSize()) {
                items(state.visibleProcesses, key = { it.pid }) { process ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 20.dp, vertical = 10.dp),
                    ) {
                        Text(
                            process.pid.toString(),
                            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(end = 12.dp),
                        )

                        Text(
                            process.name,
                            modifier = Modifier.weight(1f),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )

                        Text(
                            formatMemory(process.workingSetBytes),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }

                    HorizontalDivider(color = MaterialTheme.colorScheme.outline)
                }
            }
        }
    }
}

@Composable
private fun MachinePanel(info: SystemInfo) {
    Panel(title = "Machine") {
        DetailRow("Name", info.machineName)
        DetailRow("Windows", info.osDescription)
        DetailRow("Architecture", info.osArchitecture)
        DetailRow("Processors", info.processorCount.toString())
        DetailRow("Uptime", formatUptime(info.uptimeSeconds))
        DetailRow("PC Core", "v${info.pcCoreVersion}")
        DetailRow(".NET", info.runtimeVersion)

        // PC Core is designed to run unelevated; if it is not, that is worth seeing from here.
        DetailRow("Elevated", if (info.isElevated) "Yes — unexpected" else "No")
    }
}

private fun formatUptime(seconds: Long): String {
    val days = seconds / 86_400
    val hours = (seconds % 86_400) / 3_600
    val minutes = (seconds % 3_600) / 60

    return when {
        days > 0 -> "${days}d ${hours}h"
        hours > 0 -> "${hours}h ${minutes}m"
        else -> "${minutes}m"
    }
}

private fun formatMemory(bytes: Long?): String {
    if (bytes == null) return ""
    return String.format(Locale.US, "%.0f MB", bytes.toDouble() / (1024 * 1024))
}
