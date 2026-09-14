package dev.nexus.control.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.assistant.AssistantApproval
import dev.nexus.control.assistant.AssistantAvailability
import dev.nexus.control.assistant.AssistantConversation
import dev.nexus.control.assistant.AssistantStatus
import dev.nexus.control.assistant.AssistantTaskSummary
import dev.nexus.control.assistant.AssistantUiState
import dev.nexus.control.assistant.AssistantViewModel
import dev.nexus.control.assistant.OpenTask
import dev.nexus.control.assistant.TimelineItem

/**
 * The owner instructing PC Assistant from the phone.
 *
 * PC Assistant does the work, under its own AUTO/ASK policy, and asks here before anything its policy
 * says needs asking. Administrator approvals are shown but answered on the PC.
 */
@Composable
fun AssistantScreen(viewModel: AssistantViewModel = viewModel(factory = AssistantViewModel.Factory)) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(Unit) { viewModel.start() }
    BackHandler(enabled = state.open != null) { viewModel.close() }

    Column(
        Modifier
            .fillMaxSize()
            .imePadding()
            .padding(horizontal = 16.dp),
    ) {
        StatusStrip(state.status, state.checking, onRetry = { viewModel.refresh() })

        state.error?.let { message ->
            Spacer(Modifier.size(8.dp))
            ErrorLine(message, onDismiss = viewModel::dismissError)
        }

        Spacer(Modifier.size(10.dp))

        val open = state.open
        if (open == null) {
            TaskList(state, viewModel)
        } else {
            TaskView(open, state, viewModel)
        }
    }
}

@Composable
private fun StatusStrip(status: AssistantStatus?, checking: Boolean, onRetry: () -> Unit) {
    val availability = status?.availability
    val ready = availability == AssistantAvailability.Ready

    Surface(color = MaterialTheme.colorScheme.surface, shape = RoundedCornerShape(14.dp), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(9.dp)
                        .clip(CircleShape)
                        .background(if (ready) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error),
                )
                Spacer(Modifier.size(10.dp))
                Text(
                    when {
                        status == null && checking -> "Checking PC Assistant…"
                        status == null -> "PC Assistant"
                        else -> title(availability!!)
                    },
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.weight(1f),
                )
                if (!ready && !checking) TextButton(onClick = onRetry) { Text("Check again") }
            }

            if (status != null && !ready && status.detail.isNotBlank()) {
                Text(status.detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            if (checking) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 6.dp))
        }
    }
}

private fun title(availability: AssistantAvailability) = when (availability) {
    AssistantAvailability.Ready -> "PC Assistant is ready"
    AssistantAvailability.Disabled -> "The PC Assistant bridge is off"
    AssistantAvailability.NotConfigured -> "PC Assistant is not set up in PC Core"
    AssistantAvailability.NotRunning -> "PC Assistant is not running"
    AssistantAvailability.NotTrusted -> "PC Assistant and PC Core do not trust each other yet"
    AssistantAvailability.Incompatible -> "PC Assistant and PC Core versions do not match"
    AssistantAvailability.ClaudeNotReady -> "Claude is not ready on the PC"
    AssistantAvailability.Unavailable -> "PC Assistant is unavailable"
}

@Composable
private fun ErrorLine(message: String, onDismiss: () -> Unit) {
    Surface(color = MaterialTheme.colorScheme.errorContainer, shape = RoundedCornerShape(10.dp), modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(start = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(message, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onErrorContainer, modifier = Modifier.weight(1f))
            TextButton(onClick = onDismiss) { Text("OK") }
        }
    }
}

// --- The list, and starting a task -----------------------------------------------------------------

@Composable
private fun TaskList(state: AssistantUiState, viewModel: AssistantViewModel) {
    var prompt by rememberSaveable { mutableStateOf("") }
    val canWork = state.status?.canWork == true

    LazyColumn(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        item {
            Panel(title = "New General task") {
                ModeChips(state.newTaskMode, enabled = !state.busy, onSelect = viewModel::setNewTaskMode)
                Text(
                    if (state.newTaskMode == AssistantTaskSummary.MODE_ASK) {
                        "ASK: Claude asks here before changing anything or running a command."
                    } else {
                        "AUTO: routine work goes ahead; high-impact steps still ask here."
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.size(8.dp))
                OutlinedTextField(
                    value = prompt,
                    onValueChange = { prompt = it },
                    placeholder = { Text("What should PC Assistant do?") },
                    // Capped as well as floored: with the keyboard up there is about half a screen to
                    // work in, and a field that grows with every line walks Start down behind the
                    // keyboard. Past this it scrolls inside itself, the way the follow-up field does.
                    modifier = Modifier.fillMaxWidth().heightIn(min = 96.dp, max = 168.dp),
                    enabled = canWork && !state.busy,
                )
                Spacer(Modifier.size(8.dp))
                Button(
                    onClick = {
                        viewModel.create(prompt)
                        prompt = ""
                    },
                    enabled = canWork && !state.busy && prompt.isNotBlank(),
                ) { Text("Start") }
            }
        }

        if (state.status?.canRead == true) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("RECENT TASKS", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                    TextButton(onClick = viewModel::loadTasks, enabled = !state.loadingTasks) { Text("Refresh") }
                }
            }

            if (state.tasks.isEmpty() && !state.loadingTasks) {
                item { Text("No tasks yet.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }

            items(state.tasks, key = { it.id }) { task -> TaskRow(task, onOpen = { viewModel.open(task.id) }) }
        }
    }
}

@Composable
private fun TaskRow(task: AssistantTaskSummary, onOpen: () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.fillMaxWidth().clickable(onClick = onOpen),
    ) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp)) {
            Text(task.title, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(
                listOfNotNull(
                    stateText(task),
                    task.mode.uppercase(),
                    task.workspaceName?.let { "Workspace · $it" },
                    task.pendingApprovals.takeIf { it > 0 }?.let { "$it waiting for you" },
                ).joinToString("  ·  "),
                style = MaterialTheme.typography.bodySmall,
                color = if (task.pendingApprovals > 0) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

private fun stateText(task: AssistantTaskSummary) = when (task.state) {
    "running" -> "Working"
    "waiting_for_approval" -> "Waiting for approval"
    "completed" -> "Done"
    "failed" -> "Failed"
    "cancelled" -> "Stopped"
    "interrupted" -> "Interrupted"
    "draft" -> "New"
    else -> task.state
}

@Composable
private fun ModeChips(mode: String, enabled: Boolean, onSelect: (String) -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        FilterChip(selected = mode == AssistantTaskSummary.MODE_AUTO, onClick = { onSelect(AssistantTaskSummary.MODE_AUTO) }, label = { Text("AUTO") }, enabled = enabled)
        FilterChip(selected = mode == AssistantTaskSummary.MODE_ASK, onClick = { onSelect(AssistantTaskSummary.MODE_ASK) }, label = { Text("ASK") }, enabled = enabled)
    }
}

// --- One task ----------------------------------------------------------------------------------------

@Composable
private fun TaskView(open: OpenTask, state: AssistantUiState, viewModel: AssistantViewModel) {
    val task = open.task
    val timeline = AssistantConversation.timeline(open)
    val listState = rememberLazyListState()
    var followUp by rememberSaveable(task.id) { mutableStateOf("") }
    val canWork = state.status?.canWork == true

    // Follow the conversation as it grows, including a reply growing in place.
    val tail = timeline.lastOrNull()?.let { item -> item.key + ((item as? TimelineItem.Message)?.message?.text?.length ?: 0) } to open.approvals.size
    LaunchedEffect(tail) {
        val count = timeline.size + open.approvals.size
        if (count > 0) listState.animateScrollToItem(count)
    }

    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = viewModel::close) { Text("‹ Tasks") }
            Spacer(Modifier.weight(1f))
            ModeChips(task.mode, enabled = !state.busy, onSelect = viewModel::setMode)
        }

        Text(task.title, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                listOfNotNull(stateText(task), task.workspaceName?.let { "Workspace · $it" }).joinToString("  ·  "),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f),
            )
            if (task.running) OutlinedButton(onClick = viewModel::stop, enabled = !state.busy) { Text("Stop") }
        }

        if (task.running || state.opening) LinearProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 4.dp))

        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            if (open.olderOmitted) {
                item { Text("Earlier messages are on the PC.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }

            items(timeline, key = { it.key }) { item ->
                when (item) {
                    is TimelineItem.Message -> MessageBubble(item.message.role, item.message.text, item.message.truncated)
                    is TimelineItem.Step -> StepLine(item.progress.title, item.progress.kind)
                }
            }

            items(open.approvals, key = { "a:" + it.id }) { approval ->
                ApprovalCard(approval, enabled = !state.busy, onAnswer = { allow -> viewModel.resolve(approval.id, allow) })
            }
        }

        Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.Bottom) {
            OutlinedTextField(
                value = followUp,
                onValueChange = { followUp = it },
                placeholder = { Text(if (task.running) "Claude is working…" else "Follow up") },
                modifier = Modifier.weight(1f).heightIn(max = 160.dp),
                enabled = canWork && !task.running && !state.busy,
            )
            Spacer(Modifier.size(8.dp))
            Button(
                onClick = {
                    viewModel.send(followUp)
                    followUp = ""
                },
                enabled = canWork && !task.running && !state.busy && followUp.isNotBlank(),
            ) { Text("Send") }
        }
    }
}

@Composable
private fun MessageBubble(role: String, text: String, truncated: Boolean) {
    val owner = role == "owner"
    val system = role == "system"

    Row(Modifier.fillMaxWidth(), horizontalArrangement = if (owner) Arrangement.End else Arrangement.Start) {
        Surface(
            color = when {
                owner -> MaterialTheme.colorScheme.primaryContainer
                system -> MaterialTheme.colorScheme.surfaceVariant
                else -> MaterialTheme.colorScheme.surface
            },
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.widthIn(max = 340.dp),
        ) {
            Column(Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
                if (!owner) {
                    Text(if (system) "PC Assistant" else "Claude", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                SelectionContainer {
                    Text(text, style = MaterialTheme.typography.bodyMedium)
                }
                if (truncated) {
                    Text("Longer on the PC.", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

@Composable
private fun StepLine(title: String, kind: String) {
    val failed = kind == "tool_failed" || kind == "task_failed"
    Text(
        (if (failed) "✕ " else "· ") + title,
        style = MaterialTheme.typography.bodySmall,
        fontFamily = FontFamily.Monospace,
        color = if (failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
        maxLines = 2,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.padding(horizontal = 4.dp),
    )
}

@Composable
private fun ApprovalCard(approval: AssistantApproval, enabled: Boolean, onAnswer: (Boolean) -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.surface,
        shape = RoundedCornerShape(14.dp),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.primary),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(12.dp)) {
            Text("APPROVAL", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
            Text(approval.action, style = MaterialTheme.typography.titleSmall)
            Spacer(Modifier.size(4.dp))
            Surface(color = MaterialTheme.colorScheme.surfaceVariant, shape = RoundedCornerShape(8.dp), modifier = Modifier.fillMaxWidth()) {
                SelectionContainer {
                    Text(
                        approval.target,
                        style = MaterialTheme.typography.bodySmall,
                        fontFamily = FontFamily.Monospace,
                        modifier = Modifier.padding(8.dp),
                    )
                }
            }
            if (approval.reason.isNotBlank()) {
                Spacer(Modifier.size(4.dp))
                Text(approval.reason, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Spacer(Modifier.size(8.dp))

            if (approval.remoteResolvable) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { onAnswer(false) }, enabled = enabled) { Text("Deny") }
                    Button(onClick = { onAnswer(true) }, enabled = enabled) { Text("Allow once") }
                }
            } else {
                Text(
                    "Administrator approval. Answer it on the PC.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                )
            }
        }
    }
}
