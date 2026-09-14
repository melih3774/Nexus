package dev.nexus.control.assistant

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * What PC Core says about PC Assistant, in the words the phone switches on.
 *
 * A string on the wire, mapped here with an explicit unknown: a newer PC Core naming a state this build
 * has never heard of is shown as unavailable rather than breaking the screen.
 */
enum class AssistantAvailability(val wire: String) {
    Disabled("DISABLED"),
    NotConfigured("NOT_CONFIGURED"),
    NotRunning("NOT_RUNNING"),
    NotTrusted("NOT_TRUSTED"),
    Incompatible("INCOMPATIBLE"),
    ClaudeNotReady("CLAUDE_NOT_READY"),
    Ready("READY"),
    Unavailable("UNAVAILABLE"),
    ;

    companion object {
        fun of(wire: String?): AssistantAvailability = entries.firstOrNull { it.wire == wire } ?: Unavailable
    }
}

@Serializable
data class AssistantStatus(
    val state: String = AssistantAvailability.Unavailable.wire,
    val detail: String = "",
    @SerialName("server_version") val serverVersion: String? = null,
    @SerialName("protocol_version") val protocolVersion: Int? = null,
    val claude: String? = null,
    @SerialName("claude_version") val claudeVersion: String? = null,
    @SerialName("can_work") val canWork: Boolean = false,
) {
    val availability: AssistantAvailability get() = AssistantAvailability.of(state)

    /** Existing tasks can be read whenever PC Assistant itself is reachable, even with Claude signed out. */
    val canRead: Boolean get() = availability == AssistantAvailability.Ready || availability == AssistantAvailability.ClaudeNotReady
}

@Serializable
data class AssistantTaskSummary(
    val id: String = "",
    val title: String = "",
    val kind: String = "general",
    val mode: String = MODE_AUTO,
    val state: String = "draft",
    val running: Boolean = false,
    @SerialName("pending_approvals") val pendingApprovals: Int = 0,
    @SerialName("workspace_name") val workspaceName: String? = null,
    @SerialName("created_at") val createdAt: String = "",
    @SerialName("updated_at") val updatedAt: String = "",
    @SerialName("last_error") val lastError: String? = null,
) {
    val isWorkspace: Boolean get() = kind == "workspace"

    companion object {
        const val MODE_AUTO = "auto"
        const val MODE_ASK = "ask"
    }
}

@Serializable
data class AssistantMessage(
    val id: String = "",
    val role: String = "system",
    val text: String = "",
    val truncated: Boolean = false,
    @SerialName("created_at") val createdAt: String = "",
)

@Serializable
data class AssistantProgress(
    val id: String = "",
    val kind: String = "",
    val title: String = "",
    val detail: String? = null,
    @SerialName("tool_name") val toolName: String? = null,
    @SerialName("created_at") val createdAt: String = "",
)

@Serializable
data class AssistantApproval(
    val id: String = "",
    @SerialName("task_id") val taskId: String = "",
    @SerialName("task_title") val taskTitle: String? = null,
    @SerialName("tool_name") val toolName: String = "",
    val action: String = "",
    val target: String = "",
    val reason: String = "",
    val category: String = "",
    @SerialName("remote_resolvable") val remoteResolvable: Boolean = false,
    @SerialName("created_at") val createdAt: String = "",
)

@Serializable
data class AssistantTaskDetail(
    val task: AssistantTaskSummary = AssistantTaskSummary(),
    val messages: List<AssistantMessage> = emptyList(),
    val progress: List<AssistantProgress> = emptyList(),
    @SerialName("pending_approvals") val pendingApprovals: List<AssistantApproval> = emptyList(),
    @SerialName("older_omitted") val olderOmitted: Boolean = false,
)

@Serializable
data class AssistantTaskList(val tasks: List<AssistantTaskSummary> = emptyList())

@Serializable
data class AssistantTaskResult(val task: AssistantTaskSummary = AssistantTaskSummary())

@Serializable
data class AssistantStopResult(@SerialName("was_running") val wasRunning: Boolean = false)

@Serializable
data class AssistantResolveResult(val resolved: Boolean = false, val outcome: String = "")

@Serializable
data class AssistantSubscription(val subscribed: Boolean = false, val status: AssistantStatus = AssistantStatus())

// --- Event payloads --------------------------------------------------------------------------------

@Serializable
data class AssistantMessageEvent(
    @SerialName("task_id") val taskId: String = "",
    val message: AssistantMessage = AssistantMessage(),
)

@Serializable
data class AssistantProgressEvent(
    @SerialName("task_id") val taskId: String = "",
    val event: AssistantProgress = AssistantProgress(),
)

@Serializable
data class AssistantApprovalRequestedEvent(val approval: AssistantApproval = AssistantApproval())

@Serializable
data class AssistantApprovalResolvedEvent(
    @SerialName("approval_id") val approvalId: String = "",
    @SerialName("task_id") val taskId: String? = null,
    val outcome: String = "",
)
