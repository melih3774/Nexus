package dev.nexus.control.assistant

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.security.SessionGateState
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.serialization.KSerializer

data class AssistantUiState(
    val status: AssistantStatus? = null,
    val checking: Boolean = false,
    val tasks: List<AssistantTaskSummary> = emptyList(),
    val loadingTasks: Boolean = false,
    val open: OpenTask? = null,
    val opening: Boolean = false,

    /** A request that changes something is on its way: create, send, stop, mode, or an answer. */
    val busy: Boolean = false,
    val error: String? = null,
    val newTaskMode: String = AssistantTaskSummary.MODE_ASK,
)

/**
 * The AI Assistant screen's state.
 *
 * **Nothing here outlives the owner.** The moment the session gate stops being unlocked, every task,
 * message and approval this holds is dropped — the screen is already replaced by the lock screen, but
 * a transcript sitting in memory behind it is still a transcript. Coming back means a fresh subscription
 * and a fresh read, which is also what PC Core requires: it withdrew the subscription when the owner
 * session went.
 */
class AssistantViewModel(
    private val client: AssistantClient,
    events: Flow<EventEnvelope>,
    gate: StateFlow<SessionGateState>,
    authorizations: StateFlow<Long>,
) : ViewModel() {

    private val _state = MutableStateFlow(AssistantUiState())
    val state: StateFlow<AssistantUiState> = _state.asStateFlow()

    private var started = false
    private var refreshing: Job? = null

    /**
     * The task that was open when PC Assistant went away, so returning puts the owner back where they
     * were rather than at the list. It survives PC Assistant closing and reopening — and nothing else:
     * [forget] clears it with everything else when Nexus locks, because a remembered task id is one
     * more thing a locked phone would be holding.
     */
    private var interruptedTaskId: String? = null

    init {
        viewModelScope.launch {
            events.collect(::onEvent)
        }

        viewModelScope.launch {
            gate.collect { gateState ->
                if (!gateState.isUnlocked) forget()
            }
        }

        // Every time the PC grants the owner session again - after an unlock, or on a new connection
        // after the link dropped - the subscription has to be asked for again.
        viewModelScope.launch {
            authorizations.drop(1).distinctUntilChanged().collect {
                if (started) refresh(reopen = true)
            }
        }
    }

    fun start() {
        if (started) return
        started = true
        refresh(reopen = false)
    }

    /** Subscribes, reads the status and the task list, and re-reads the open task if [reopen]. */
    fun refresh(reopen: Boolean = true) {
        refreshing?.cancel()
        _state.update { it.copy(checking = true, error = null) }

        refreshing = viewModelScope.launch {
            val status = runCatching { client.subscribe().status }
                .getOrElse { cause ->
                    _state.update { it.copy(checking = false, error = describe(cause)) }
                    return@launch
                }

            _state.update { it.copy(status = status, checking = false) }

            if (!status.canRead) {
                dropUnreadableTasks()
                return@launch
            }

            loadTasks()

            val resume = _state.value.open?.task?.id ?: interruptedTaskId
            if (reopen && resume != null) {
                interruptedTaskId = null
                open(resume)
            }
        }
    }

    fun loadTasks() {
        _state.update { it.copy(loadingTasks = true) }

        viewModelScope.launch {
            runCatching { client.tasks() }
                .onSuccess { list -> _state.update { it.copy(tasks = list.tasks, loadingTasks = false) } }
                .onFailure { cause -> _state.update { it.copy(loadingTasks = false, error = describe(cause)) } }
        }
    }

    fun open(taskId: String) {
        val known = _state.value.tasks.firstOrNull { it.id == taskId }
        _state.update { it.copy(open = known?.let { task -> OpenTask(task) }, opening = true, error = null) }
        load(taskId)
    }

    fun close() {
        interruptedTaskId = null
        _state.update { it.copy(open = null, opening = false, error = null) }
        loadTasks()
    }

    fun setNewTaskMode(mode: String) = _state.update { it.copy(newTaskMode = mode) }

    fun create(prompt: String) {
        val text = prompt.trim()
        if (text.isEmpty()) return

        if (text.length > AssistantClient.MAX_PROMPT_CHARS) {
            _state.update { it.copy(error = "That is longer than PC Assistant accepts (${AssistantClient.MAX_PROMPT_CHARS} characters).") }
            return
        }

        perform {
            val created = client.create(_state.value.newTaskMode, text).task
            _state.update { it.copy(open = OpenTask(created), tasks = AssistantConversation.upsert(it.tasks, created)) }
            load(created.id)
        }
    }

    fun send(text: String) {
        val open = _state.value.open ?: return
        val message = text.trim()
        if (message.isEmpty()) return

        perform {
            val task = client.send(open.task.id, message).task
            _state.update { state -> state.copy(open = state.open?.let { AssistantConversation.withTask(it, task) }) }
        }
    }

    fun stop() {
        val open = _state.value.open ?: return
        perform { client.stop(open.task.id) }
    }

    fun setMode(mode: String) {
        val open = _state.value.open ?: return
        if (open.task.mode == mode) return

        perform {
            val task = client.setMode(open.task.id, mode).task
            _state.update { state -> state.copy(open = state.open?.let { AssistantConversation.withTask(it, task) }) }
        }
    }

    fun resolve(approvalId: String, allow: Boolean) {
        perform(
            onRefused = { cause ->
                // Answered already, from the PC or before the phone caught up: the card is stale either way.
                if (cause is NexusRequestException && cause.error.code == NexusProtocol.Errors.NOT_FOUND) dropApproval(approvalId)
            },
        ) {
            client.resolve(approvalId, allow)
            dropApproval(approvalId)
        }
    }

    fun dismissError() = _state.update { it.copy(error = null) }

    private fun load(taskId: String) {
        viewModelScope.launch {
            runCatching { client.task(taskId) }
                .onSuccess { detail ->
                    _state.update { state ->
                        // The owner may have backed out, or opened another task, while this was on its way.
                        if (state.open != null && state.open.task.id != taskId) {
                            state.copy(opening = false)
                        } else {
                            state.copy(open = AssistantConversation.merge(state.open, detail), opening = false)
                        }
                    }
                }
                .onFailure { cause -> _state.update { it.copy(opening = false, error = describe(cause)) } }
        }
    }

    private fun dropApproval(approvalId: String) =
        _state.update { state -> state.copy(open = state.open?.let { AssistantConversation.withoutApproval(it, approvalId) }) }

    private fun perform(onRefused: (Throwable) -> Unit = {}, work: suspend () -> Unit) {
        _state.update { it.copy(busy = true, error = null) }

        viewModelScope.launch {
            runCatching { work() }
                .onSuccess { _state.update { it.copy(busy = false) } }
                .onFailure { cause ->
                    onRefused(cause)
                    _state.update { it.copy(busy = false, error = describe(cause)) }

                    if (cause is NexusRequestException && cause.error.code == NexusProtocol.Errors.ASSISTANT_UNAVAILABLE) {
                        refresh(reopen = false)
                    }
                }
        }
    }

    private fun onEvent(event: EventEnvelope) {
        if (!event.eventName.startsWith("assistant.")) return
        val payload = event.payload ?: return

        when (event.eventName) {
            NexusProtocol.Events.ASSISTANT_STATUS_CHANGED -> decode(payload, AssistantStatus.serializer())?.let { status ->
                val wasReadable = _state.value.status?.canRead == true
                _state.update { it.copy(status = status) }

                if (status.canRead && !wasReadable) refresh(reopen = true)
                if (!status.canRead) dropUnreadableTasks()
            }

            NexusProtocol.Events.ASSISTANT_TASK_CHANGED -> decode(payload, AssistantTaskResult.serializer())?.let { result ->
                val task = result.task
                val open = _state.value.open

                _state.update { state ->
                    state.copy(
                        tasks = AssistantConversation.upsert(state.tasks, task),
                        open = state.open?.let { AssistantConversation.withTask(it, task) },
                    )
                }

                // An event can be dropped on a busy link, so a turn that just ended is read once in full.
                if (open != null && open.task.id == task.id && AssistantConversation.isFinished(task) && !AssistantConversation.isFinished(open.task)) {
                    load(task.id)
                }
            }

            NexusProtocol.Events.ASSISTANT_MESSAGE -> decode(payload, AssistantMessageEvent.serializer())?.let { message ->
                _state.update { state -> state.copy(open = state.open?.let { AssistantConversation.withMessage(it, message) }) }
            }

            NexusProtocol.Events.ASSISTANT_PROGRESS -> decode(payload, AssistantProgressEvent.serializer())?.let { progress ->
                _state.update { state -> state.copy(open = state.open?.let { AssistantConversation.withProgress(it, progress) }) }
            }

            NexusProtocol.Events.ASSISTANT_APPROVAL_REQUESTED -> decode(payload, AssistantApprovalRequestedEvent.serializer())?.let { requested ->
                _state.update { state -> state.copy(open = state.open?.let { AssistantConversation.withApproval(it, requested.approval) }) }
            }

            NexusProtocol.Events.ASSISTANT_APPROVAL_RESOLVED -> decode(payload, AssistantApprovalResolvedEvent.serializer())?.let { resolved ->
                dropApproval(resolved.approvalId)
            }
        }
    }

    /** Drops everything the owner's session was needed to see. */
    /**
     * PC Assistant can no longer be read, so nothing held here describes it any more. The task that was
     * open is remembered — only its id, and only until it is reopened or Nexus locks — because closing
     * PC Assistant while reading a task should not also lose the owner's place in it.
     */
    private fun dropUnreadableTasks() {
        interruptedTaskId = _state.value.open?.task?.id ?: interruptedTaskId
        _state.update { it.copy(tasks = emptyList(), open = null) }
    }

    private fun forget() {
        refreshing?.cancel()
        interruptedTaskId = null
        _state.update { AssistantUiState(newTaskMode = it.newTaskMode) }
    }

    private fun <T> decode(payload: kotlinx.serialization.json.JsonElement, serializer: KSerializer<T>): T? =
        runCatching { NexusJson.decodeFromJsonElement(serializer, payload) }.getOrNull()

    override fun onCleared() {
        client.unsubscribe()
        super.onCleared()
    }

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val container = this.nexusContainer
                AssistantViewModel(container.assistant, container.connection.events, container.gate.state, container.gate.authorizations)
            }
        }

        /** Turns a refusal into something the owner can act on. */
        fun describe(cause: Throwable): String = when {
            cause is NotConnectedException -> "Not connected to the PC."

            cause is NexusRequestException -> when (cause.error.code) {
                NexusProtocol.Errors.UNKNOWN_ACTION ->
                    "This PC Core does not have the AI Assistant yet. Update Nexus on the PC."

                NexusProtocol.Errors.NOT_AUTHORIZED -> "Unlock Nexus again to use PC Assistant."
                NexusProtocol.Errors.ASSISTANT_TASK_BUSY -> "Claude is still working on this task. Stop it, or wait for it to finish."
                NexusProtocol.Errors.ASSISTANT_REQUIRES_LOCAL_APPROVAL -> "Administrator approvals are answered on the PC."
                NexusProtocol.Errors.NOT_FOUND -> "That is no longer waiting. It may have been answered on the PC."
                NexusProtocol.Errors.UNSUPPORTED -> cause.error.message.ifBlank { "PC Assistant does not allow that from the phone." }
                else -> cause.error.message.ifBlank { "PC Assistant refused the request." }
            }

            else -> "Could not reach the PC."
        }
    }
}
