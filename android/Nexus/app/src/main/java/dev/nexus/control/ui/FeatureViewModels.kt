package dev.nexus.control.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.data.NexusRepository
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ProcessEntry
import dev.nexus.control.protocol.SecurityState
import dev.nexus.control.protocol.SecurityStateChanged
import dev.nexus.control.protocol.SystemInfo
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * A screen's data, in one shape.
 *
 * Loading, failure and content are mutually exclusive, so they are one value rather than three
 * independent flags that can contradict each other.
 */
data class Loadable<T>(
    val value: T? = null,
    val loading: Boolean = false,
    val error: String? = null,
) {
    fun loading() = copy(loading = true, error = null)
    fun loaded(value: T) = Loadable(value = value, loading = false, error = null)
    fun failed(message: String) = copy(loading = false, error = message)
}

/** Turns a protocol failure into something the owner can read. */
internal fun describeFailure(cause: Throwable): String = when {
    cause is NotConnectedException -> "Not connected to the PC."

    cause is NexusRequestException -> when (cause.error.code) {
        NexusProtocol.Errors.PATH_NOT_ALLOWED ->
            "That folder is outside the ones PC Core is allowed to read."

        NexusProtocol.Errors.NOT_FOUND -> "That no longer exists on the PC."
        NexusProtocol.Errors.ACCESS_DENIED -> "Windows refused access to that."
        NexusProtocol.Errors.UNSUPPORTED -> "The PC has this turned off."

        NexusProtocol.Errors.NOT_AUTHORIZED ->
            "This phone is not permitted to do that. Device management stays on the PC."

        NexusProtocol.Errors.INVALID_STATE_TRANSITION ->
            "The PC refused that change from its current state."

        else -> cause.error.message.ifBlank { "The PC refused the request." }
    }

    else -> "Could not reach the PC."
}

// --- System -----------------------------------------------------------------------------------

data class SystemUiState(
    val info: SystemInfo? = null,
    val processes: List<ProcessEntry> = emptyList(),
    val filter: String = "",
    val loading: Boolean = false,
    val error: String? = null,
) {
    val visibleProcesses: List<ProcessEntry>
        get() = if (filter.isBlank()) {
            processes
        } else {
            processes.filter { it.name.contains(filter, ignoreCase = true) || it.pid.toString() == filter }
        }
}

class SystemViewModel(private val repository: NexusRepository) : ViewModel() {

    private val _state = MutableStateFlow(SystemUiState())
    val state: StateFlow<SystemUiState> = _state.asStateFlow()

    fun refresh() {
        _state.value = _state.value.copy(loading = true, error = null)

        viewModelScope.launch {
            runCatching {
                val info = repository.systemInfo()
                val processes = repository.processes()
                info to processes
            }
                .onSuccess { (info, processes) ->
                    _state.value = _state.value.copy(
                        info = info,
                        processes = processes.processes.sortedBy { it.name.lowercase() },
                        loading = false,
                    )
                }
                .onFailure {
                    _state.value = _state.value.copy(loading = false, error = describeFailure(it))
                }
        }
    }

    fun filter(value: String) {
        _state.value = _state.value.copy(filter = value)
    }

    companion object {
        val Factory = viewModelFactory { initializer { SystemViewModel(this.nexusContainer.repository) } }
    }
}

// --- Security ---------------------------------------------------------------------------------

data class SecurityUiState(
    val state: SecurityState? = null,
    val unknownState: String? = null,
    val since: String = "",
    val busy: Boolean = false,
    val error: String? = null,
)

/**
 * The security state, live.
 *
 * The screen subscribes to `security.state_changed` for the whole time it is open, so a change made
 * on the PC — or by this phone — shows up without a refresh. That matters beyond convenience: this
 * is the one screen where a stale reading would tell the owner their machine is in a state it is
 * not in.
 */
class SecurityViewModel(private val repository: NexusRepository) : ViewModel() {

    private val _state = MutableStateFlow(SecurityUiState())
    val state: StateFlow<SecurityUiState> = _state.asStateFlow()

    init {
        viewModelScope.launch {
            repository.events
                .collect { event ->
                    if (event.eventName != NexusProtocol.Events.SECURITY_STATE_CHANGED) return@collect

                    val payload = event.payload?.let {
                        runCatching {
                            NexusJson.decodeFromJsonElement(SecurityStateChanged.serializer(), it)
                        }.getOrNull()
                    } ?: return@collect

                    _state.value = _state.value.copy(
                        state = payload.securityState,
                        unknownState = if (payload.securityState == null) payload.currentState else null,
                        since = event.timestampUtc,
                        error = null,
                    )
                }
        }
    }

    fun refresh() {
        _state.value = _state.value.copy(busy = true, error = null)

        viewModelScope.launch {
            runCatching { repository.securityState() }
                .onSuccess {
                    _state.value = SecurityUiState(
                        state = it.securityState,
                        unknownState = if (it.securityState == null) it.state else null,
                        since = it.sinceUtc,
                    )
                }
                .onFailure {
                    _state.value = _state.value.copy(busy = false, error = describeFailure(it))
                }
        }
    }

    fun arm() = command { repository.arm() }

    fun disarm() = command { repository.disarm() }

    private fun command(block: suspend () -> dev.nexus.control.protocol.SecurityCommandResult) {
        _state.value = _state.value.copy(busy = true, error = null)

        viewModelScope.launch {
            runCatching { block() }
                .onSuccess {
                    // The command's own answer is authoritative for this moment; the event that
                    // follows says the same thing, and a no-op change sends no event at all.
                    _state.value = SecurityUiState(
                        state = it.securityState,
                        unknownState = if (it.securityState == null) it.state else null,
                        since = it.sinceUtc,
                    )
                }
                .onFailure {
                    _state.value = _state.value.copy(busy = false, error = describeFailure(it))
                }
        }
    }

    companion object {
        val Factory = viewModelFactory { initializer { SecurityViewModel(this.nexusContainer.repository) } }
    }
}
