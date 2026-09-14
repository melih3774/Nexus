package dev.nexus.control.assistant

/** One task, as the phone is showing it. */
data class OpenTask(
    val task: AssistantTaskSummary,
    val messages: List<AssistantMessage> = emptyList(),
    val progress: List<AssistantProgress> = emptyList(),
    val approvals: List<AssistantApproval> = emptyList(),
    val olderOmitted: Boolean = false,
)

/** A line in the transcript: a message, or a compact progress step. */
sealed interface TimelineItem {
    val key: String
    val at: String

    data class Message(val message: AssistantMessage) : TimelineItem {
        override val key: String get() = "m:" + message.id
        override val at: String get() = message.createdAt
    }

    data class Step(val progress: AssistantProgress) : TimelineItem {
        override val key: String get() = "p:" + progress.id
        override val at: String get() = progress.createdAt
    }
}

/**
 * How pushed events change what an open task shows. Pure, so the rules can be tested without a PC.
 *
 * **Messages are snapshots.** PC Assistant sends the whole text of a message each time it grows, under
 * the same id, so a later event for an id replaces the earlier one rather than appending to it. That is
 * what lets a dropped event cost nothing: the next one carries everything.
 */
object AssistantConversation {

    /** Progress kinds worth a line. Approvals have cards and the state is in the header. */
    private val shownKinds = setOf("tool_started", "tool_completed", "tool_failed", "question", "task_failed", "task_cancelled", "progress")

    private const val MAX_PROGRESS = 200

    fun fromDetail(detail: AssistantTaskDetail) = OpenTask(
        task = detail.task,
        messages = detail.messages,
        progress = detail.progress.takeLast(MAX_PROGRESS),
        approvals = detail.pendingApprovals,
        olderOmitted = detail.olderOmitted,
    )

    /**
     * A fresh read merged with what already arrived live. The read wins for anything it has, except a
     * message the stream has already grown further than the stored copy — PC Assistant stores a growing
     * reply less often than it streams it.
     */
    fun merge(current: OpenTask?, detail: AssistantTaskDetail): OpenTask {
        val fresh = fromDetail(detail)
        if (current == null || current.task.id != detail.task.id) return fresh

        val stored = fresh.messages.associateBy { it.id }
        val messages = fresh.messages.map { message ->
            val live = current.messages.firstOrNull { it.id == message.id }
            if (live != null && live.text.length > message.text.length) live else message
        } + current.messages.filter { it.id !in stored }

        val progress = (fresh.progress + current.progress.filter { live -> fresh.progress.none { it.id == live.id } })
            .sortedBy { it.createdAt }
            .takeLast(MAX_PROGRESS)

        return fresh.copy(messages = messages.sortedBy { it.createdAt }, progress = progress)
    }

    fun withTask(open: OpenTask, task: AssistantTaskSummary): OpenTask =
        if (open.task.id == task.id) open.copy(task = task) else open

    fun withMessage(open: OpenTask, event: AssistantMessageEvent): OpenTask {
        if (open.task.id != event.taskId) return open

        val index = open.messages.indexOfFirst { it.id == event.message.id }
        val messages = if (index >= 0) {
            open.messages.toMutableList().also { it[index] = event.message }
        } else {
            open.messages + event.message
        }

        return open.copy(messages = messages)
    }

    fun withProgress(open: OpenTask, event: AssistantProgressEvent): OpenTask {
        if (open.task.id != event.taskId || open.progress.any { it.id == event.event.id }) return open
        return open.copy(progress = (open.progress + event.event).takeLast(MAX_PROGRESS))
    }

    fun withApproval(open: OpenTask, approval: AssistantApproval): OpenTask {
        if (open.task.id != approval.taskId || open.approvals.any { it.id == approval.id }) return open
        return open.copy(approvals = open.approvals + approval)
    }

    fun withoutApproval(open: OpenTask, approvalId: String): OpenTask =
        open.copy(approvals = open.approvals.filterNot { it.id == approvalId })

    /** Messages and shown progress, in time order. Timestamps are ISO-8601 UTC, so text order is time order. */
    fun timeline(open: OpenTask): List<TimelineItem> =
        (open.messages.map { TimelineItem.Message(it) } + open.progress.filter { it.kind in shownKinds }.map { TimelineItem.Step(it) })
            .sortedBy { it.at }

    /** A task changed somewhere: the list keeps one row per task, most recently active first. */
    fun upsert(tasks: List<AssistantTaskSummary>, task: AssistantTaskSummary): List<AssistantTaskSummary> =
        (listOf(task) + tasks.filterNot { it.id == task.id }).sortedByDescending { it.updatedAt }

    fun isFinished(task: AssistantTaskSummary): Boolean = task.state in setOf("completed", "failed", "cancelled", "interrupted")
}
