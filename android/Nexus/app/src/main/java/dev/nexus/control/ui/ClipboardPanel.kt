package dev.nexus.control.ui

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.clipboard.ClipboardSync
import dev.nexus.control.clipboard.PullDecision
import dev.nexus.control.clipboard.PushDecision
import dev.nexus.control.files.FileClient
import dev.nexus.control.files.describeFileError
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.nexusContainer
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class ClipboardUiState(
    val busy: Boolean = false,
    val message: String? = null,
    val failed: Boolean = false,

    /** The send box is open, holding [draft]. */
    val composing: Boolean = false,
    val draft: String = "",
)

/**
 * Text between the two clipboards, one explicit tap at a time.
 *
 * The phone's clipboard is read only when the owner opens the send box - Android shows that it was
 * read, and reading it any other time would be reading it behind their back.
 */
class ClipboardViewModel(
    application: Application,
    private val client: FileClient,
    private val sync: ClipboardSync,
) : AndroidViewModel(application) {

    private val _state = MutableStateFlow(ClipboardUiState())
    val state: StateFlow<ClipboardUiState> = _state.asStateFlow()

    private val clipboard = application.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

    /** What the phone's clipboard held when the send box was opened. */
    private var phoneClipboard: String? = null

    fun copyFromPc() = perform {
        when (val decision = sync.pulled(client.clipboard())) {
            is PullDecision.CopyToPhone -> {
                clipboard.setPrimaryClip(ClipData.newPlainText("Nexus", decision.text))
                sync.copiedToPhone(decision.text)

                "Copied ${decision.text.length} characters from the PC." +
                    if (decision.truncated) " The PC had more; the rest was left behind." else ""
            }

            is PullDecision.AlreadyOnPhone ->
                if (decision.fromThisPhone) "The PC's clipboard still holds what this phone sent." else "This phone's clipboard already has the PC's text."

            PullDecision.Empty -> "The PC's clipboard has no text."
        }
    }

    fun openSend() {
        phoneClipboard = runCatching { clipboard.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(getApplication())?.toString() }.getOrNull()
        _state.update { it.copy(composing = true, draft = phoneClipboard.orEmpty(), message = null) }
    }

    fun updateDraft(text: String) = _state.update { it.copy(draft = text) }

    fun closeSend() = _state.update { it.copy(composing = false) }

    fun send() {
        when (val decision = sync.push(_state.value.draft, phoneClipboard)) {
            PushDecision.Empty -> _state.update { it.copy(message = "There is nothing to send.", failed = true) }
            PushDecision.TooLong -> _state.update { it.copy(message = "That is too much text for a clipboard. Send it as a file.", failed = true) }
            is PushDecision.Send -> {
                _state.update { it.copy(composing = false) }
                perform { sync.pushed(client.setClipboard(decision.text)) }
            }
        }
    }

    private fun perform(work: suspend () -> String) {
        _state.update { it.copy(busy = true, message = null, failed = false) }

        viewModelScope.launch {
            runCatching { work() }
                .onSuccess { message -> _state.update { it.copy(busy = false, message = message, failed = false) } }
                .onFailure { cause ->
                    val message = when (cause) {
                        is NexusRequestException -> describeFileError(cause.error.code, cause.error.message)
                        else -> describeFailure(cause)
                    }
                    _state.update { it.copy(busy = false, message = message, failed = true) }
                }
        }
    }

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val container = this.nexusContainer
                ClipboardViewModel(this[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]!!, container.files, container.clipboardSync)
            }
        }
    }
}

/** A small panel on the home screen: PC to phone, phone to PC. */
@Composable
fun ClipboardPanel(
    enabled: Boolean,
    viewModel: ClipboardViewModel = viewModel(factory = ClipboardViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    Panel(title = "Clipboard") {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxWidth()) {
            Button(onClick = viewModel::copyFromPc, enabled = enabled && !state.busy, modifier = Modifier.weight(1f)) {
                Text("Copy from PC")
            }
            OutlinedButton(onClick = viewModel::openSend, enabled = enabled && !state.busy, modifier = Modifier.weight(1f)) {
                Text("Send to PC")
            }
        }

        state.message?.let {
            Spacer(Modifier.height(10.dp))
            Text(
                it,
                style = MaterialTheme.typography.bodySmall,
                color = if (state.failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }

    if (state.composing) {
        AlertDialog(
            onDismissRequest = viewModel::closeSend,
            title = { Text("Send to the PC's clipboard") },
            text = {
                OutlinedTextField(
                    value = state.draft,
                    onValueChange = viewModel::updateDraft,
                    placeholder = { Text("Type or paste text") },
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 120.dp, max = 280.dp),
                )
            },
            confirmButton = { TextButton(onClick = viewModel::send, enabled = state.draft.isNotEmpty()) { Text("Send") } },
            dismissButton = { TextButton(onClick = viewModel::closeSend) { Text("Cancel") } },
        )
    }
}
