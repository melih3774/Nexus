package dev.nexus.control

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.protocol.InputAck
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusKey
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.PointerButton
import dev.nexus.control.protocol.ProtocolError
import dev.nexus.control.remote.DesktopPoint
import dev.nexus.control.remote.RemoteControlSession
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The phone's half of a control session.
 *
 * Two things are worth the test cost. Modifier ordering, because a latched Ctrl that goes down and
 * never comes up turns every subsequent keystroke into a shortcut; and cleanup, because the moment
 * it matters is a dropped connection — exactly when nothing can be sent to fix it.
 */
class RemoteControlSessionTest {

    /** Records what would have been sent, and can be told to refuse. */
    private class FakeChannel : NexusCommandChannel {
        val sent = mutableListOf<Pair<String, JsonElement?>>()
        var refuseWith: String? = null

        /** When set, only this action is refused. Otherwise every request is. */
        var refuseOnly: String? = null

        var ack: InputAck = InputAck()

        override suspend fun request(action: String, params: JsonElement?, timeoutMs: Long): JsonElement? {
            sent += action to params

            refuseWith?.takeIf { refuseOnly == null || refuseOnly == action || describes(action, params) }
                ?.let { throw NexusRequestException(ProtocolError(it, "refused")) }

            return NexusJson.encodeToJsonElement(InputAck.serializer(), ack)
        }

        override fun notify(action: String, params: JsonElement?): Boolean {
            sent += action to params
            return true
        }

        /** True when this is the specific key press the test wants refused, rather than a modifier. */
        private fun describes(action: String, params: JsonElement?): Boolean =
            action == NexusProtocol.Actions.INPUT_KEY &&
                params?.jsonObject?.get("key")?.jsonPrimitive?.content == refuseOnly

        fun actions() = sent.map { it.first }

        fun keysOf(action: String) = sent.filter { it.first == action }.map {
            it.second!!.jsonObject["key"]!!.jsonPrimitive.content to
                it.second!!.jsonObject["action"]!!.jsonPrimitive.content
        }
    }

    @Test
    fun `a pointer move is sent without waiting for a reply`() = runTest {
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.moveTo(DesktopPoint(0.25, 0.75), displayIndex = 1)

        val (action, params) = channel.sent.single()
        assertEquals(NexusProtocol.Actions.INPUT_POINTER, action)

        val body = params!!.jsonObject
        assertEquals("ABSOLUTE", body["mode"]!!.jsonPrimitive.content)
        assertEquals(0.25, body["x"]!!.jsonPrimitive.content.toDouble(), 0.0001)
        assertEquals(1, body["display_index"]!!.jsonPrimitive.content.toInt())
    }

    @Test
    fun `a zero relative move sends nothing`() = runTest {
        val channel = FakeChannel()
        RemoteControlSession(channel).moveBy(0.0, 0.0)

        assertTrue(channel.sent.isEmpty())
    }

    @Test
    fun `a zero scroll sends nothing`() = runTest {
        val channel = FakeChannel()
        RemoteControlSession(channel).scroll(0.0, 0.0)

        assertTrue(channel.sent.isEmpty())
    }

    @Test
    fun `a latched modifier goes down before the key and up after it`() = runTest {
        // Ctrl+C on a touchscreen: there is no way to hold one key and tap another, so the modifier
        // latches and is applied around the next action.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.CONTROL)
        session.pressKey(NexusKey.C)

        assertEquals(
            listOf("CONTROL" to "DOWN", "C" to "PRESS", "CONTROL" to "UP"),
            channel.keysOf(NexusProtocol.Actions.INPUT_KEY),
        )
    }

    @Test
    fun `a latch is spent by the action it modified`() = runTest {
        // Otherwise Ctrl would stay on and the next letter would be another shortcut.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.CONTROL)
        session.pressKey(NexusKey.C)

        assertTrue(session.activeModifiers.isEmpty())

        session.pressKey(NexusKey.V)

        assertEquals(
            listOf("CONTROL" to "DOWN", "C" to "PRESS", "CONTROL" to "UP", "V" to "PRESS"),
            channel.keysOf(NexusProtocol.Actions.INPUT_KEY),
        )
    }

    @Test
    fun `several latched modifiers are released in reverse order`() = runTest {
        // Ctrl+Shift+Esc. Releasing in the order they were pressed can register as a different
        // chord on the way out.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.CONTROL)
        session.toggleModifier(NexusKey.SHIFT)
        session.pressKey(NexusKey.ESCAPE)

        assertEquals(
            listOf(
                "CONTROL" to "DOWN",
                "SHIFT" to "DOWN",
                "ESCAPE" to "PRESS",
                "SHIFT" to "UP",
                "CONTROL" to "UP",
            ),
            channel.keysOf(NexusProtocol.Actions.INPUT_KEY),
        )
    }

    @Test
    fun `a modifier is released even when the key it modified fails`() = runTest {
        // The failure this prevents: a refused Ctrl+C leaving Ctrl down, after which every
        // keystroke is a shortcut and nothing on the phone shows why.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.CONTROL)

        // Only the C press is refused; the Ctrl that went down before it really is held on the PC.
        channel.refuseWith = NexusProtocol.Errors.RATE_LIMITED
        channel.refuseOnly = "C"

        runCatching { session.pressKey(NexusKey.C) }

        assertEquals(
            listOf("CONTROL" to "DOWN", "C" to "PRESS", "CONTROL" to "UP"),
            channel.keysOf(NexusProtocol.Actions.INPUT_KEY),
        )

        assertTrue(session.activeModifiers.isEmpty())
    }

    @Test
    fun `a modifier that never went down is not released, and the latch is still cleared`() = runTest {
        // The other half: if pressing Ctrl itself is refused, nothing is held on the PC and sending
        // a stray key-up would be inventing an event. The latch still has to go, or the next
        // keystroke would silently be a shortcut.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.CONTROL)
        channel.refuseWith = NexusProtocol.Errors.RATE_LIMITED

        runCatching { session.pressKey(NexusKey.C) }

        assertEquals(listOf("CONTROL" to "DOWN"), channel.keysOf(NexusProtocol.Actions.INPUT_KEY))
        assertTrue(session.activeModifiers.isEmpty())
    }

    @Test
    fun `toggling a modifier twice clears it`() = runTest {
        val session = RemoteControlSession(FakeChannel())

        assertTrue(session.toggleModifier(NexusKey.ALT))
        assertEquals(setOf(NexusKey.ALT), session.activeModifiers)

        assertFalse(session.toggleModifier(NexusKey.ALT))
        assertTrue(session.activeModifiers.isEmpty())
    }

    @Test
    fun `a modifier applies to a click as well as a key`() = runTest {
        // Ctrl+click and Shift+click are ordinary things to want.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.SHIFT)
        session.click(PointerButton.LEFT)

        assertEquals(
            listOf(
                NexusProtocol.Actions.INPUT_KEY,
                NexusProtocol.Actions.INPUT_BUTTON,
                NexusProtocol.Actions.INPUT_KEY,
            ),
            channel.actions(),
        )
    }

    @Test
    fun `typing is not wrapped in latched modifiers`() = runTest {
        // Ctrl plus a paragraph is not a thing anyone means; applying it would turn a paste into a
        // burst of shortcuts.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.toggleModifier(NexusKey.CONTROL)
        session.type("hello")

        assertEquals(listOf(NexusProtocol.Actions.INPUT_TEXT), channel.actions())
    }

    @Test
    fun `empty text sends nothing`() = runTest {
        val channel = FakeChannel()
        RemoteControlSession(channel).type("")

        assertTrue(channel.sent.isEmpty())
    }

    @Test
    fun `pressing a modifier key latches it instead of sending it`() = runTest {
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.pressKey(NexusKey.META)

        assertTrue(channel.sent.isEmpty())
        assertEquals(setOf(NexusKey.META), session.activeModifiers)
    }

    @Test
    fun `ending input clears held state even when the request fails`() = runTest {
        // The case that matters: the connection is already gone, so telling the PC fails — and the
        // phone must still stop believing Ctrl is held.
        val channel = FakeChannel()
        val session = RemoteControlSession(channel)

        session.beginInput()
        session.toggleModifier(NexusKey.CONTROL)

        channel.refuseWith = NexusProtocol.Errors.INTERNAL_ERROR
        session.endInput()

        assertTrue(session.activeModifiers.isEmpty())
        assertFalse(session.isInputActive)
    }

    @Test
    fun `forgetting held state needs no connection at all`() = runTest {
        val session = RemoteControlSession(FakeChannel())

        session.beginInput()
        session.toggleModifier(NexusKey.SHIFT)

        session.forgetHeldState()

        assertTrue(session.activeModifiers.isEmpty())
        assertFalse(session.isInputActive)
    }

    @Test
    fun `a refusal by Windows is surfaced rather than thrown`() = runTest {
        val channel = FakeChannel()
        channel.ack = InputAck(delivered = false, delivery = "BLOCKED_BY_INTEGRITY", diagnostic = "elevated window")

        val session = RemoteControlSession(channel)
        session.click()

        assertNotNull(session.lastRefusal)
        assertTrue(session.lastRefusal!!.contains("elevated", ignoreCase = true))
    }

    @Test
    fun `a delivered action clears a previous refusal`() = runTest {
        val channel = FakeChannel()
        channel.ack = InputAck(delivered = false, delivery = "BLOCKED_BY_INTEGRITY", diagnostic = "elevated window")

        val session = RemoteControlSession(channel)
        session.click()

        channel.ack = InputAck()
        session.click()

        assertNull(session.lastRefusal)
    }

    @Test
    fun `beginning input reports failure rather than throwing`() = runTest {
        val channel = FakeChannel()
        channel.refuseWith = NexusProtocol.Errors.NOT_AUTHORIZED

        assertFalse(RemoteControlSession(channel).beginInput())
    }
}
