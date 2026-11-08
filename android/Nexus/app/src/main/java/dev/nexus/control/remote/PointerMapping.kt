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
    val zoom: Float = 1f,
    val panX: Float = 0f,
    val panY: Float = 0f,
) {
    /** Image pixels to viewport pixels: the fit to the viewport, times the owner's zoom. */
    val scale: Float
        get() {
            if (imageWidth <= 0 || imageHeight <= 0) return 0f
            return minOf(viewportWidth / imageWidth, viewportHeight / imageHeight) * zoom
        }

    val drawnWidth: Float get() = imageWidth * scale

    val drawnHeight: Float get() = imageHeight * scale

    /** Left edge of the image within the viewport; the letterbox bar is everything before it. */
    val offsetX: Float get() = (viewportWidth - drawnWidth) / 2f + panX

    val offsetY: Float get() = (viewportHeight - drawnHeight) / 2f + panY

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

/**
 * Whole pixels of pointer movement, with the fractions kept for next time.
 *
 * The PC rounds every relative move to whole pixels, so a slow finger, or a zoomed picture that
 * divides the movement down, would otherwise send a stream of moves that all round to nothing.
 */
class PointerRemainder {
    private var x = 0.0
    private var y = 0.0

    fun take(dx: Double, dy: Double): Pair<Double, Double> {
        x += dx
        y += dy

        val wholeX = x.toLong().toDouble()
        val wholeY = y.toLong().toDouble()

        x -= wholeX
        y -= wholeY

        return wholeX to wholeY
    }
}

/**
 * How far the owner has zoomed into the picture, and where.
 *
 * Entirely on the phone: the stream keeps sending the same picture and only how it is drawn changes.
 * Every operation takes the picture as fitted at zoom 1 and returns a view that is already inside
 * its limits, so the picture can never be dragged away from the edge of the screen.
 */
data class DesktopView(val zoom: Float = 1f, val panX: Float = 0f, val panY: Float = 0f) {

    val isZoomed: Boolean get() = zoom > 1f

    /** The picture as this view draws it. */
    fun applyTo(fitted: RenderedDesktop): RenderedDesktop = fitted.copy(zoom = zoom, panX = panX, panY = panY)

    /** Zooms by [factor], keeping what is under the fingers under the fingers. */
    fun pinched(factor: Float, focusX: Float, focusY: Float, fitted: RenderedDesktop): DesktopView {
        val next = (zoom * factor).coerceIn(1f, MAX_ZOOM)
        val ratio = next / zoom
        val fromCentreX = focusX - fitted.viewportWidth / 2f
        val fromCentreY = focusY - fitted.viewportHeight / 2f

        return DesktopView(
            zoom = next,
            panX = fromCentreX - (fromCentreX - panX) * ratio,
            panY = fromCentreY - (fromCentreY - panY) * ratio,
        ).within(fitted)
    }

    fun panned(dx: Float, dy: Float, fitted: RenderedDesktop): DesktopView =
        copy(panX = panX + dx, panY = panY + dy).within(fitted)

    /**
     * Moves the view as little as possible to keep a desktop position away from the screen's edges.
     * What makes a zoomed picture usable with a touchpad: the pointer pushes the view along.
     */
    fun revealing(point: DesktopPoint, fitted: RenderedDesktop): DesktopView {
        if (!isZoomed || !fitted.isUsable) return within(fitted)

        val drawn = applyTo(fitted)
        val x = drawn.offsetX + point.x.toFloat() * drawn.drawnWidth
        val y = drawn.offsetY + point.y.toFloat() * drawn.drawnHeight

        val marginX = fitted.viewportWidth * REVEAL_MARGIN
        val marginY = fitted.viewportHeight * REVEAL_MARGIN

        return panned(
            dx = pushBack(x, marginX, fitted.viewportWidth - marginX),
            dy = pushBack(y, marginY, fitted.viewportHeight - marginY),
            fitted = fitted,
        )
    }

    /** The same view with the picture still covering as much of the screen as it can. */
    fun within(fitted: RenderedDesktop): DesktopView {
        val limitX = maxOf(0f, (fitted.drawnWidth * zoom - fitted.viewportWidth) / 2f)
        val limitY = maxOf(0f, (fitted.drawnHeight * zoom - fitted.viewportHeight) / 2f)

        return copy(panX = panX.coerceIn(-limitX, limitX), panY = panY.coerceIn(-limitY, limitY))
    }

    private fun pushBack(position: Float, low: Float, high: Float): Float = when {
        position < low -> low - position
        position > high -> high - position
        else -> 0f
    }

    companion object {
        const val MAX_ZOOM = 5f

        /** How close to an edge, as a share of the screen, the pointer gets before the view follows. */
        const val REVEAL_MARGIN = 0.15f
    }
}
