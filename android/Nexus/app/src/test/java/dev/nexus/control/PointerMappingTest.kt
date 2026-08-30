package dev.nexus.control

import dev.nexus.control.remote.RenderedDesktop
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Where a touch on the phone lands on the PC.
 *
 * This is the arithmetic that decides whether a tap does what the owner meant. It is wrong in ways
 * that are hard to see — a click a few pixels off, or a click at all when the finger was on a black
 * bar — so it is tested at the corners, in both orientations, and on the letterbox itself.
 */
class PointerMappingTest {

    /** A 16:9 desktop in a portrait viewport: bars top and bottom. */
    private val portrait = RenderedDesktop(
        viewportWidth = 1080f,
        viewportHeight = 2400f,
        imageWidth = 1280,
        imageHeight = 720,
    )

    /** The same desktop in landscape: bars at the sides, because 2400:1080 is wider than 16:9. */
    private val landscape = RenderedDesktop(
        viewportWidth = 2400f,
        viewportHeight = 1080f,
        imageWidth = 1280,
        imageHeight = 720,
    )

    @Test
    fun `the image is centred and letterboxed`() {
        // 1080 / 1280 is the binding ratio, so the image is 1080 wide and 607.5 tall.
        assertEquals(1080f / 1280f, portrait.scale, 0.0001f)
        assertEquals(1080f, portrait.drawnWidth, 0.01f)
        assertEquals(607.5f, portrait.drawnHeight, 0.01f)

        assertEquals(0f, portrait.offsetX, 0.01f)
        assertEquals((2400f - 607.5f) / 2f, portrait.offsetY, 0.01f)
    }

    @Test
    fun `the top left corner of the image maps to the origin of the desktop`() {
        val point = portrait.toDesktop(portrait.offsetX, portrait.offsetY)

        assertNotNull(point)
        assertEquals(0.0, point!!.x, 0.001)
        assertEquals(0.0, point.y, 0.001)
    }

    @Test
    fun `the bottom right corner of the image maps to the far corner of the desktop`() {
        // The corner cases are the ones a naive mapping gets wrong, and they are exactly where a
        // close button or a Start menu lives.
        val point = portrait.toDesktop(
            portrait.offsetX + portrait.drawnWidth,
            portrait.offsetY + portrait.drawnHeight,
        )

        assertNotNull(point)
        assertEquals(1.0, point!!.x, 0.001)
        assertEquals(1.0, point.y, 0.001)
    }

    @Test
    fun `the centre maps to the centre`() {
        val point = portrait.toDesktop(portrait.viewportWidth / 2f, portrait.viewportHeight / 2f)!!

        assertEquals(0.5, point.x, 0.001)
        assertEquals(0.5, point.y, 0.001)
    }

    @Test
    fun `a touch on the letterbox produces nothing at all`() {
        // The important refusal. Clamping instead would turn a tap on a black bar into a click on
        // the taskbar, which is both wrong and exactly the sort of thing that gets noticed late.
        assertNull(portrait.toDesktop(540f, 10f))
        assertNull(portrait.toDesktop(540f, 2390f))
        assertNull(portrait.toDesktop(540f, portrait.offsetY - 1f))
        assertNull(portrait.toDesktop(540f, portrait.offsetY + portrait.drawnHeight + 1f))
    }

    @Test
    fun `a touch beside the image in landscape produces nothing`() {
        assertNull(landscape.toDesktop(10f, 540f))
        assertNull(landscape.toDesktop(2390f, 540f))
    }

    @Test
    fun `the same desktop position is reached in either orientation`() {
        // Rotating the phone changes the viewport and nothing else. A point three quarters across
        // the desktop has to stay three quarters across it.
        val target = 0.75

        val portraitTouch = portrait.offsetX + (portrait.drawnWidth * target).toFloat()
        val landscapeTouch = landscape.offsetX + (landscape.drawnWidth * target).toFloat()

        val fromPortrait = portrait.toDesktop(portraitTouch, portrait.viewportHeight / 2f)!!
        val fromLandscape = landscape.toDesktop(landscapeTouch, landscape.viewportHeight / 2f)!!

        assertEquals(target, fromPortrait.x, 0.002)
        assertEquals(target, fromLandscape.x, 0.002)
    }

    @Test
    fun `a viewport with no size maps nothing`() {
        // What the first frame sees, before layout has measured anything.
        val unmeasured = RenderedDesktop(0f, 0f, 1280, 720)

        assertNull(unmeasured.toDesktop(10f, 10f))
        assertTrue(!unmeasured.isUsable)
    }

    @Test
    fun `an absent frame maps nothing`() {
        val noImage = RenderedDesktop(1080f, 2400f, 0, 0)

        assertNull(noImage.toDesktop(500f, 1200f))
    }

    @Test
    fun `touchpad deltas are scaled back into desktop pixels`() {
        // A finger movement should cover the same amount of desktop however far the stream has been
        // scaled down to fit, or the pointer feels slower on a bigger monitor.
        val (dx, dy) = portrait.toDesktopDelta(84.375f, 0f)

        // The image is drawn at 1080/1280 of its size, so 84.375 drawn pixels is 100 desktop ones.
        assertEquals(100.0, dx, 0.01)
        assertEquals(0.0, dy, 0.01)
    }

    @Test
    fun `touchpad sensitivity multiplies the delta`() {
        val (dx, _) = portrait.toDesktopDelta(84.375f, 0f, sensitivity = 2f)

        assertEquals(200.0, dx, 0.01)
    }

    @Test
    fun `a delta with no frame is zero rather than infinite`() {
        // Dividing by a zero scale would produce infinity, which would then be sent to the PC.
        val (dx, dy) = RenderedDesktop(1080f, 2400f, 0, 0).toDesktopDelta(10f, 10f)

        assertEquals(0.0, dx, 0.0)
        assertEquals(0.0, dy, 0.0)
    }
}
