package dev.nexus.control.remote

import android.util.Log
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.ButtonAction
import dev.nexus.control.protocol.CaptureScreenResult
import dev.nexus.control.protocol.ButtonParams
import dev.nexus.control.protocol.DisplayInfo
import dev.nexus.control.protocol.InputAck
import dev.nexus.control.protocol.KeyAction
import dev.nexus.control.protocol.KeyParams
import dev.nexus.control.protocol.ListDisplaysResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusKey
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.PointerButton
import dev.nexus.control.protocol.PointerMode
import dev.nexus.control.protocol.PointerParams
import dev.nexus.control.protocol.ScrollParams
import dev.nexus.control.protocol.StartStreamParams
import dev.nexus.control.protocol.StreamStarted
import dev.nexus.control.protocol.NativeInputStatus
import dev.nexus.control.protocol.TextParams
import dev.nexus.control.protocol.TouchAction
import dev.nexus.control.protocol.TouchParams
import kotlinx.coroutines.CancellationException

/**
 * The phone's half of a remote-control session: what is being streamed, and what is being typed.
 *
 * **Streaming and input are kept apart on purpose,** matching the PC. Opening the remote screen
 * starts a stream; it does not make the phone a keyboard. Input begins when the owner turns it on
 * and ends when they leave, disconnect, or anything goes wrong — and the two can be stopped
 * independently.
 *
 * **Everything held is tracked here as well as on the PC.** The PC releases what a dropped
 * connection left down, but the phone has to stop *believing* Ctrl is latched, or the next
 * keystroke after a reconnect would silently be a shortcut.
 */
class RemoteControlSession(private val connection: NexusCommandChannel) {

    /** Modifiers the owner has latched in the toolbar. Applied around the next key or click. */
    private val latched = linkedSetOf<NexusKey>()

    private var inputActive = false
    private var pointerDown = false
    private var contactDown = false

    /** Set when Windows refuses input, so the screen can explain rather than appear broken. */
    var lastRefusal: String? = null
        private set

    val activeModifiers: Set<NexusKey> get() = latched.toSet()

    val isInputActive: Boolean get() = inputActive

    // --- Streaming ---------------------------------------------------------------------------------

    suspend fun listDisplays(): List<DisplayInfo> {
        val result = connection.request(NexusProtocol.Actions.LIST_DISPLAYS) ?: return emptyList()
        return NexusJson.decodeFromJsonElement(ListDisplaysResult.serializer(), result).displays
    }

    suspend fun startStream(displayIndex: Int? = null): StreamStarted {
        val result = connection.request(
            NexusProtocol.Actions.STREAM_START,
            NexusJson.encodeToJsonElement(StartStreamParams.serializer(), StartStreamParams(displayIndex)),
        ) ?: throw IllegalStateException("The PC did not describe the stream it started.")

        return NexusJson.decodeFromJsonElement(StreamStarted.serializer(), result)
    }

    /**
     * One screenshot, through the capability that has nothing to do with streaming.
     *
     * This is the fallback when a live stream cannot start — a display that cannot be duplicated,
     * another application already holding the duplication — and it works precisely because the two
     * paths share no code. A streaming fault does not take the screenshot that diagnoses it.
     */
    suspend fun captureStill(maxWidth: Int): CaptureScreenResult {
        val result = connection.request(
            NexusProtocol.Actions.CAPTURE_SCREEN,
            NexusJson.encodeToJsonElement(
                kotlinx.serialization.json.JsonObject.serializer(),
                kotlinx.serialization.json.buildJsonObject {
                    put("max_width", kotlinx.serialization.json.JsonPrimitive(maxWidth))
                },
            ),
        ) ?: throw IllegalStateException("The PC returned no screenshot.")

        return NexusJson.decodeFromJsonElement(CaptureScreenResult.serializer(), result)
    }

    /** Best-effort: the stream is stopping either way, and a failure here must not surface as an error. */
    suspend fun stopStream() {
        quietly { connection.request(NexusProtocol.Actions.STREAM_STOP) }
    }

    // --- Input session -----------------------------------------------------------------------------

    suspend fun beginInput(): Boolean {
        return try {
            connection.request(NexusProtocol.Actions.INPUT_BEGIN)
            inputActive = true
            lastRefusal = null
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.d(TAG, "Could not enable input: ${e.javaClass.simpleName}")
            false
        }
    }

    /**
     * Ends input and forgets everything the phone believed was held.
     *
     * Called on leaving the screen, on backgrounding, and on disconnect. The local state is cleared
     * even when the request fails, because a failed request usually means the connection is gone —
     * exactly when a stale latched Ctrl would do the most damage.
     */
    suspend fun endInput() {
        quietly { connection.request(NexusProtocol.Actions.INPUT_END) }
        forgetHeldState()
    }

    /** Clears held state without ending the session. For an interrupted gesture. */
    suspend fun releaseAll() {
        quietly { connection.request(NexusProtocol.Actions.INPUT_RELEASE_ALL) }
        latched.clear()
        pointerDown = false
        contactDown = false
    }

    /** Local-only cleanup, for when there is no connection left to tell. */
    fun forgetHeldState() {
        inputActive = false
        pointerDown = false
        contactDown = false
        latched.clear()
    }

    // --- Pointer ------------------------------------------------------------------------------------

    /**
     * Moves the pointer to a position on the display, as a fraction of it.
     *
     * Sent without awaiting a reply: during a drag this arrives as fast as fingers move, and a round
     * trip per move would put the network between the finger and the cursor.
     */
    fun moveTo(point: DesktopPoint, displayIndex: Int) {
        connection.notify(
            NexusProtocol.Actions.INPUT_POINTER,
            NexusJson.encodeToJsonElement(
                PointerParams.serializer(),
                PointerParams(
                    mode = PointerMode.ABSOLUTE,
                    x = point.x,
                    y = point.y,
                    displayIndex = displayIndex,
                ),
            ),
        )
    }

    fun moveBy(dx: Double, dy: Double) {
        if (dx == 0.0 && dy == 0.0) return

        connection.notify(
            NexusProtocol.Actions.INPUT_POINTER,
            NexusJson.encodeToJsonElement(
                PointerParams.serializer(),
                PointerParams(mode = PointerMode.RELATIVE, dx = dx, dy = dy),
            ),
        )
    }

    suspend fun click(button: PointerButton = PointerButton.LEFT) = withModifiers {
        send(NexusProtocol.Actions.INPUT_BUTTON, ButtonParams.serializer(), ButtonParams(button, ButtonAction.CLICK))
    }

    suspend fun doubleClick(button: PointerButton = PointerButton.LEFT) = withModifiers {
        send(
            NexusProtocol.Actions.INPUT_BUTTON,
            ButtonParams.serializer(),
            ButtonParams(button, ButtonAction.DOUBLE_CLICK),
        )
    }

    suspend fun pressButton(button: PointerButton = PointerButton.LEFT) {
        pointerDown = true
        send(NexusProtocol.Actions.INPUT_BUTTON, ButtonParams.serializer(), ButtonParams(button, ButtonAction.DOWN))
    }

    suspend fun releaseButton(button: PointerButton = PointerButton.LEFT) {
        pointerDown = false
        send(NexusProtocol.Actions.INPUT_BUTTON, ButtonParams.serializer(), ButtonParams(button, ButtonAction.UP))
    }

    // --- Touch ---------------------------------------------------------------------------------

    /**
     * Puts a touch contact down, moves it, or lifts it.
     *
     * Sent without awaiting a reply for the same reason moves are: during a drag these arrive as
     * fast as fingers move, and a round trip per contact update would put the network between the
     * finger and what it is dragging.
     *
     * `touchDown` is tracked locally as well, so that an interrupted gesture — a call arriving, the
     * app being backgrounded — can lift a contact the PC is still holding. A finger left pressed on
     * the desktop is the touch equivalent of a stuck mouse button, and worse, because there is no
     * cursor to show it.
     */
    fun touch(action: TouchAction, point: DesktopPoint, displayIndex: Int) {
        contactDown = when (action) {
            TouchAction.DOWN, TouchAction.MOVE -> true
            TouchAction.UP, TouchAction.CANCEL -> false
        }

        connection.notify(
            NexusProtocol.Actions.INPUT_TOUCH,
            NexusJson.encodeToJsonElement(
                TouchParams.serializer(),
                TouchParams(
                    action = action,
                    x = point.x,
                    y = point.y,
                    displayIndex = displayIndex,
                ),
            ),
        )
    }

    /** True while the phone believes a contact is still down on the PC. */
    val isContactDown: Boolean get() = contactDown

    /**
     * Which input path the PC is using.
     *
     * Best-effort: a PC too old to know this action is not an error, it just has no native input,
     * and the phone shows nothing rather than a failure.
     */
    suspend fun inputStatus(): NativeInputStatus? = try {
        connection.request(NexusProtocol.Actions.INPUT_STATUS)
            ?.let { NexusJson.decodeFromJsonElement(NativeInputStatus.serializer(), it) }
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        Log.d(TAG, "Input status is unavailable: ${e.javaClass.simpleName}")
        null
    }

    /** Wheel notches. Positive scrolls up, matching Windows and the PC's validation. */
    fun scroll(vertical: Double, horizontal: Double = 0.0) {
        if (vertical == 0.0 && horizontal == 0.0) return

        connection.notify(
            NexusProtocol.Actions.INPUT_SCROLL,
            NexusJson.encodeToJsonElement(ScrollParams.serializer(), ScrollParams(vertical, horizontal)),
        )
    }

    // --- Keyboard -----------------------------------------------------------------------------------

    /**
     * Toggles a latched modifier.
     *
     * Latching rather than requiring a real chord: there is no way to hold Ctrl and tap C at once on
     * a touchscreen. The modifier goes down just before the next key or click and comes up straight
     * after, so Windows sees an ordinary Ctrl+C.
     */
    fun toggleModifier(key: NexusKey): Boolean {
        require(key.isModifier) { "$key is not a modifier" }

        return if (!latched.remove(key)) {
            latched.add(key)
            true
        } else {
            false
        }
    }

    fun clearModifiers() {
        latched.clear()
    }

    suspend fun pressKey(key: NexusKey) {
        if (key.isModifier) {
            toggleModifier(key)
            return
        }

        withModifiers {
            send(NexusProtocol.Actions.INPUT_KEY, KeyParams.serializer(), KeyParams(key, KeyAction.PRESS))
        }
    }

    /**
     * Types text.
     *
     * Never wrapped in latched modifiers: Ctrl plus a paragraph is not a thing anyone means, and
     * applying it would turn a paste into a burst of shortcuts.
     */
    suspend fun type(text: String) {
        if (text.isEmpty()) return

        send(NexusProtocol.Actions.INPUT_TEXT, TextParams.serializer(), TextParams(text))
    }

    // --- Internals -------------------------------------------------------------------------------------

    /**
     * Holds the latched modifiers down around one action, then releases them and clears the latch.
     *
     * The release is in a `finally`: if the action fails, the modifiers must still come up. A
     * modifier left down after a failed Ctrl+C would make every subsequent keystroke a shortcut.
     */
    private suspend fun <T> withModifiers(action: suspend () -> T): T {
        val modifiers = latched.toList()

        // Recorded as each one lands, so the release matches what actually went down. Pressing the
        // modifiers inside the try is the point: if the second of two fails, the first is still
        // held on the PC and something has to let go of it.
        val pressed = mutableListOf<NexusKey>()

        try {
            for (modifier in modifiers) {
                send(NexusProtocol.Actions.INPUT_KEY, KeyParams.serializer(), KeyParams(modifier, KeyAction.DOWN))
                pressed += modifier
            }

            return action()
        } finally {
            for (modifier in pressed.asReversed()) {
                quietly {
                    send(NexusProtocol.Actions.INPUT_KEY, KeyParams.serializer(), KeyParams(modifier, KeyAction.UP))
                }
            }

            // Cleared whatever happened. A latch that survives a failure would silently modify the
            // next thing the owner did.
            latched.clear()
        }
    }

    private suspend fun <T> send(
        action: String,
        serializer: kotlinx.serialization.KSerializer<T>,
        params: T,
    ): InputAck {
        val result = connection.request(action, NexusJson.encodeToJsonElement(serializer, params))
        val ack = result?.let { NexusJson.decodeFromJsonElement(InputAck.serializer(), it) } ?: InputAck()

        lastRefusal = if (ack.delivered) null else ack.diagnostic ?: "Windows refused this input."

        return ack
    }

    private suspend fun quietly(action: suspend () -> Unit) {
        try {
            action()
        } catch (e: CancellationException) {
            throw e
        } catch (e: NexusRequestException) {
            Log.d(TAG, "Cleanup request refused: ${e.error.code}")
        } catch (e: Exception) {
            Log.d(TAG, "Cleanup request failed: ${e.javaClass.simpleName}")
        }
    }

    private companion object {
        const val TAG = "NexusRemote"
    }
}
