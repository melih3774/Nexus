package dev.nexus.control.clipboard

import dev.nexus.control.files.ClipboardSetResult
import dev.nexus.control.files.PcClipboard
import java.security.MessageDigest

/** What to do with the PC's clipboard now that the owner asked for it. */
sealed interface PullDecision {

    /** Put this on the phone's clipboard. */
    data class CopyToPhone(val text: String, val truncated: Boolean) : PullDecision

    /** The phone's clipboard already holds exactly this; writing it again would be an echo. */
    data class AlreadyOnPhone(val fromThisPhone: Boolean) : PullDecision

    data object Empty : PullDecision
}

sealed interface PushDecision {
    data class Send(val text: String) : PushDecision
    data object Empty : PushDecision
    data object TooLong : PushDecision
}

/**
 * The phone's half of keeping clipboard text from bouncing.
 *
 * PC Core already refuses to treat identical text as a change, and says when its clipboard still holds
 * what this phone sent. This remembers the other side: what Nexus knows is on the *phone's* clipboard,
 * so text that just came from here - or was just copied here - is not written to it again. Every write
 * to the phone's clipboard is an event other apps and Android's own clipboard overlay react to, and one
 * that repeats the same text is the start of a loop the moment anything syncs automatically.
 *
 * Only hashes are kept, in memory. Clipboards hold passwords.
 */
class ClipboardSync {

    private var onPhone: String? = null

    /** The PC answered `clipboard.get`. */
    fun pulled(pc: PcClipboard): PullDecision {
        val text = pc.text
        if (!pc.hasText || text.isNullOrEmpty()) return PullDecision.Empty

        if (hash(text) == onPhone) {
            return PullDecision.AlreadyOnPhone(fromThisPhone = pc.origin == PcClipboard.ORIGIN_PHONE)
        }

        return PullDecision.CopyToPhone(text, pc.truncated)
    }

    /** The text was written to the phone's clipboard. */
    fun copiedToPhone(text: String) {
        onPhone = hash(text)
    }

    /**
     * About to send [text]. [phoneClipboard] is what the phone's clipboard held when the owner opened
     * the send box, if it was read: sending that exact text means the phone still has it.
     */
    fun push(text: String, phoneClipboard: String?): PushDecision {
        if (text.isEmpty()) return PushDecision.Empty
        if (text.length > PcClipboard.MAX_TEXT_CHARS) return PushDecision.TooLong

        if (text == phoneClipboard) onPhone = hash(text)

        return PushDecision.Send(text)
    }

    /** What to tell the owner after PC Core answered `clipboard.set`. */
    fun pushed(result: ClipboardSetResult): String =
        if (result.changed) {
            "Sent ${result.length} characters to the PC's clipboard."
        } else {
            "The PC's clipboard already had this text."
        }

    private fun hash(text: String): String =
        MessageDigest.getInstance("SHA-256").digest(text.toByteArray()).joinToString("") { "%02x".format(it) }
}
