package dev.nexus.control.ui

import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.data.NexusRepository
import dev.nexus.control.files.DownloadPurpose
import dev.nexus.control.files.FileClient
import dev.nexus.control.files.FilePlace
import dev.nexus.control.files.FileRoot
import dev.nexus.control.files.Transfer
import dev.nexus.control.files.TransferDirection
import dev.nexus.control.files.TransferManager
import dev.nexus.control.files.TransferStatus
import dev.nexus.control.files.describeFileError
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.FileEntry
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class FilesUiState(
    val roots: List<FileRoot> = emptyList(),
    val places: List<FilePlace> = emptyList(),

    /** The folder being shown; null for the list of drives and places. */
    val path: String? = null,
    val parent: String? = null,
    val entries: List<FileEntry> = emptyList(),
    val skipped: Int = 0,
    val truncated: Boolean = false,
    val showHidden: Boolean = false,
    val loading: Boolean = false,
    val error: String? = null,
    val selected: FileEntry? = null,
)

/**
 * Browsing the PC, and handing files to [TransferManager].
 *
 * Navigation uses paths PC Core produced, verbatim: an entry's `path`, a listing's `parent`, a root or a
 * place. The app never joins a folder and a name and has no notion of `..`, so it never invents a path
 * to be refused. PC Core re-authorises every one regardless.
 */
class FilesViewModel(
    private val client: FileClient,
    private val repository: NexusRepository,
    private val transferManager: TransferManager,
) : ViewModel() {

    private val _state = MutableStateFlow(FilesUiState())
    val state: StateFlow<FilesUiState> = _state.asStateFlow()

    val transfers: StateFlow<List<Transfer>> = transferManager.transfers

    /** Each transfer as it completes. The screen opens a file that was downloaded to be opened. */
    val finished: SharedFlow<Transfer> = transferManager.finished

    /** Folders visited, so back retraces them rather than climbing the path. */
    private val history = ArrayDeque<String>()

    private var listing: Job? = null
    private var started = false

    init {
        viewModelScope.launch {
            transferManager.finished.collect { transfer ->
                // An upload into the folder on screen appears in it.
                if (transfer.direction == TransferDirection.Upload && transfer.remotePath == _state.value.path) refresh()
            }
        }
    }

    fun start() {
        if (started) return
        started = true
        loadRoots()
    }

    fun loadRoots() {
        _state.update { it.copy(loading = it.path == null, error = null) }

        viewModelScope.launch {
            runCatching { client.roots() }
                .onSuccess { roots -> _state.update { it.copy(roots = roots.roots, places = roots.places, loading = false) } }
                .onFailure { cause ->
                    if (cause is NexusRequestException && cause.error.code == NexusProtocol.Errors.UNKNOWN_ACTION) {
                        // A PC Core from before v1.4 offers the bare allowed roots from the handshake.
                        val roots = repository.allowedRoots.map { FileRoot(path = it, name = it.trimEnd('\\').substringAfterLast('\\').ifEmpty { it }) }
                        _state.update { it.copy(roots = roots, loading = false) }
                    } else if (_state.value.path == null) {
                        _state.update { it.copy(loading = false, error = describe(cause)) }
                    }
                }
        }
    }

    fun open(path: String, remember: Boolean = true) {
        val current = _state.value.path
        if (remember && current != null && current != path) history.addLast(current)

        _state.update { it.copy(path = path, loading = true, error = null, selected = null) }

        listing?.cancel()
        listing = viewModelScope.launch {
            runCatching { client.list(path, _state.value.showHidden) }
                .onSuccess { result ->
                    _state.update {
                        it.copy(
                            path = result.path,
                            parent = result.parent,
                            entries = result.entries,
                            skipped = result.skippedCount,
                            truncated = result.truncated,
                            loading = false,
                        )
                    }
                }
                .onFailure { cause ->
                    if (cause is kotlinx.coroutines.CancellationException) return@onFailure
                    _state.update { it.copy(loading = false, entries = emptyList(), parent = null, error = describe(cause)) }
                }
        }
    }

    fun refresh() {
        val path = _state.value.path
        if (path == null) loadRoots() else open(path, remember = false)
    }

    fun up() {
        _state.value.parent?.let { open(it) } ?: showRoots()
    }

    fun showRoots() {
        history.clear()
        listing?.cancel()
        _state.update { it.copy(path = null, parent = null, entries = emptyList(), error = null, loading = false, selected = null) }
    }

    fun toggleHidden() {
        _state.update { it.copy(showHidden = !it.showHidden) }
        refresh()
    }

    /** @return false when there is nowhere to go back to, so the screen can be left. */
    fun back(): Boolean {
        if (_state.value.selected != null) {
            clearSelection()
            return true
        }

        if (_state.value.path == null) return false

        history.removeLastOrNull()?.let { open(it, remember = false) } ?: showRoots()
        return true
    }

    fun select(entry: FileEntry) {
        if (entry.isDirectory) {
            open(entry.path)
            return
        }

        _state.update { it.copy(selected = entry) }
    }

    fun clearSelection() {
        _state.update { it.copy(selected = null) }
    }

    fun download(entry: FileEntry, purpose: DownloadPurpose) {
        clearSelection()
        transferManager.download(entry, purpose)
    }

    fun upload(uris: List<Uri>) {
        val directory = _state.value.path ?: return
        uris.forEach { transferManager.upload(it, directory) }
    }

    fun cancel(id: Long) = transferManager.cancel(id)

    fun retry(id: Long) = transferManager.retry(id)

    fun replace(id: Long) = transferManager.replace(id)

    fun dismiss(id: Long) = transferManager.dismiss(id)

    fun clearFinished() = transferManager.clearFinished()

    fun viewIntent(id: Long) = transferManager.viewIntent(id)

    private fun describe(cause: Throwable): String = when (cause) {
        is NotConnectedException -> "Not connected to the PC."
        is NexusRequestException -> describeFileError(cause.error.code, cause.error.message)
        else -> "Could not reach the PC."
    }

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val container = this.nexusContainer
                FilesViewModel(container.files, container.repository, container.transfers)
            }
        }
    }
}

/** Whether a transfer row still has something to offer, used to keep finished rows from piling up. */
internal val Transfer.finished: Boolean get() = status == TransferStatus.Done
