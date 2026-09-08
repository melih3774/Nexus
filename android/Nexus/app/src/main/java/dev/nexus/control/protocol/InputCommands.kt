package dev.nexus.control.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The keys the phone may name, mirroring `PcCore.Capabilities.Input.NexusKey`.
 *
 * Ordinary characters are deliberately absent: they go through `input.text`, which handles Unicode
 * properly. These are the keys that text cannot express — modifiers, navigation, function keys — plus
 * the letters and digits needed to build combinations like Ctrl+C.
 *
 * The wire name is the enum name here. `ProtocolVectorsTest` checks this list against the one PC
 * Core generates, so a key added on one side and forgotten on the other fails a test rather than
 * silently doing nothing on a phone.
 */
@Serializable
enum class NexusKey {
    ESCAPE, TAB, ENTER, BACKSPACE, DELETE, INSERT, SPACE,
    HOME, END, PAGE_UP, PAGE_DOWN,
    ARROW_LEFT, ARROW_UP, ARROW_RIGHT, ARROW_DOWN,
    CONTROL, ALT, SHIFT, META,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    DIGIT_0, DIGIT_1, DIGIT_2, DIGIT_3, DIGIT_4,
    DIGIT_5, DIGIT_6, DIGIT_7, DIGIT_8, DIGIT_9,
    ;

    /** True for the four keys that latch in the toolbar rather than firing on tap. */
    val isModifier: Boolean get() = this == CONTROL || this == ALT || this == SHIFT || this == META

    /** What the toolbar shows. Short, because the buttons are small. */
    val label: String
        get() = when (this) {
            CONTROL -> "Ctrl"
            ALT -> "Alt"
            SHIFT -> "Shift"
            META -> "Win"
            ESCAPE -> "Esc"
            ARROW_LEFT -> "←"
            ARROW_UP -> "↑"
            ARROW_RIGHT -> "→"
            ARROW_DOWN -> "↓"
            PAGE_UP -> "PgUp"
            PAGE_DOWN -> "PgDn"
            BACKSPACE -> "Bksp"
            DELETE -> "Del"
            ENTER -> "Enter"
            TAB -> "Tab"
            HOME -> "Home"
            END -> "End"
            else -> name
        }
}

@Serializable
enum class PointerButton { LEFT, RIGHT, MIDDLE }

@Serializable
enum class ButtonAction { DOWN, UP, CLICK, DOUBLE_CLICK }

@Serializable
enum class KeyAction { DOWN, UP, PRESS }

/** Which of the two pointer models a move is expressed in. */
@Serializable
enum class PointerMode { ABSOLUTE, RELATIVE }

/**
 * A pointer move.
 *
 * Absolute positions are a fraction of the display rather than pixels, so the phone never has to
 * know about DPI scaling, monitor origins, or the coordinate range Windows expects. Everything that
 * has to be exactly right happens on the PC, where it is unit-tested against multi-monitor layouts.
 */
@Serializable
data class PointerParams(
    val mode: PointerMode,
    val x: Double = 0.0,
    val y: Double = 0.0,
    val dx: Double = 0.0,
    val dy: Double = 0.0,
    @SerialName("display_index") val displayIndex: Int = 0,
)

@Serializable
data class ButtonParams(val button: PointerButton, val action: ButtonAction)

/** Wheel notches. Positive vertical scrolls up, matching Windows. */
@Serializable
data class ScrollParams(val vertical: Double = 0.0, val horizontal: Double = 0.0)

@Serializable
data class KeyParams(val key: NexusKey, val action: KeyAction)

@Serializable
data class TextParams(val text: String)

@Serializable
data class StartStreamParams(@SerialName("display_index") val displayIndex: Int? = null)

/** What PC Core answers with when a stream starts. */
@Serializable
data class StreamStarted(
    @SerialName("stream_id") val streamId: Long = 0,
    val display: DisplayInfo = DisplayInfo(),
    @SerialName("max_fps") val maxFps: Int = 0,
    @SerialName("max_width") val maxWidth: Int = 0,
    @SerialName("max_height") val maxHeight: Int = 0,
    val quality: Int = 0,
    @SerialName("frame_protocol_version") val frameProtocolVersion: Int = 0,
)

@Serializable
data class DisplayInfo(
    val index: Int = 0,
    val name: String = "",
    val left: Int = 0,
    val top: Int = 0,
    val width: Int = 0,
    val height: Int = 0,
    @SerialName("is_primary") val isPrimary: Boolean = false,
)

@Serializable
data class ListDisplaysResult(val count: Int = 0, val displays: List<DisplayInfo> = emptyList())

/**
 * The answer to an input command.
 *
 * `delivered = false` is not an error. Windows refuses input aimed at a window running as
 * administrator, and PC Core deliberately stays a standard-user process rather than working around
 * that — so the phone needs to be able to say why nothing happened.
 */
@Serializable
data class InputAck(
    val delivered: Boolean = true,
    val delivery: String = "DELIVERED",
    val diagnostic: String? = null,
) {
    val blockedByIntegrity: Boolean get() = delivery == "BLOCKED_BY_INTEGRITY"
}

@Serializable
data class InputSessionStarted(
    val active: Boolean = false,
    @SerialName("max_events_per_second") val maxEventsPerSecond: Int = 0,
)

/**
 * What one touch contact is being told to do.
 *
 * Direct mode sends these instead of a pointer move plus a button. Whether the PC turns them into
 * real Windows touch or back into a pointer and a button is the PC's business: it is the only side
 * that knows what the machine can do, and putting the decision there means the phone has one
 * gesture implementation rather than two.
 */
@Serializable
enum class TouchAction { DOWN, MOVE, UP, CANCEL }

/**
 * One touch contact, at a position on a display expressed as a fraction of it.
 *
 * Exactly the coordinate space `input.pointer` uses in absolute mode, deliberately: a tap and a
 * pointer move at the same place on the phone must land on the same pixel of the PC.
 */
@Serializable
data class TouchParams(
    val action: TouchAction,
    val x: Double = 0.0,
    val y: Double = 0.0,
    @SerialName("display_index") val displayIndex: Int = 0,
    @SerialName("contact_id") val contactId: Int = 0,
)

/**
 * Which mechanism is carrying input on the PC, and how healthy it is.
 *
 * A diagnostic, never a setting. The owner does not pick an input path and is never asked to enable
 * one — the PC uses the best available and reports which it got. This exists so that "the pointer
 * will not click that Administrator window" has an answer on the phone instead of being a mystery.
 */
@Serializable
data class NativeInputStatus(
    val path: String = "LOCAL",
    val state: String = "NOT_INSTALLED",
    @SerialName("reaches_elevated_windows") val reachesElevatedWindows: Boolean = false,
    @SerialName("touch_available") val touchAvailable: Boolean = false,
    @SerialName("physical_digitizer") val physicalDigitizer: Boolean = false,
    val desktop: String = "DEFAULT",
    @SerialName("secure_desktop_active") val secureDesktopActive: Boolean = false,
    @SerialName("broker_version") val brokerVersion: String? = null,
    val diagnostic: String? = null,
    val summary: String = "",
) {
    /** True when input reaches everything on the desktop, elevated windows included. */
    val isFullyNative: Boolean get() = state == "READY" && reachesElevatedWindows

    /** What the status line shows. Falls back to composing one if the PC sent none. */
    val label: String
        get() = when {
            summary.isNotBlank() -> summary
            isFullyNative -> "Native input: ready"
            else -> "Standard input"
        }
}
