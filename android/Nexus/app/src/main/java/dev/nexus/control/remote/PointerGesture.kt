package dev.nexus.control.remote

/**
 * What one finger on the desktop surface means.
 *
 * **The invariant this type exists to hold: moving the pointer and holding the left button are
 * separate actions.** Before v0.7.1 they were not. Any single-finger movement past the touch slop
 * called `onDragStart`, which pressed the left button, so on a real phone the pointer could not be
 * moved without dragging — every swipe selected text, dragged icons, or swept a selection box
 * across the desktop, in both Direct and Touchpad modes. The PC was innocent: `input.pointer` never
 * touches a button, and `input.button` was being sent deliberately by the phone.
 *
 * Pressing a button is now something only a deliberate gesture does:
 *
 * - move a finger — the pointer moves, and nothing is pressed
 * - tap — left click
 * - hold still, then move — drag: the left button goes down, the pointer moves, and it comes up
 *   when the finger lifts
 * - hold still, then lift — right click
 * - two fingers — scroll
 * - in Touchpad mode, tap and then touch again and move — drag, as on a laptop's touchpad. The tap
 *   before it is what asks for the button, so no wait is needed
 *
 * Holding still is what separates a drag from a move, and it is the same wait that already meant
 * "right click", so nothing new has to be learned: a long press that goes somewhere is a drag, a
 * long press that stays put is a right click. Anything more deliberate than that has a button on
 * the toolbar instead.
 *
 * This is a plain state machine over positions and timestamps with no Compose or Android types in
 * it, because the bug it replaces was a logic bug that a test could have caught and did not.
 */
sealed interface GestureOutcome {

    /** Move the pointer. Never presses anything — true whether or not a drag is in progress. */
    data class Move(val x: Float, val y: Float, val dx: Float, val dy: Float) : GestureOutcome

    /** A deliberate drag begins: hold the left button down, at this position. */
    data class DragStart(val x: Float, val y: Float) : GestureOutcome

    /** Release the left button the drag was holding. */
    data object DragEnd : GestureOutcome

    /** Whole wheel notches. Positive is up, matching Windows. */
    data class Scroll(val notches: Float) : GestureOutcome

    /** Held in place and released: a right click, at the position the finger went down. */
    data class RightClick(val x: Float, val y: Float) : GestureOutcome

    /**
     * A plain tap.
     *
     * Left as a tap rather than a click because the caller still has to wait to find out whether a
     * second one is coming, and that wait belongs to the caller's coroutine, not to this.
     */
    data class Tap(val x: Float, val y: Float) : GestureOutcome

    /** A second touch right after a tap, lifted where it landed. */
    data class DoubleTap(val x: Float, val y: Float) : GestureOutcome
}

/**
 * Recognises one gesture, from the first finger down to the last finger up.
 *
 * Fed raw positions and timestamps by the surface. One instance per gesture: it carries the state
 * of a single touch and is thrown away when that touch ends.
 *
 * @param afterTap this touch landed right after a tap, in Touchpad mode. Moving it drags at once,
 * and lifting it without moving is a double tap however long it rested.
 */
class PointerGestureRecognizer(
    private val touchSlop: Float,
    private val longPressMs: Long = LONG_PRESS_MS,
    private val scrollPixelsPerNotch: Float = SCROLL_PIXELS_PER_NOTCH,
    private val afterTap: Boolean = false,
) {
    private var downX = 0f
    private var downY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var startedAt = 0L

    private var travelled = 0f
    private var scrollRemainder = 0f

    private var scrolled = false
    private var multiTouch = false

    /** A drag is available: the finger held still long enough, or a tap came just before it. */
    private var dragArmed = afterTap

    /** The left button is down because of [dragArmed] followed by movement. */
    private var dragging = false

    /** True once the finger has travelled past the slop, which rules the gesture out as a tap. */
    private var moved = false

    val isDragging: Boolean get() = dragging

    fun down(x: Float, y: Float, atMs: Long) {
        downX = x
        downY = y
        lastX = x
        lastY = y
        startedAt = atMs
    }

    /**
     * One pointer update.
     *
     * @param pointerCount how many fingers are currently down.
     * @return everything that should happen as a result, in order.
     */
    fun move(x: Float, y: Float, atMs: Long, pointerCount: Int): List<GestureOutcome> {
        if (pointerCount >= 2) {
            // The tap before this touch was waiting to see what followed it. A scroll is not a
            // second click, so the first one is still owed.
            val owed = if (afterTap && !multiTouch && !dragging) {
                listOf(GestureOutcome.Tap(downX, downY))
            } else {
                emptyList()
            }

            multiTouch = true
            scrolled = true

            // Accumulated rather than rounded per event, so a slow drag still produces whole
            // notches instead of being truncated to nothing over and over.
            scrollRemainder += y - lastY
            lastX = x
            lastY = y

            val notches = (scrollRemainder / scrollPixelsPerNotch).toInt()

            if (notches == 0) return owed

            scrollRemainder -= notches * scrollPixelsPerNotch
            return owed + GestureOutcome.Scroll(notches.toFloat())
        }

        val dx = x - lastX
        val dy = y - lastY

        if (scrolled) {
            lastX = x
            lastY = y
            travelled += kotlin.math.abs(dx) + kotlin.math.abs(dy)
            return emptyList()
        }

        /*
         * Arming is decided from the state *before* this event's movement is counted.
         *
         * The order matters and a physical phone proved it. Counting the movement first meant that
         * the event which ends a hold — the one that finally moves the finger — was itself enough
         * to push `travelled` past the slop, so the "has it stayed still?" test failed on the very
         * event it was meant to judge and the drag never armed. A finger resting on a real
         * touchscreen usually jitters enough to arm on a tiny delta first, which is why this only
         * showed up when the gesture was driven cleanly: hold, then one decisive move.
         *
         * A finger that has already rested past the threshold has asked for a drag. What it does
         * next decides whether it gets one, not whether it was asking.
         */
        if (!moved && !dragArmed && travelled < touchSlop && atMs - startedAt >= longPressMs) {
            dragArmed = true
        }

        travelled += kotlin.math.abs(dx) + kotlin.math.abs(dy)
        lastX = x
        lastY = y

        if (travelled <= touchSlop) return emptyList()

        moved = true

        // The one place a button is ever pressed by movement, and only because the finger already
        // waited in place to ask for it.
        if (dragArmed && !dragging) {
            dragging = true
            return listOf(
                GestureOutcome.DragStart(downX, downY),
                GestureOutcome.Move(x, y, dx, dy),
            )
        }

        return listOf(GestureOutcome.Move(x, y, dx, dy))
    }

    /**
     * The gesture ended.
     *
     * Called with no pointers left down. A gesture that pressed something always releases it here,
     * whatever else it did.
     */
    fun up(atMs: Long): List<GestureOutcome> {
        if (dragging) return listOf(GestureOutcome.DragEnd)

        if (scrolled || multiTouch) return emptyList()

        // It never moved far enough to drag, so it was the second half of a double tap.
        if (afterTap) return listOf(GestureOutcome.DoubleTap(downX, downY))

        // Held in place and let go without ever moving.
        if (dragArmed) return listOf(GestureOutcome.RightClick(downX, downY))

        // A long press that this recognizer never saw a move event for still counts: some surfaces
        // deliver no updates at all while a finger rests perfectly still.
        if (!moved && atMs - startedAt >= longPressMs) {
            return listOf(GestureOutcome.RightClick(downX, downY))
        }

        if (moved) return emptyList()

        return listOf(GestureOutcome.Tap(downX, downY))
    }

    companion object {
        /**
         * How long a finger must rest before a drag becomes available.
         *
         * The same wait that already meant "right click", deliberately: one hold, and what happens
         * next decides which of the two it was.
         */
        const val LONG_PRESS_MS = 450L

        /** How far a finger travels for one wheel notch — a comfortable swipe is a few lines. */
        const val SCROLL_PIXELS_PER_NOTCH = 48f
    }
}
