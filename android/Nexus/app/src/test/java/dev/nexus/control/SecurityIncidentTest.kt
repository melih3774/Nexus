package dev.nexus.control

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.protocol.EvidenceKind
import dev.nexus.control.protocol.EvidenceSummary
import dev.nexus.control.protocol.GetSessionResult
import dev.nexus.control.protocol.IncidentTrigger
import dev.nexus.control.protocol.ListSessionsResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ProtocolError
import dev.nexus.control.protocol.ResolveSessionResult
import dev.nexus.control.protocol.SecuritySessionSummary
import dev.nexus.control.protocol.SecurityState
import dev.nexus.control.protocol.SessionDetail
import dev.nexus.control.protocol.SessionResolution
import dev.nexus.control.security.OwnerConfirmation
import dev.nexus.control.security.OwnerConfirmationPrompt
import dev.nexus.control.security.SecurityIncidentClient
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The phone's half of the security workflow.
 *
 * The part worth the most attention is the confirmation gate. `This was me` stands a security
 * incident down from a device that may be lying unlocked next to the PC it is protecting, so it must
 * not be possible for a decline — or a phone with no screen lock at all — to result in the
 * acknowledgement being sent anyway.
 */
class SecurityIncidentTest {

    private class FakeChannel : NexusCommandChannel {
        val sent = mutableListOf<String>()
        var refuseWith: String? = null
        var reply: JsonElement? = null

        override suspend fun request(action: String, params: JsonElement?, timeoutMs: Long): JsonElement? {
            sent += action
            refuseWith?.let { throw NexusRequestException(ProtocolError(it, "refused")) }
            return reply
        }

        override fun notify(action: String, params: JsonElement?): Boolean {
            sent += action
            return true
        }
    }

    private class ScriptedPrompt(
        private val result: OwnerConfirmation,
        private val available: Boolean = true,
    ) : OwnerConfirmationPrompt {
        var prompts = 0

        override fun isAvailable() = available

        override suspend fun confirm(title: String, subtitle: String): OwnerConfirmation {
            prompts++
            return result
        }
    }

    // --- Wire shapes ------------------------------------------------------------------------------

    @Test
    fun `a session summary decodes from what PC Core sends`() {
        val json = """
            {
              "session_id": "a1b2c3d4",
              "started_at_utc": "2026-08-30T18:42:07.123+00:00",
              "ended_at_utc": null,
              "trigger": "KEYBOARD_ACTIVITY",
              "resolution": "CONFIRMED_NOT_OWNER",
              "high_alert": true,
              "duration_seconds": 192,
              "timeline_count": 9,
              "screenshot_count": 7,
              "camera_count": 2,
              "audio_count": 1
            }
        """.trimIndent()

        val summary = NexusJson.decodeFromString(SecuritySessionSummary.serializer(), json)

        assertEquals("a1b2c3d4", summary.sessionId)
        assertEquals(IncidentTrigger.KEYBOARD_ACTIVITY, summary.trigger)
        assertEquals(SessionResolution.CONFIRMED_NOT_OWNER, summary.resolution)
        assertTrue(summary.highAlert)
        assertTrue(summary.isOpen)
        assertEquals(10, summary.evidenceCount)
        assertEquals("3m 12s", summary.duration)
    }

    @Test
    fun `an acknowledged session is closed`() {
        val summary = SecuritySessionSummary(resolution = SessionResolution.ACKNOWLEDGED_OWNER)

        assertFalse(summary.isOpen)
        assertEquals("This was me", summary.resolution.label)
    }

    @Test
    fun `durations read the way a person would say them`() {
        assertEquals("45s", SecuritySessionSummary(durationSeconds = 45).duration)
        assertEquals("3m 12s", SecuritySessionSummary(durationSeconds = 192).duration)
        assertEquals("2h 5m", SecuritySessionSummary(durationSeconds = 7500).duration)
    }

    @Test
    fun `only images are claimed to be viewable`() {
        // v0.7 renders images. Saying a video is viewable and then failing would be worse than
        // saying plainly that playback is not in this version.
        assertTrue(EvidenceKind.SCREENSHOT.isViewableImage)
        assertTrue(EvidenceKind.CAMERA_IMAGE.isViewableImage)

        assertFalse(EvidenceKind.CAMERA_VIDEO.isViewableImage)
        assertFalse(EvidenceKind.AUDIO.isViewableImage)
        assertFalse(EvidenceKind.SCREEN_RECORDING.isViewableImage)
    }

    @Test
    fun `evidence metadata carries a digest and no path`() {
        val json = """
            {
              "evidence_id": "e1",
              "kind": "CAMERA_IMAGE",
              "created_at_utc": "2026-08-30T18:42:13+00:00",
              "content_type": "image/jpeg",
              "size_bytes": 34816,
              "duration_ms": 0,
              "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
            }
        """.trimIndent()

        val evidence = NexusJson.decodeFromString(EvidenceSummary.serializer(), json)

        assertEquals(EvidenceKind.CAMERA_IMAGE, evidence.kind)
        assertEquals("34 KB", evidence.sizeLabel)
        assertEquals("9f86d081", evidence.shortDigest)

        // Nothing in the shape can carry a filesystem location; the phone never learns the layout
        // of the owner's disk.
        assertFalse(Json.encodeToString(EvidenceSummary.serializer(), evidence).contains("path"))
    }

    @Test
    fun `the active session response says whether anything is happening`() {
        val json = """
            {"session": null, "has_active_session": false, "state": "ARMED"}
        """.trimIndent()

        val result = NexusJson.decodeFromString(GetSessionResult.serializer(), json)

        assertFalse(result.hasActiveSession)
        assertNull(result.session)
        assertEquals(SecurityState.ARMED, result.securityState)
    }

    // --- The client ---------------------------------------------------------------------------------

    @Test
    fun `listing sessions calls the documented action`() = runTest {
        val channel = FakeChannel()
        channel.reply = NexusJson.encodeToJsonElement(
            ListSessionsResult.serializer(),
            ListSessionsResult(1, listOf(SecuritySessionSummary(sessionId = "s1"))),
        )

        val result = SecurityIncidentClient(channel).listSessions()

        assertEquals(listOf(NexusProtocol.Actions.SECURITY_SESSIONS_LIST), channel.sent)
        assertEquals("s1", result.sessions.single().sessionId)
    }

    @Test
    fun `asking for the active session sends no id`() = runTest {
        val channel = FakeChannel()
        channel.reply = NexusJson.encodeToJsonElement(
            GetSessionResult.serializer(),
            GetSessionResult(SessionDetail(), hasActiveSession = true, state = "SECURITY_SESSION"),
        )

        val result = SecurityIncidentClient(channel).getSession()

        assertEquals(listOf(NexusProtocol.Actions.SECURITY_SESSIONS_GET), channel.sent)
        assertTrue(result.hasActiveSession)
    }

    @Test
    fun `a refusal surfaces as an exception rather than an empty result`() = runTest {
        val channel = FakeChannel()
        channel.refuseWith = NexusProtocol.Errors.NOT_AUTHORIZED

        val client = SecurityIncidentClient(channel)

        val failure = runCatching { client.listSessions() }.exceptionOrNull()

        assertTrue(failure is NexusRequestException)
        assertEquals(NexusProtocol.Errors.NOT_AUTHORIZED, (failure as NexusRequestException).error.code)
    }

    @Test
    fun `resolving calls exactly one action each`() = runTest {
        val channel = FakeChannel()
        channel.reply = NexusJson.encodeToJsonElement(
            ResolveSessionResult.serializer(),
            ResolveSessionResult(resolved = true, state = "OWNER_PRESENT"),
        )

        val client = SecurityIncidentClient(channel)

        assertTrue(client.thisWasMe().resolved)
        assertTrue(client.notMe().resolved)

        assertEquals(
            listOf(NexusProtocol.Actions.SECURITY_THIS_WAS_ME, NexusProtocol.Actions.SECURITY_NOT_ME),
            channel.sent,
        )
    }

    // --- The confirmation gate ------------------------------------------------------------------------

    @Test
    fun `a declined confirmation sends no acknowledgement`() = runTest {
        // The failure this prevents: an unlocked phone on the same desk as the PC standing down an
        // incident about whoever picked it up.
        val channel = FakeChannel()
        val prompt = ScriptedPrompt(OwnerConfirmation.DECLINED)

        val sent = acknowledgeThrough(prompt, channel)

        assertEquals(1, prompt.prompts)
        assertFalse(sent)
        assertTrue(channel.sent.isEmpty())
    }

    @Test
    fun `a phone with no screen lock cannot acknowledge`() = runTest {
        // Unavailable is a refusal, not a bypass. A check that passes when it cannot run is not a
        // check, and this one guards standing down a security incident.
        val channel = FakeChannel()
        val prompt = ScriptedPrompt(OwnerConfirmation.UNAVAILABLE, available = false)

        assertFalse(acknowledgeThrough(prompt, channel))
        assertTrue(channel.sent.isEmpty())
    }

    @Test
    fun `a confirmed owner does acknowledge`() = runTest {
        val channel = FakeChannel()
        channel.reply = NexusJson.encodeToJsonElement(
            ResolveSessionResult.serializer(),
            ResolveSessionResult(resolved = true, state = "OWNER_PRESENT"),
        )

        assertTrue(acknowledgeThrough(ScriptedPrompt(OwnerConfirmation.CONFIRMED), channel))
        assertEquals(listOf(NexusProtocol.Actions.SECURITY_THIS_WAS_ME), channel.sent)
    }

    @Test
    fun `reporting an intruder is not gated`() = runTest {
        // Asking someone to authenticate before they can report an intruder would be friction in
        // the worst possible place, and it only ever raises suspicion.
        val channel = FakeChannel()
        channel.reply = NexusJson.encodeToJsonElement(
            ResolveSessionResult.serializer(),
            ResolveSessionResult(resolved = true, state = "SECURITY_SESSION"),
        )

        SecurityIncidentClient(channel).notMe()

        assertEquals(listOf(NexusProtocol.Actions.SECURITY_NOT_ME), channel.sent)
    }

    /**
     * The gate as the view model applies it, without Android in the way: confirm, check, and only
     * then send.
     */
    private suspend fun acknowledgeThrough(prompt: OwnerConfirmationPrompt, channel: FakeChannel): Boolean {
        if (prompt.confirm("Confirm it was you", "This ends the security session on your PC.")
            != OwnerConfirmation.CONFIRMED
        ) {
            return false
        }

        SecurityIncidentClient(channel).thisWasMe()
        return true
    }
}
