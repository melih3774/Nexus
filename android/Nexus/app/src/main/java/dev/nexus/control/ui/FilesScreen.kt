package dev.nexus.control.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.protocol.FileEntry

/**
 * A read-only browser over the folders PC Core allows.
 *
 * Read-only in the literal sense: there is no write, delete, rename, move or download here, and no
 * disabled buttons hinting at them. The roots come from the authenticated session, so the list is
 * exactly what this PC has chosen to expose — the phone has no way to ask about anything else and
 * no way to construct a path outside them.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FilesScreen(
    modifier: Modifier = Modifier,
    viewModel: FilesViewModel = viewModel(factory = FilesViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(Unit) { viewModel.start() }

    Column(modifier.fillMaxSize()) {
        Column(Modifier.padding(horizontal = 20.dp, vertical = 12.dp)) {
            Text("Files", style = MaterialTheme.typography.titleLarge)

            Text(
                state.path ?: "Allowed folders",
                style = MonoSmall(),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }

        HorizontalDivider(color = MaterialTheme.colorScheme.outline)

        when {
            state.loading && state.entries.isEmpty() ->
                CenteredMessage("Reading the folder…", busy = true)

            state.error != null ->
                CenteredMessage(
                    state.error!!,
                    actionLabel = state.path?.let { "Try again" },
                    onAction = state.path?.let { path -> { viewModel.open(path, remember = false) } },
                )

            state.path == null -> RootList(state.roots, viewModel::open)

            state.entries.isEmpty() -> CenteredMessage("This folder is empty.")

            else -> EntryList(state.entries, state.skipped, viewModel::select)
        }
    }

    state.selected?.let { entry ->
        ModalBottomSheet(onDismissRequest = viewModel::clearSelection) {
            FileDetail(entry)
        }
    }
}

@Composable
private fun RootList(roots: List<String>, onOpen: (String) -> Unit) {
    if (roots.isEmpty()) {
        CenteredMessage(
            "PC Core is not exposing any folders. Add one to its allowed roots on the PC.",
        )
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        items(roots) { root ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { onOpen(root) }
                    .padding(horizontal = 20.dp, vertical = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("▸", color = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.padding(horizontal = 8.dp))

                Column {
                    Text(root.substringAfterLast('\\').ifEmpty { root })
                    Text(
                        root,
                        style = MonoSmall(),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }

            HorizontalDivider(color = MaterialTheme.colorScheme.outline)
        }
    }
}

@Composable
private fun EntryList(entries: List<FileEntry>, skipped: Int, onSelect: (FileEntry) -> Unit) {
    LazyColumn(Modifier.fillMaxSize()) {
        items(entries) { entry ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { onSelect(entry) }
                    .padding(horizontal = 20.dp, vertical = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    if (entry.isDirectory) "▸" else "·",
                    color = if (entry.isDirectory) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    },
                )

                Spacer(Modifier.padding(horizontal = 8.dp))

                Column(Modifier.weight(1f)) {
                    Text(entry.name, maxLines = 1, overflow = TextOverflow.Ellipsis)

                    if (!entry.isDirectory) {
                        Text(
                            formatSize(entry.sizeBytes),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }

                // Reparse points are shown but never followed — PC Core does not walk into them,
                // and saying so is more useful than hiding them.
                if (entry.isLink) {
                    Text(
                        "link",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.secondary,
                    )
                }
            }

            HorizontalDivider(color = MaterialTheme.colorScheme.outline)
        }

        if (skipped > 0) {
            item {
                Text(
                    "$skipped item(s) could not be read and are not listed.",
                    modifier = Modifier.padding(20.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun FileDetail(entry: FileEntry) {
    Column(Modifier.padding(24.dp)) {
        Text(entry.name, style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(4.dp))

        Text(
            entry.path,
            style = MonoSmall(),
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        Spacer(Modifier.height(16.dp))

        DetailRow("Type", if (entry.isDirectory) "Folder" else "File")
        DetailRow("Size", formatSize(entry.sizeBytes))
        DetailRow("Modified", entry.modifiedUtc?.replace('T', ' ')?.take(19) ?: "—")
        DetailRow("Read-only", if (entry.isReadOnly) "Yes" else "No")
        DetailRow("Hidden", if (entry.isHidden) "Yes" else "No")
        DetailRow("Link", if (entry.isLink) "Yes" else "No")

        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun MonoSmall() = MaterialTheme.typography.bodySmall.copy(
    fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace,
)

private fun formatSize(bytes: Long?): String {
    if (bytes == null) return "—"
    if (bytes < 1024) return "$bytes B"

    val units = listOf("KB", "MB", "GB", "TB")
    var value = bytes.toDouble() / 1024
    var unit = 0

    while (value >= 1024 && unit < units.lastIndex) {
        value /= 1024
        unit++
    }

    return String.format(java.util.Locale.US, "%.1f %s", value, units[unit])
}
