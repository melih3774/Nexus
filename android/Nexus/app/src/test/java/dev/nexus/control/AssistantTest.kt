package dev.nexus.control

import dev.nexus.control.assistant.AssistantApproval
import dev.nexus.control.assistant.AssistantAvailability
import dev.nexus.control.assistant.AssistantClient
import dev.nexus.control.assistant.AssistantConversation
import dev.nexus.control.assistant.AssistantMessage
import dev.nexus.control.assistant.AssistantMessageEvent
import dev.nexus.control.assistant.AssistantProgress
import dev.nexus.control.assistant.AssistantProgressEvent
import dev.nexus.control.assistant.AssistantStatus
import dev.nexus.control.assistant.AssistantTaskDetail
import dev.nexus.control.assistant.AssistantTaskSummary
import dev.nexus.control.assistant.AssistantViewModel
import dev.nexus.control.assistant.OpenTask
import dev.nexus.control.assistant.TimelineItem
import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ProtocolError
import dev.nexus.control.security.LockReason
import dev.nexus.control.security.SessionGateState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's half of PC Assistant: what it sends, and how pushed events change an open task. */
class AssistantTest {

    private val task = AssistantTaskSummary(id = "t1", title = "Plan", mode = "ask", state = "running", running = true, updatedAt = "2026-09-14T10:00:00+00:00")

    private fun message(id: String, text: String, at: String, role: String = "assistant") =
        AssistantMessage(id = id, role = role, text = text, createdAt = at)

    // --- Conversation ------------------------------------------------------------------------------

    @Test
    fun `a growing reply replaces itself instead of repeating`() {
        var open = OpenTask(task)

        open = AssistantConversation.withMessage(open, AssistantMessageEvent("t1", message("m1", "Hel", "2026-09-14T10:00:01+00:00")))
        open = AssistantConversation.withMessage(open, AssistantMessageEvent("t1", message("m1", "Hello there", "2026-09-14T10:00:01+00:00")))

        assertEquals(1, open.messages.size)
        assertEquals("Hello there", open.messages.single().text)
    }

    @Test
    fun `events for another task do not leak into the open one`() {
        val open = OpenTask(task)

        val after = AssistantConversation.withMessage(open, AssistantMessageEvent("other", message("m1", "not yours", "2026-09-14T10:00:01+00:00")))
        val progressed = AssistantConversation.withProgress(open, AssistantProgressEvent("other", AssistantProgress(id = "p1", kind = "tool_started", title = "Read")))
        val approved = AssistantConversation.withApproval(open, AssistantApproval(id = "a1", taskId = "other"))

        assertEquals(open, after)
        assertEquals(open, progressed)
        assertEquals(open, approved)
    }

    @Test
    fun `an approval card appears once and goes when it is answered`() {
        val card = AssistantApproval(id = "a1", taskId = "t1", action = "Claude wants to run", target = "git status", remoteResolvable = true)

        var open = AssistantConversation.withApproval(OpenTask(task), card)
        open = AssistantConversation.withApproval(open, card)
        assertEquals(1, open.approvals.size)

        open = AssistantConversation.withoutApproval(open, "a1")
        assertTrue(open.approvals.isEmpty())
    }

    @Test
    fun `the timeline interleaves messages and tool steps in time order and leaves out what has its own place`() {
        val open = OpenTask(
            task,
            messages = listOf(message("m2", "done", "2026-09-14T10:00:03+00:00"), message("m1", "hi", "2026-09-14T10:00:00+00:00", role = "owner")),
            progress = listOf(
                AssistantProgress(id = "p1", kind = "tool_started", title = "Read notes", createdAt = "2026-09-14T10:00:01.5+00:00"),
                AssistantProgress(id = "p2", kind = "approval_required", title = "card", createdAt = "2026-09-14T10:00:02+00:00"),
                AssistantProgress(id = "p3", kind = "task_completed", title = "Done", createdAt = "2026-09-14T10:00:04+00:00"),
            ),
        )

        assertEquals(listOf("m:m1", "p:p1", "m:m2"), AssistantConversation.timeline(open).map(TimelineItem::key))
    }

    @Test
    fun `a tool that finished is one line, not the same line twice`() {
        val open = OpenTask(
            task,
            progress = listOf(
                AssistantProgress(id = "p1", kind = "tool_started", title = "Write: C:\\notes.txt", toolName = "Write", createdAt = "2026-09-14T10:00:01+00:00"),
                AssistantProgress(id = "p2", kind = "tool_completed", title = "Write", toolName = "Write", createdAt = "2026-09-14T10:00:02+00:00"),
            ),
        )

        val steps = AssistantConversation.timeline(open)
        assertEquals(listOf("p:p1"), steps.map(TimelineItem::key))
    }

    @Test
    fun `a finish without its start still shows, and a failure always does`() {
        val open = OpenTask(
            task,
            progress = listOf(
                // The transcript was truncated before this tool's start, so its finish is all there is.
                AssistantProgress(id = "p1", kind = "tool_completed", title = "Read", toolName = "Read", createdAt = "2026-09-14T10:00:01+00:00"),
                AssistantProgress(id = "p2", kind = "tool_started", title = "Write: C:\\notes.txt", toolName = "Write", createdAt = "2026-09-14T10:00:02+00:00"),
                AssistantProgress(id = "p3", kind = "tool_failed", title = "Write failed", toolName = "Write", createdAt = "2026-09-14T10:00:03+00:00"),
            ),
        )

        assertEquals(listOf("p:p1", "p:p2", "p:p3"), AssistantConversation.timeline(open).map(TimelineItem::key))
    }

    @Test
    fun `a fresh read keeps a reply the stream has already grown further`() {
        val live = OpenTask(task, messages = listOf(message("m1", "Hello there, this is longer", "2026-09-14T10:00:01+00:00")))
        val stored = AssistantTaskDetail(task = task, messages = listOf(message("m0", "question", "2026-09-14T10:00:00+00:00", "owner"), message("m1", "Hello", "2026-09-14T10:00:01+00:00")))

        val merged = AssistantConversation.merge(live, stored)

        assertEquals(listOf("question", "Hello there, this is longer"), merged.messages.map { it.text })
    }

    @Test
    fun `the task list keeps one row per task, newest first`() {
        val older = task.copy(id = "t0", updatedAt = "2026-09-14T09:00:00+00:00")
        val updated = task.copy(state = "completed", running = false, updatedAt = "2026-09-14T11:00:00+00:00")

        val list = AssistantConversation.upsert(listOf(older, task), updated)

        assertEquals(listOf("t1", "t0"), list.map { it.id })
        assertEquals("completed", list.first().state)
        assertTrue(AssistantConversation.isFinished(list.first()))
    }

    // --- Status ------------------------------------------------------------------------------------

    @Test
    fun `an unknown state from a newer PC Core reads as unavailable, not as ready`() {
        val status = AssistantStatus(state = "SOMETHING_NEW")

        assertEquals(AssistantAvailability.Unavailable, status.availability)
        assertFalse(status.canRead)
    }

    @Test
    fun `tasks can be read while Claude is signed out, but not started`() {
        val status = AssistantStatus(state = "CLAUDE_NOT_READY", canWork = false)

        assertTrue(status.canRead)
        assertFalse(status.canWork)
    }

    // --- Client ------------------------------------------------------------------------------------

    private class FakeChannel : NexusCommandChannel {
        val sent = mutableListOf<Pair<String, JsonObject?>>()
        var reply: JsonElement? = null
        var refuseWith: String? = null

        override suspend fun request(action: String, params: JsonElement?, timeoutMs: Long): JsonElement? {
            sent += action to params?.jsonObject
            refuseWith?.let { throw NexusRequestException(ProtocolError(it, "refused")) }
            return reply
        }

        override fun notify(action: String, params: JsonElement?): Boolean {
            sent += action to params?.jsonObject
            return true
        }
    }

    @Test
    fun `starting a task sends a General task with its mode and prompt`() = runBlocking {
        val channel = FakeChannel().apply { reply = NexusJson.parseToJsonElement("""{"task":{"id":"t9","title":"x","mode":"ask","state":"running"}}""") }

        val created = AssistantClient(channel).create("ask", "plan my week")

        val (action, params) = channel.sent.single()
        assertEquals(NexusProtocol.Actions.ASSISTANT_TASKS_CREATE, action)
        assertEquals("general", params!!["kind"]!!.jsonPrimitive.content)
        assertEquals("ask", params["mode"]!!.jsonPrimitive.content)
        assertEquals("plan my week", params["prompt"]!!.jsonPrimitive.content)
        assertEquals("t9", created.task.id)
    }

    @Test
    fun `an answer is allow once or deny, never anything wider`() = runBlocking {
        val channel = FakeChannel().apply { reply = NexusJson.parseToJsonElement("""{"resolved":true,"outcome":"allowed"}""") }
        val client = AssistantClient(channel)

        client.resolve("a1", allow = true)
        client.resolve("a2", allow = false)

        assertEquals(listOf("allow_once", "deny"), channel.sent.map { it.second!!["decision"]!!.jsonPrimitive.content })
        assertEquals(listOf("a1", "a2"), channel.sent.map { it.second!!["approval_id"]!!.jsonPrimitive.content })
    }

    // --- The screen -------------------------------------------------------------------------------

    /** Answers each action with its own canned result, the way PC Core does. */
    private class ScriptedChannel(private val replies: Map<String, String>) : NexusCommandChannel {
        val sent = mutableListOf<String>()

        override suspend fun request(action: String, params: JsonElement?, timeoutMs: Long): JsonElement? {
            sent += action
            return replies[action]?.let(NexusJson::parseToJsonElement)
        }

        override fun notify(action: String, params: JsonElement?): Boolean {
            sent += action
            return true
        }
    }

    /** A scripted channel that can be made to refuse everything, the way an older PC Core would. */
    private class RefusingChannel(private val replies: Map<String, String>) : NexusCommandChannel {
        var refuseWith: String? = null

        override suspend fun request(action: String, params: JsonElement?, timeoutMs: Long): JsonElement? {
            refuseWith?.let { throw NexusRequestException(ProtocolError(it, "refused")) }
            return replies[action]?.let(NexusJson::parseToJsonElement)
        }

        override fun notify(action: String, params: JsonElement?): Boolean = true
    }

    private fun statusEvent(state: String, canWork: Boolean) = EventEnvelope(
        eventName = NexusProtocol.Events.ASSISTANT_STATUS_CHANGED,
        payload = NexusJson.parseToJsonElement("""{"state":"$state","can_work":$canWork}""").jsonObject,
    )

    private fun screen(channel: NexusCommandChannel, events: MutableSharedFlow<EventEnvelope>, gate: MutableStateFlow<SessionGateState>) =
        AssistantViewModel(AssistantClient(channel), events, gate, MutableStateFlow(0L))

    private val readyReplies = mapOf(
        NexusProtocol.Actions.ASSISTANT_SUBSCRIBE to """{"subscribed":true,"status":{"state":"READY","can_work":true}}""",
        NexusProtocol.Actions.ASSISTANT_TASKS_LIST to """{"tasks":[{"id":"t1","title":"Plan","mode":"ask","state":"running","running":true}]}""",
        NexusProtocol.Actions.ASSISTANT_TASKS_GET to """{"task":{"id":"t1","title":"Plan","mode":"ask","state":"running","running":true},"messages":[{"id":"m1","role":"owner","text":"plan my week"}]}""",
    )

    @OptIn(ExperimentalCoroutinesApi::class)
    @Test
    fun `PC Assistant closing and coming back puts the owner back in the task they were reading`() = runTest {
        Dispatchers.setMain(StandardTestDispatcher(testScheduler))
        try {
            val events = MutableSharedFlow<EventEnvelope>(extraBufferCapacity = 8)
            val gate = MutableStateFlow<SessionGateState>(SessionGateState.Unlocked(remoteAuthorized = true))
            val screen = screen(ScriptedChannel(readyReplies), events, gate)

            screen.start()
            advanceUntilIdle()
            screen.open("t1")
            advanceUntilIdle()
            assertEquals("t1", screen.state.value.open?.task?.id)

            // PC Assistant is closed: nothing read from it is still true, so it all goes.
            events.emit(statusEvent("NOT_RUNNING", canWork = false))
            advanceUntilIdle()
            assertEquals(null, screen.state.value.open)
            assertTrue(screen.state.value.tasks.isEmpty())

            events.emit(statusEvent("READY", canWork = true))
            advanceUntilIdle()
            assertEquals("t1", screen.state.value.open?.task?.id)
            assertEquals("plan my week", screen.state.value.open?.messages?.single()?.text)
        } finally {
            Dispatchers.resetMain()
        }
    }

    @OptIn(ExperimentalCoroutinesApi::class)
    @Test
    fun `a status that could not be read is not the last one that could`() = runTest {
        Dispatchers.setMain(StandardTestDispatcher(testScheduler))
        try {
            val events = MutableSharedFlow<EventEnvelope>(extraBufferCapacity = 8)
            val gate = MutableStateFlow<SessionGateState>(SessionGateState.Unlocked(remoteAuthorized = true))
            val channel = RefusingChannel(readyReplies)
            val screen = screen(channel, events, gate)

            screen.start()
            advanceUntilIdle()
            assertEquals(AssistantAvailability.Ready, screen.state.value.status?.availability)

            // An older PC Core: the action is gone, so what it said a minute ago says nothing now.
            channel.refuseWith = NexusProtocol.Errors.UNKNOWN_ACTION
            screen.refresh()
            advanceUntilIdle()

            assertEquals(null, screen.state.value.status)
            assertTrue(screen.state.value.error!!.contains("Update Nexus"))
        } finally {
            Dispatchers.resetMain()
        }
    }

    @OptIn(ExperimentalCoroutinesApi::class)
    @Test
    fun `a lock in between is not a place to come back to`() = runTest {
        Dispatchers.setMain(StandardTestDispatcher(testScheduler))
        try {
            val events = MutableSharedFlow<EventEnvelope>(extraBufferCapacity = 8)
            val gate = MutableStateFlow<SessionGateState>(SessionGateState.Unlocked(remoteAuthorized = true))
            val screen = screen(ScriptedChannel(readyReplies), events, gate)

            screen.start()
            advanceUntilIdle()
            screen.open("t1")
            advanceUntilIdle()

            events.emit(statusEvent("NOT_RUNNING", canWork = false))
            advanceUntilIdle()

            gate.value = SessionGateState.Locked(LockReason.ScreenLocked)
            advanceUntilIdle()

            events.emit(statusEvent("READY", canWork = true))
            advanceUntilIdle()

            // The owner has to ask for the task again, in the open, rather than find it waiting.
            assertEquals(null, screen.state.value.open)
        } finally {
            Dispatchers.resetMain()
        }
    }

    @Test
    fun `refusals are worded for the owner`() {
        fun described(code: String) = AssistantViewModel.describe(NexusRequestException(ProtocolError(code, "")))

        assertTrue(described(NexusProtocol.Errors.ASSISTANT_REQUIRES_LOCAL_APPROVAL).contains("on the PC"))
        assertTrue(described(NexusProtocol.Errors.ASSISTANT_TASK_BUSY).contains("still working"))
        assertTrue(described(NexusProtocol.Errors.NOT_AUTHORIZED).contains("Unlock"))
        assertTrue(described(NexusProtocol.Errors.UNKNOWN_ACTION).contains("Update Nexus"))
    }
}
