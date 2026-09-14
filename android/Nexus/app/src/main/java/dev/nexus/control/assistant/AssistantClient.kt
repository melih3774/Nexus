package dev.nexus.control.assistant

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

/**
 * Every PC Assistant call, and nothing that decides anything.
 *
 * The phone never talks to PC Assistant. These go to PC Core, which requires the owner session for each
 * one and relays it to PC Assistant, which applies its own AUTO/ASK policy and approval cards.
 */
class AssistantClient(private val connection: NexusCommandChannel) {

    suspend fun status(): AssistantStatus = call(NexusProtocol.Actions.ASSISTANT_STATUS, null, AssistantStatus.serializer())

    suspend fun subscribe(): AssistantSubscription =
        call(NexusProtocol.Actions.ASSISTANT_SUBSCRIBE, null, AssistantSubscription.serializer())

    /** Fire and forget: used on the way out, when nothing is left to report a failure to. */
    fun unsubscribe() {
        connection.notify(NexusProtocol.Actions.ASSISTANT_UNSUBSCRIBE)
    }

    suspend fun tasks(limit: Int = 40): AssistantTaskList =
        call(NexusProtocol.Actions.ASSISTANT_TASKS_LIST, encode(LimitParams.serializer(), LimitParams(limit)), AssistantTaskList.serializer())

    suspend fun task(taskId: String): AssistantTaskDetail = call(
        NexusProtocol.Actions.ASSISTANT_TASKS_GET,
        encode(TaskParams.serializer(), TaskParams(taskId)),
        AssistantTaskDetail.serializer(),
        timeoutMs = 45_000,
    )

    suspend fun create(mode: String, prompt: String): AssistantTaskResult = call(
        NexusProtocol.Actions.ASSISTANT_TASKS_CREATE,
        encode(CreateParams.serializer(), CreateParams(mode = mode, prompt = prompt)),
        AssistantTaskResult.serializer(),
        timeoutMs = 45_000,
    )

    suspend fun send(taskId: String, text: String): AssistantTaskResult = call(
        NexusProtocol.Actions.ASSISTANT_TASKS_SEND,
        encode(SendParams.serializer(), SendParams(taskId, text)),
        AssistantTaskResult.serializer(),
        timeoutMs = 45_000,
    )

    suspend fun stop(taskId: String): AssistantStopResult =
        call(NexusProtocol.Actions.ASSISTANT_TASKS_STOP, encode(TaskParams.serializer(), TaskParams(taskId)), AssistantStopResult.serializer())

    suspend fun setMode(taskId: String, mode: String): AssistantTaskResult = call(
        NexusProtocol.Actions.ASSISTANT_TASKS_SET_MODE,
        encode(ModeParams.serializer(), ModeParams(taskId, mode)),
        AssistantTaskResult.serializer(),
    )

    /** [allow] true is "Allow once"; there is no "allow for this task" from a phone. */
    suspend fun resolve(approvalId: String, allow: Boolean): AssistantResolveResult = call(
        NexusProtocol.Actions.ASSISTANT_APPROVALS_RESOLVE,
        encode(ResolveParams.serializer(), ResolveParams(approvalId, if (allow) DECISION_ALLOW_ONCE else DECISION_DENY)),
        AssistantResolveResult.serializer(),
    )

    private suspend fun <T> call(action: String, params: JsonElement?, serializer: KSerializer<T>, timeoutMs: Long = 20_000): T {
        val result = connection.request(action, params, timeoutMs) ?: throw IllegalStateException("$action returned no result")
        return NexusJson.decodeFromJsonElement(serializer, result)
    }

    private fun <T> encode(serializer: KSerializer<T>, value: T): JsonElement = NexusJson.encodeToJsonElement(serializer, value)

    @Serializable
    private data class LimitParams(val limit: Int)

    @Serializable
    private data class TaskParams(@SerialName("task_id") val taskId: String)

    @Serializable
    private data class CreateParams(val kind: String = "general", val mode: String, val prompt: String)

    @Serializable
    private data class SendParams(@SerialName("task_id") val taskId: String, val text: String)

    @Serializable
    private data class ModeParams(@SerialName("task_id") val taskId: String, val mode: String)

    @Serializable
    private data class ResolveParams(@SerialName("approval_id") val approvalId: String, val decision: String)

    companion object {
        const val DECISION_ALLOW_ONCE = "allow_once"
        const val DECISION_DENY = "deny"

        /** Matches PC Core's and PC Assistant's limit, so the phone refuses first rather than after a round trip. */
        const val MAX_PROMPT_CHARS = 32_000
    }
}
