package dev.nexus.control.remote

/**
 * Where the desktop image actually sits inside the view, and how a touch on it maps back.
 *
 * The image is fitted to the viewport preserving aspect ratio, so unless the two happen to have the
 * same shape there are bars down the sides or along the top and bottom. Every touch coordinate has
 * to be corrected for those bars before it means anything on the PC — and a touch that lands *on*
 * them means nothing at all and must not be turned into a click somewhere plausible.
 *
 * This is deliberately pure arithmetic with no Compose or Android types in it. Getting it wrong
 * puts the pointer somewhere the owner did not touch, which is close to invisible in a screenshot
 * and obvious in use, so it is the part of the remote screen that most deserves its own tests.
 */
data class RenderedDesktop(
    val viewportWidth: Float,
    val viewportHeight: Float,
    val imageWidth: Int,
    val imageHeight: Int,
) {
    /** How much the desktop image is scaled to fit. Always <= 1 in practice, but not assumed. */
    val scale: Float
        get() {
            if (imageWidth <= 0 || imageHeight <= 0) return 0f
            return minOf(viewportWidth / imageWidth, viewportHeight / imageHeight)
        }

    val drawnWidth: Float get() = imageWidth * scale

    val drawnHeight: Float get() = imageHeight * scale

    /** Left edge of the image within the viewport; the letterbox bar is everything before it. */
    val offsetX: Float get() = (viewportWidth - drawnWidth) / 2f

    val offsetY: Float get() = (viewportHeight - drawnHeight) / 2f

    val isUsable: Boolean get() = imageWidth > 0 && imageHeight > 0 && viewportWidth > 0 && viewportHeight > 0

    /**
     * Converts a touch in viewport coordinates to a position on the desktop, as a fraction of it.
     *
     * Returns null when the touch is outside the rendered image. That is the important case: the
     * alternative — clamping to the nearest edge — would quietly turn a tap on a black bar into a
     * click on the taskbar.
     */
    fun toDesktop(touchX: Float, touchY: Float): DesktopPoint? {
        if (!isUsable) return null

        val u = (touchX - offsetX) / drawnWidth
        val v = (touchY - offsetY) / drawnHeight

        if (u < 0f || u > 1f || v < 0f || v > 1f) return null

        // Clamped only against floating-point drift at the very edges, after the range check has
        // already rejected anything genuinely outside.
        return DesktopPoint(u.coerceIn(0f, 1f).toDouble(), v.coerceIn(0f, 1f).toDouble())
    }

    /**
     * Converts a movement in viewport pixels to one in desktop pixels.
     *
     * Used by touchpad mode so that a given finger movement covers the same amount of desktop
     * regardless of how much the stream is scaled down, which is what stops the pointer feeling
     * slower on a bigger monitor.
     */
    fun toDesktopDelta(dx: Float, dy: Float, sensitivity: Float = 1f): Pair<Double, Double> {
        if (!isUsable || scale <= 0f) return 0.0 to 0.0

        return (dx / scale * sensitivity).toDouble() to (dy / scale * sensitivity).toDouble()
    }
}

/** A position on the streamed display, 0 to 1 on each axis. Exactly what `input.pointer` expects. */
data class DesktopPoint(val x: Double, val y: Double)
