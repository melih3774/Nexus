package dev.nexus.control

import dev.nexus.control.remote.GestureOutcome
import dev.nexus.control.remote.PointerGestureRecognizer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * What a finger on the desktop surface is allowed to do to the PC's mouse.
 *
 * **The bug these tests were written for.** On a real paired phone, moving the pointer behaved as
 * though the left button were held: swipes selected text, dragged icons, and swept selection boxes
 * across the desktop, in both Direct and Touchpad modes. The cause was in the gesture loop, not the
 * PC — any single-finger movement past the touch slop called `onDragStart`, which sent
 * `input.button DOWN`, so there was no way to move the pointer without dragging.
 *
 * Nothing could have caught it: the decision lived inside a Compose `pointerInput` block, and the
 * only pointer test in the project covered coordinate mapping. So the decision moved into
 * [PointerGestureRecognizer], and the invariant it exists to protect is asserted first and
 * repeatedly below: **movement alone never presses a button.**
 */
class PointerGestureTest {

    private val slop = 10f

    private fun recognizer() = PointerGestureRecognizer(touchSlop = slop, longPressMs = 450L)

    /** A finger that goes down, moves in a straight line, and lifts — without ever pausing. */
    private fun swipe(
        from: Float = 100f,
        to: Float = 400f,
        steps: Int = 6,
        stepMs: Long = 16L,
    ): List<GestureOutcome> {
        val gesture = recognizer()
        val outcomes = mutableListOf<GestureOutcome>()

        gesture.down(from, from, 0L)

        for (step in 1..steps) {
            val at = from + (to - from) * step / steps
            outcomes += gesture.move(at, from, stepMs * step, pointerCount = 1)
        }

        outcomes += gesture.up(stepMs * steps + 1)
        return outcomes
    }

    // --- the invariant --------------------------------------------------------------------------

    @Test
    fun `moving a finger never presses a button`() {
        val outcomes = swipe()

        assertTrue("a swipe must move the pointer", outcomes.any { it is GestureOutcome.Move })

        assertFalse(
            "moving the pointer must not hold the left button: $outcomes",
            outcomes.any { it is GestureOutcome.DragStart },
        )

        assertFalse(
            "nothing was pressed, so nothing may be released: $outcomes",
            outcomes.any { it is GestureOutcome.DragEnd },
        )
    }

    @Test
    fun `a slow swipe is still a move and not a drag`() {
        // Slow enough that the whole gesture outlasts the long-press wait several times over. The
        // finger is moving throughout, so it is still only ever a move: the wait arms a drag only
        // while the finger is holding still.
        val outcomes = swipe(steps = 12, stepMs = 200L)

        assertTrue(outcomes.any { it is GestureOutcome.Move })
        assertFalse("a slow swipe is not a drag: $outcomes", outcomes.any { it is GestureOutcome.DragStart })
    }

    @Test
    fun `movement below the touch slop does nothing at all`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)

        val outcomes = gesture.move(100f + slop / 3f, 100f, 16L, pointerCount = 1)

        assertTrue("a finger tremor is not a movement: $outcomes", outcomes.isEmpty())
    }

    // --- tap ------------------------------------------------------------------------------------

    @Test
    fun `a short tap is a tap`() {
        val gesture = recognizer()
        gesture.down(100f, 200f, 0L)

        val outcome = gesture.up(80L).single()

        assertEquals(GestureOutcome.Tap(100f, 200f), outcome)
    }

    @Test
    fun `a swipe is not a tap`() {
        assertFalse(
            "a finger that travelled must not also click where it started",
            swipe().any { it is GestureOutcome.Tap },
        )
    }

    // --- deliberate drag ------------------------------------------------------------------------

    @Test
    fun `holding still and then moving is a drag`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)

        // Resting in place, past the long-press wait. Nothing has happened yet: whether this is a
        // right click or a drag is still undecided.
        assertTrue(gesture.move(100f, 100f, 500L, pointerCount = 1).isEmpty())
        assertFalse(gesture.isDragging)

        val started = gesture.move(160f, 100f, 520L, pointerCount = 1)

        assertEquals(
            "the button goes down where the finger went down, not where it has reached",
            GestureOutcome.DragStart(100f, 100f),
            started.first(),
        )

        assertTrue("the same event must also move: $started", started.any { it is GestureOutcome.Move })
        assertTrue(gesture.isDragging)

        val continued = gesture.move(220f, 100f, 540L, pointerCount = 1)

        assertTrue(continued.all { it is GestureOutcome.Move })
        assertEquals(
            "the drag must not press again once it is holding",
            0,
            continued.count { it is GestureOutcome.DragStart },
        )

        assertEquals(listOf(GestureOutcome.DragEnd), gesture.up(560L))
    }

    /**
     * The physical-device regression.
     *
     * A hold followed by one decisive move, with no jitter events in between — exactly what
     * `adb shell input motionevent` produces, and what a steady finger on a good screen can produce
     * too. The first version of this recognizer counted the movement before testing whether the
     * finger had stayed still, so this event failed its own "has it stayed put?" check and the drag
     * silently became a plain move. It measured as 719 pixels of cursor travel with the left button
     * never once down.
     */
    @Test
    fun `a hold followed by a single large jump still drags`() {
        val gesture = recognizer()
        gesture.down(350f, 500f, 0L)

        // No intermediate events at all: the finger rests, then moves 100px in one report.
        val outcomes = gesture.move(450f, 500f, 750L, pointerCount = 1)

        assertEquals(
            "a hold that ends in one large move is still a drag: $outcomes",
            GestureOutcome.DragStart(350f, 500f),
            outcomes.first(),
        )

        assertTrue(gesture.isDragging)
        assertEquals(listOf(GestureOutcome.DragEnd), gesture.up(800L))
    }

    @Test
    fun `a drag always releases what it pressed`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)
        gesture.move(100f, 100f, 500L, pointerCount = 1)
        gesture.move(200f, 100f, 520L, pointerCount = 1)

        assertTrue(gesture.isDragging)

        // Whatever ends the gesture — a lift, a cancel, the surface going away — the button comes
        // back up. A left button left down on the PC is the worst outcome available here.
        assertEquals(listOf(GestureOutcome.DragEnd), gesture.up(999L))
    }

    // --- right click ----------------------------------------------------------------------------

    @Test
    fun `holding still and then lifting is a right click`() {
        val gesture = recognizer()
        gesture.down(300f, 400f, 0L)
        gesture.move(300f, 400f, 500L, pointerCount = 1)

        assertEquals(listOf(GestureOutcome.RightClick(300f, 400f)), gesture.up(520L))
    }

    @Test
    fun `a hold with no movement events at all is still a right click`() {
        // Some surfaces deliver nothing while a finger rests perfectly still, so the wait has to be
        // decided on the way up as well.
        val gesture = recognizer()
        gesture.down(300f, 400f, 0L)

        assertEquals(listOf(GestureOutcome.RightClick(300f, 400f)), gesture.up(600L))
    }

    @Test
    fun `a hold that became a drag does not also right click`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)
        gesture.move(100f, 100f, 500L, pointerCount = 1)
        gesture.move(200f, 100f, 520L, pointerCount = 1)

        assertFalse(
            "one gesture cannot be both a drag and a right click",
            gesture.up(540L).any { it is GestureOutcome.RightClick },
        )
    }

    // --- scroll ---------------------------------------------------------------------------------

    @Test
    fun `two fingers scroll and never move the pointer`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)

        val outcomes = buildList {
            for (step in 1..8) {
                addAll(gesture.move(100f, 100f + step * 40f, step * 16L, pointerCount = 2))
            }
        }

        assertTrue("two fingers must scroll: $outcomes", outcomes.any { it is GestureOutcome.Scroll })

        assertFalse(
            "a two-finger gesture must not move the pointer: $outcomes",
            outcomes.any { it is GestureOutcome.Move },
        )

        assertFalse(
            "a two-finger gesture must not press anything: $outcomes",
            outcomes.any { it is GestureOutcome.DragStart },
        )
    }

    @Test
    fun `a scroll does not end as a tap or a click`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)
        gesture.move(100f, 200f, 16L, pointerCount = 2)

        assertTrue("lifting two fingers clicks nothing", gesture.up(600L).isEmpty())
    }

    @Test
    fun `slow scrolling still accumulates whole notches`() {
        val gesture = recognizer()
        gesture.down(100f, 100f, 0L)

        // Each step is well under one notch. Rounding per event would discard all of it.
        val notches = buildList {
            for (step in 1..20) {
                addAll(gesture.move(100f, 100f + step * 10f, step * 16L, pointerCount = 2))
            }
        }.filterIsInstance<GestureOutcome.Scroll>().sumOf { it.notches.toInt() }

        assertEquals("200 pixels at 48 per notch", 4, notches)
    }

    // --- tap, then touch again (Touchpad) -------------------------------------------------------

    private fun afterTap() = PointerGestureRecognizer(touchSlop = slop, longPressMs = 450L, afterTap = true)

    @Test
    fun `a touch that follows a tap and moves drags at once with no wait`() {
        val gesture = afterTap()
        gesture.down(100f, 100f, 0L)

        val started = gesture.move(130f, 100f, 40L, pointerCount = 1)

        assertEquals(GestureOutcome.DragStart(100f, 100f), started.first())
        assertEquals(GestureOutcome.Move(130f, 100f, 30f, 0f), started.last())
        assertTrue(gesture.isDragging)

        val continued = gesture.move(170f, 120f, 60L, pointerCount = 1)

        assertEquals(listOf<GestureOutcome>(GestureOutcome.Move(170f, 120f, 40f, 20f)), continued)
        assertEquals(listOf<GestureOutcome>(GestureOutcome.DragEnd), gesture.up(80L))
    }

    @Test
    fun `a touch that follows a tap and lifts in place is a double tap`() {
        val gesture = afterTap()
        gesture.down(100f, 100f, 0L)

        assertTrue(gesture.move(100f + slop / 3f, 100f, 20L, pointerCount = 1).isEmpty())
        assertEquals(listOf<GestureOutcome>(GestureOutcome.DoubleTap(100f, 100f)), gesture.up(60L))
    }

    @Test
    fun `resting on the second touch does not turn it into a right click`() {
        val gesture = afterTap()
        gesture.down(100f, 100f, 0L)

        assertEquals(listOf<GestureOutcome>(GestureOutcome.DoubleTap(100f, 100f)), gesture.up(900L))
    }

    @Test
    fun `resting on the second touch and then moving still drags`() {
        val gesture = afterTap()
        gesture.down(100f, 100f, 0L)

        assertTrue(gesture.move(100f, 100f, 800L, pointerCount = 1).isEmpty())
        assertTrue(gesture.move(160f, 100f, 820L, pointerCount = 1).first() is GestureOutcome.DragStart)
        assertEquals(listOf<GestureOutcome>(GestureOutcome.DragEnd), gesture.up(840L))
    }

    @Test
    fun `a scroll right after a tap still clicks once and presses nothing`() {
        val gesture = afterTap()
        gesture.down(100f, 100f, 0L)

        val outcomes = buildList {
            for (step in 1..4) {
                addAll(gesture.move(100f, 100f + step * 30f, step * 16L, pointerCount = 2))
            }
            addAll(gesture.up(100L))
        }

        assertEquals(1, outcomes.count { it is GestureOutcome.Tap })
        assertTrue(outcomes.any { it is GestureOutcome.Scroll })
        assertFalse(outcomes.any { it is GestureOutcome.DragStart || it is GestureOutcome.DoubleTap })
    }

    @Test
    fun `a second finger during a tap drag does not click and still releases`() {
        val gesture = afterTap()
        gesture.down(100f, 100f, 0L)
        gesture.move(140f, 100f, 20L, pointerCount = 1)

        val outcomes = gesture.move(140f, 160f, 40L, pointerCount = 2)

        assertFalse(outcomes.any { it is GestureOutcome.Tap })
        assertEquals(listOf<GestureOutcome>(GestureOutcome.DragEnd), gesture.up(60L))
    }

    @Test
    fun `an ordinary touch still needs the wait before it can drag`() {
        assertFalse(swipe().any { it is GestureOutcome.DragStart || it is GestureOutcome.DoubleTap })
    }

    // --- the two modes share this model ---------------------------------------------------------

    @Test
    fun `the recognizer reports movement the same way whichever mode is showing`() {
        // Direct and Touchpad differ only in how the caller sends the movement — an absolute
        // position or a delta. Both are carried on every Move, so neither mode needs a gesture rule
        // of its own, and neither can drift into pressing a button the other does not.
        val move = swipe().filterIsInstance<GestureOutcome.Move>().first()

        assertTrue("Direct needs a position", move.x > 0f)
        assertTrue("Touchpad needs a delta", move.dx != 0f)
    }
}
