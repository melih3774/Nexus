package dev.nexus.control

import dev.nexus.control.remote.DesktopPoint
import dev.nexus.control.remote.DesktopView
import dev.nexus.control.remote.PointerRemainder
import dev.nexus.control.remote.RenderedDesktop
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The zoomed picture: that it stays on the screen, and that a touch on it still lands on the pixel
 * under the finger, which is the part that goes wrong without anyone seeing why.
 */
class DesktopViewTest {

    /** Exactly 16:9 in a 16:9 viewport, so no bars and easy numbers. */
    private val full = RenderedDesktop(1600f, 900f, 1280, 720)

    /** A wider phone in landscape: bars left and right. */
    private val barred = RenderedDesktop(2400f, 1080f, 1280, 720)

    private val eps = 0.001

    @Test
    fun `an unzoomed view changes nothing about the fit`() {
        val drawn = DesktopView().applyTo(barred)

        assertEquals(barred.scale, drawn.scale, 0.0001f)
        assertEquals(barred.offsetX, drawn.offsetX, 0.0001f)
    }

    @Test
    fun `what is under the fingers stays under the fingers`() {
        val before = DesktopView().applyTo(full).toDesktop(400f, 300f)!!

        val view = DesktopView().pinched(2f, 400f, 300f, full)
        val after = view.applyTo(full).toDesktop(400f, 300f)!!

        assertEquals(2f, view.zoom, 0.0001f)
        assertEquals(before.x, after.x, eps)
        assertEquals(before.y, after.y, eps)
    }

    @Test
    fun `a touch on a zoomed picture maps to the pixel under it`() {
        // Zoomed 2x about the centre: the screen shows the middle half of the desktop.
        val drawn = DesktopView().pinched(2f, 800f, 450f, full).applyTo(full)

        assertEquals(0.25, drawn.toDesktop(0f, 0f)!!.x, eps)
        assertEquals(0.5, drawn.toDesktop(800f, 450f)!!.x, eps)
        assertEquals(0.75, drawn.toDesktop(1600f, 900f)!!.y, eps)
    }

    @Test
    fun `zoom stops at the fit and at the maximum`() {
        assertEquals(1f, DesktopView().pinched(0.2f, 100f, 100f, full).zoom, 0.0001f)
        assertEquals(DesktopView.MAX_ZOOM, DesktopView().pinched(50f, 100f, 100f, full).zoom, 0.0001f)
    }

    @Test
    fun `zooming back out to the fit recentres the picture`() {
        val zoomedOffCentre = DesktopView().pinched(3f, 100f, 100f, full)

        val back = zoomedOffCentre.pinched(0.1f, 1500f, 800f, full)

        assertFalse(back.isZoomed)
        assertEquals(0f, back.panX, 0.0001f)
        assertEquals(0f, back.panY, 0.0001f)
    }

    @Test
    fun `the picture cannot be dragged away from the edge of the screen`() {
        val view = DesktopView(zoom = 2f).panned(99_999f, -99_999f, full)
        val drawn = view.applyTo(full)

        assertEquals(0f, drawn.offsetX, 0.01f)
        assertEquals(900f, drawn.offsetY + drawn.drawnHeight, 0.01f)
    }

    @Test
    fun `an axis that still fits on the screen stays centred`() {
        // 2400x1080 viewport; the picture at 1.1x is 2112 wide, still inside the 2400 available,
        // but 1188 tall, more than the 1080 available.
        val view = DesktopView(zoom = 1.1f).panned(500f, 500f, barred)

        assertEquals(0f, view.panX, 0.0001f)
        assertEquals(54f, view.panY, 0.01f)
    }

    @Test
    fun `the black bars are still outside the picture while they are visible`() {
        val drawn = DesktopView(zoom = 1.1f).within(barred).applyTo(barred)

        assertNull(drawn.toDesktop(10f, 500f))
        assertNotNull(drawn.toDesktop(1200f, 500f))
    }

    @Test
    fun `a pointer in the middle of the view does not move it`() {
        val view = DesktopView(zoom = 2f)

        assertEquals(view, view.revealing(DesktopPoint(0.5, 0.5), full))
    }

    @Test
    fun `a pointer past the margin pushes the view just far enough`() {
        val view = DesktopView(zoom = 2f).revealing(DesktopPoint(0.74, 0.5), full)
        val drawn = view.applyTo(full)
        val x = drawn.offsetX + 0.74f * drawn.drawnWidth

        assertEquals(1600f * (1f - DesktopView.REVEAL_MARGIN), x, 0.01f)
    }

    @Test
    fun `an unzoomed view never follows the pointer`() {
        val view = DesktopView().revealing(DesktopPoint(0.99, 0.01), full)

        assertEquals(0f, view.panX, 0.0001f)
        assertEquals(0f, view.panY, 0.0001f)
    }

    @Test
    fun `small movements add up instead of rounding to nothing`() {
        val remainder = PointerRemainder()
        var sentX = 0.0

        repeat(12) { sentX += remainder.take(0.25, 0.0).first }

        assertEquals(3.0, sentX, eps)
    }

    @Test
    fun `movement in the other direction keeps its sign`() {
        val remainder = PointerRemainder()

        assertEquals(0.0, remainder.take(-0.6, 0.0).first, eps)
        assertEquals(-1.0, remainder.take(-0.6, 0.0).first, eps)
        assertTrue(remainder.take(0.0, 2.5).second == 2.0)
    }
}
