package dev.nexus.control

import dev.nexus.control.clipboard.ClipboardSync
import dev.nexus.control.clipboard.PullDecision
import dev.nexus.control.clipboard.PushDecision
import dev.nexus.control.files.ClipboardSetResult
import dev.nexus.control.files.PcClipboard
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's half of stopping clipboard text bouncing between the phone and the PC. */
class ClipboardSyncTest {

    private val sync = ClipboardSync()

    private fun pc(text: String?, origin: String = PcClipboard.ORIGIN_PC, version: Long = 1) =
        PcClipboard(hasText = text != null, text = text, length = text?.length ?: 0, version = version, origin = origin)

    @Test
    fun `text copied on the PC is copied to the phone`() {
        val decision = sync.pulled(pc("from the PC"))

        assertEquals(PullDecision.CopyToPhone("from the PC", truncated = false), decision)
    }

    @Test
    fun `copying the same text again does not write the phone's clipboard a second time`() {
        val first = sync.pulled(pc("same")) as PullDecision.CopyToPhone
        sync.copiedToPhone(first.text)

        assertEquals(PullDecision.AlreadyOnPhone(fromThisPhone = false), sync.pulled(pc("same")))
    }

    @Test
    fun `text this phone sent comes back as its own, not as new text`() {
        // The owner opened the send box with the phone's clipboard in it and sent that.
        val push = sync.push("from the phone", phoneClipboard = "from the phone")
        assertEquals(PushDecision.Send("from the phone"), push)

        val back = sync.pulled(pc("from the phone", origin = PcClipboard.ORIGIN_PHONE, version = 2))

        assertEquals(PullDecision.AlreadyOnPhone(fromThisPhone = true), back)
    }

    @Test
    fun `a round trip settles instead of looping`() {
        // PC -> phone.
        val pulled = sync.pulled(pc("ping")) as PullDecision.CopyToPhone
        sync.copiedToPhone(pulled.text)

        // Phone -> PC with exactly that text: sent, and the PC answers that nothing changed.
        val pushed = sync.push("ping", phoneClipboard = "ping")
        assertTrue(pushed is PushDecision.Send)
        assertEquals("The PC's clipboard already had this text.", sync.pushed(ClipboardSetResult(changed = false, version = 1, length = 4)))

        // PC -> phone again: nothing to write. Every step after the first was a no-op on both ends.
        assertTrue(sync.pulled(pc("ping", version = 1)) is PullDecision.AlreadyOnPhone)
    }

    @Test
    fun `typed text that is not on the phone's clipboard is still copied back when asked`() {
        sync.push("typed in the box", phoneClipboard = "something else")

        val back = sync.pulled(pc("typed in the box", origin = PcClipboard.ORIGIN_PHONE))

        assertEquals(PullDecision.CopyToPhone("typed in the box", truncated = false), back)
    }

    @Test
    fun `new text on the PC is copied even after an earlier copy`() {
        sync.copiedToPhone("old")

        assertEquals(PullDecision.CopyToPhone("new", truncated = false), sync.pulled(pc("new", version = 5)))
    }

    @Test
    fun `nothing to copy, nothing to send, too much to send`() {
        assertEquals(PullDecision.Empty, sync.pulled(pc(null)))
        assertEquals(PushDecision.Empty, sync.push("", phoneClipboard = null))
        assertEquals(PushDecision.TooLong, sync.push("x".repeat(PcClipboard.MAX_TEXT_CHARS + 1), phoneClipboard = null))
    }
}
