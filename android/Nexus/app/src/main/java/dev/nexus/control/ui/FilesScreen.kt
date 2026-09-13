package dev.nexus.control.ui

import android.content.ActivityNotFoundException
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.files.DownloadPurpose
import dev.nexus.control.files.FilePlace
import dev.nexus.control.files.FileRoot
import dev.nexus.control.files.Transfer
import dev.nexus.control.files.TransferDirection
import dev.nexus.control.files.TransferStatus
import dev.nexus.control.protocol.FileEntry
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * The PC's files: browse drives and folders, open a file on the phone, save one to Downloads, and
 * upload into the folder on screen. Every call goes over the owner session; Windows' own permissions
 * decide what is readable and writable, and PC Core says which it was when it refuses.
 *
 * Nothing here deletes, renames or moves a file on the PC. Replacing one with an upload of the same
 * name is the only change to an existing file, and it asks first.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FilesScreen(
    modifier: Modifier = Modifier,
    viewModel: FilesViewModel = viewModel(factory = FilesViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val transfers by viewModel.transfers.collectAsStateWithLifecycle()
    val context = LocalContext.current

    LaunchedEffect(Unit) { viewModel.start() }

    val open: (Long) -> Unit = { id ->
        val intent = viewModel.viewIntent(id)

        try {
            if (intent != null) context.startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            Toast.makeText(context, "No app on this phone opens this kind of file. Save it instead.", Toast.LENGTH_LONG).show()
        }
    }

    // A file downloaded to look at is shown as soon as it is verified.
    LaunchedEffect(Unit) {
        viewModel.finished.collect { transfer ->
            if (transfer.direction == TransferDirection.Download && transfer.purpose == DownloadPurpose.Open) open(transfer.id)
        }
    }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isNotEmpty()) viewModel.upload(uris)
    }

    BackHandler(enabled = state.path != null || state.selected != null) { viewModel.back() }

    Column(modifier.fillMaxSize()) {
        Header(
            state = state,
            onUp = viewModel::up,
            onRoots = viewModel::showRoots,
            onRefresh = viewModel::refresh,
            onUpload = { picker.launch(arrayOf("*/*")) },
            onToggleHidden = viewModel::toggleHidden,
        )

        HorizontalDivider(color = MaterialTheme.colorScheme.outline)

        Column(Modifier.weight(1f)) {
            when {
                state.loading && state.entries.isEmpty() -> CenteredMessage("Reading the folder…", busy = true)

                state.error != null -> CenteredMessage(
                    state.error!!,
                    actionLabel = if (state.path != null) "Back to drives" else "Try again",
                    onAction = if (state.path != null) viewModel::showRoots else viewModel::loadRoots,
                )

                state.path == null -> RootList(state.roots, state.places, viewModel::open)

                state.entries.isEmpty() -> CenteredMessage("This folder is empty.")

                else -> EntryList(state.entries, state.skipped, state.truncated, viewModel::select)
            }
        }

        if (transfers.isNotEmpty()) {
            HorizontalDivider(color = MaterialTheme.colorScheme.outline)
            TransferList(
                transfers = transfers,
                onCancel = viewModel::cancel,
                onRetry = viewModel::retry,
                onDismiss = viewModel::dismiss,
                onOpen = open,
                onClearFinished = viewModel::clearFinished,
            )
        }
    }

    state.selected?.let { entry ->
        ModalBottomSheet(onDismissRequest = viewModel::clearSelection) {
            FileDetail(
                entry = entry,
                onOpen = { viewModel.download(entry, DownloadPurpose.Open) },
                onSave = { viewModel.download(entry, DownloadPurpose.Save) },
            )
        }
    }

    // One question at a time: the first upload waiting on a name that is already taken.
    transfers.firstOrNull { it.status == TransferStatus.Conflict }?.let { conflict ->
        AlertDialog(
            onDismissRequest = { viewModel.dismiss(conflict.id) },
            title = { Text("Replace “${conflict.name}”?") },
            text = { Text("A file with this name is already in that folder on the PC. Replacing it cannot be undone from the phone.") },
            confirmButton = {
                TextButton(onClick = { viewModel.replace(conflict.id) }) {
                    Text("Replace", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = { TextButton(onClick = { viewModel.dismiss(conflict.id) }) { Text("Skip") } },
        )
    }
}

@Composable
private fun Header(
    state: FilesUiState,
    onUp: () -> Unit,
    onRoots: () -> Unit,
    onRefresh: () -> Unit,
    onUpload: () -> Unit,
    onToggleHidden: () -> Unit,
) {
    Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
        Text(
            state.path ?: "Drives and places",
            style = MonoSmall(),
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = 4.dp),
        )

        Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
            if (state.path != null) {
                TextButton(onClick = onUp) { Text(if (state.parent != null) "↑ Up" else "↑ Drives") }
                if (state.parent != null) TextButton(onClick = onRoots) { Text("Drives") }
                TextButton(onClick = onUpload) { Text("Upload here") }
            }

            Spacer(Modifier.weight(1f))
            TextButton(onClick = onToggleHidden) { Text(if (state.showHidden) "Hide hidden" else "Show hidden") }
            TextButton(onClick = onRefresh) { Text("↻") }
        }
    }
}

@Composable
private fun RootList(roots: List<FileRoot>, places: List<FilePlace>, onOpen: (String) -> Unit) {
    if (roots.isEmpty()) {
        CenteredMessage("PC Core is not exposing any folders. Add one to its allowed roots on the PC.")
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        if (places.isNotEmpty()) {
            item { SectionLabel("Places") }
            items(places) { place -> NavRow("★", place.name, place.path) { onOpen(place.path) } }
        }

        item { SectionLabel("Drives") }
        items(roots) { root ->
            val detail = if (root.freeBytes != null && root.totalBytes != null) {
                "${formatSize(root.freeBytes)} free of ${formatSize(root.totalBytes)}"
            } else {
                root.path
            }

            NavRow(if (root.kind == "drive") "◼" else "▸", root.name, detail) { onOpen(root.path) }
        }
    }
}

@Composable
private fun SectionLabel(text: String) {
    Text(
        text.uppercase(),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(start = 20.dp, top = 16.dp, bottom = 4.dp),
    )
}

@Composable
private fun NavRow(glyph: String, title: String, detail: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(glyph, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.width(16.dp))

        Column {
            Text(title)
            Text(detail, style = MonoSmall(), color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }

    HorizontalDivider(color = MaterialTheme.colorScheme.outline)
}

@Composable
private fun EntryList(entries: List<FileEntry>, skipped: Int, truncated: Boolean, onSelect: (FileEntry) -> Unit) {
    LazyColumn(Modifier.fillMaxSize()) {
        items(entries, key = { it.path }) { entry ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { onSelect(entry) }
                    .padding(horizontal = 20.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    if (entry.isDirectory) "▸" else "·",
                    color = if (entry.isDirectory) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                )

                Spacer(Modifier.width(16.dp))

                Column(Modifier.weight(1f)) {
                    Text(entry.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(
                        listOfNotNull(
                            if (entry.isDirectory) "Folder" else formatSize(entry.sizeBytes),
                            formatModified(entry.modifiedUtc),
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                // Reparse points are shown but PC Core never walks into them during a listing.
                if (entry.isLink) {
                    Text("link", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.secondary)
                }
            }

            HorizontalDivider(color = MaterialTheme.colorScheme.outline)
        }

        if (skipped > 0 || truncated) {
            item {
                Text(
                    listOfNotNull(
                        if (truncated) "Only the first ${entries.size} items are shown." else null,
                        if (skipped > 0) "$skipped item(s) could not be read." else null,
                    ).joinToString(" "),
                    modifier = Modifier.padding(20.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun FileDetail(entry: FileEntry, onOpen: () -> Unit, onSave: () -> Unit) {
    Column(Modifier.padding(start = 24.dp, end = 24.dp, bottom = 24.dp)) {
        Text(entry.name, style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(4.dp))
        Text(entry.path, style = MonoSmall(), color = MaterialTheme.colorScheme.onSurfaceVariant)

        Spacer(Modifier.height(16.dp))

        DetailRow("Type", typeOf(entry))
        DetailRow("Size", formatSize(entry.sizeBytes))
        DetailRow("Modified", formatModified(entry.modifiedUtc) ?: "—")
        if (entry.isReadOnly) DetailRow("Read-only", "Yes")
        if (entry.isHidden) DetailRow("Hidden", "Yes")

        Spacer(Modifier.height(20.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = onOpen, modifier = Modifier.weight(1f)) { Text("Open") }
            OutlinedButton(onClick = onSave, modifier = Modifier.weight(1f)) { Text("Save to phone") }
        }

        Text(
            "Save puts it in Downloads/Nexus. Open keeps a temporary copy for viewing.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 12.dp),
        )
    }
}

@Composable
private fun TransferList(
    transfers: List<Transfer>,
    onCancel: (Long) -> Unit,
    onRetry: (Long) -> Unit,
    onDismiss: (Long) -> Unit,
    onOpen: (Long) -> Unit,
    onClearFinished: () -> Unit,
) {
    Column(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(start = 20.dp, end = 8.dp, top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("TRANSFERS", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.weight(1f))
            if (transfers.any { it.finished }) TextButton(onClick = onClearFinished) { Text("Clear done") }
        }

        LazyColumn(Modifier.heightIn(max = 240.dp)) {
            items(transfers.reversed(), key = { it.id }) { transfer ->
                TransferRow(transfer, onCancel, onRetry, onDismiss, onOpen)
            }
        }
    }
}

@Composable
private fun TransferRow(
    transfer: Transfer,
    onCancel: (Long) -> Unit,
    onRetry: (Long) -> Unit,
    onDismiss: (Long) -> Unit,
    onOpen: (Long) -> Unit,
) {
    Column(Modifier.padding(horizontal = 20.dp, vertical = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(if (transfer.direction == TransferDirection.Upload) "↑" else "↓", color = MaterialTheme.colorScheme.primary)
            Spacer(Modifier.width(12.dp))

            Column(Modifier.weight(1f)) {
                Text(transfer.name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)

                val status = transfer.status
                Text(
                    when (status) {
                        TransferStatus.Queued -> "Waiting"
                        TransferStatus.Running -> "${formatSize(transfer.doneBytes)} of ${formatSize(transfer.totalBytes)}"
                        TransferStatus.Verifying -> "Checking it matches the PC…"
                        TransferStatus.Done -> if (transfer.direction == TransferDirection.Upload) "Uploaded" else "Done · ${formatSize(transfer.totalBytes)}"
                        TransferStatus.Conflict -> "Name already taken on the PC"
                        is TransferStatus.Failed -> status.reason
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = if (status is TransferStatus.Failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            when (val status = transfer.status) {
                TransferStatus.Queued, TransferStatus.Running, TransferStatus.Verifying ->
                    TextButton(onClick = { onCancel(transfer.id) }) { Text("Cancel") }

                TransferStatus.Done -> {
                    if (transfer.direction == TransferDirection.Download) TextButton(onClick = { onOpen(transfer.id) }) { Text("Open") }
                    TextButton(onClick = { onDismiss(transfer.id) }) { Text("✕") }
                }

                is TransferStatus.Failed -> {
                    TextButton(onClick = { onRetry(transfer.id) }) { Text(if (status.resumable) "Resume" else "Retry") }
                    TextButton(onClick = { onDismiss(transfer.id) }) { Text("✕") }
                }

                TransferStatus.Conflict -> TextButton(onClick = { onDismiss(transfer.id) }) { Text("✕") }
            }
        }

        if (transfer.active) {
            Spacer(Modifier.height(6.dp))

            if (transfer.status == TransferStatus.Running && transfer.totalBytes > 0) {
                LinearProgressIndicator(progress = { transfer.progress }, modifier = Modifier.fillMaxWidth())
            } else {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
private fun MonoSmall() = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace)

private fun typeOf(entry: FileEntry): String = when {
    entry.isDirectory -> "Folder"
    entry.name.contains('.') -> "${entry.name.substringAfterLast('.').uppercase()} file"
    else -> "File"
}

private val modifiedFormat = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")

internal fun formatModified(utc: String?): String? = utc?.let {
    runCatching { OffsetDateTime.parse(it).atZoneSameInstant(ZoneId.systemDefault()).format(modifiedFormat) }.getOrNull()
}

internal fun formatSize(bytes: Long?): String {
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
