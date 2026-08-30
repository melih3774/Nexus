package dev.nexus.control.remote

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalViewConfiguration
import kotlin.math.abs

/**
 * What the owner touches: the PC's desktop, and the gestures over it.
 *
 * The gesture set is deliberately small and mostly what a phone already teaches — tap, long press,
 * drag, two-finger scroll. There are no three-finger chords or edge swipes to memorise, because a
 * remote desktop is already an unfamiliar thing to operate and a gesture nobody can remember is a
 * feature nobody uses. Anything ambiguous has an explicit button on the toolbar instead.
 */
@Composable
fun DesktopSurface(
    frame: DecodedFrame?,
    pointerMode: PointerModeUi,
    inputEnabled: Boolean,
    onTap: (DesktopPoint?) -> Unit,
    onDoubleTap: (DesktopPoint?) -> Unit,
    onLongPress: (DesktopPoint?) -> Unit,
    onDragStart: (DesktopPoint?) -> Unit,
    onDrag: (DesktopPoint?, Float, Float) -> Unit,
    onDragEnd: () -> Unit,
    onScroll: (Float) -> Unit,
    onBackgroundTap: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var viewport by remember { mutableStateOf(0f to 0f) }

    val rendered = RenderedDesktop(
        viewportWidth = viewport.first,
        viewportHeight = viewport.second,
        imageWidth = frame?.width ?: 0,
        imageHeight = frame?.height ?: 0,
    )

    // Read inside the gesture loop, which never restarts, so it must see the current values rather
    // than the ones captured when it started.
    val current by rememberUpdatedState(rendered)
    val mode by rememberUpdatedState(pointerMode)
    val enabled by rememberUpdatedState(inputEnabled)
    val touchSlop = LocalViewConfiguration.current.touchSlop

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black)
            .onSizeChanged { viewport = it.width.toFloat() to it.height.toFloat() }
            .pointerInput(Unit) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val startedAt = System.currentTimeMillis()

                    var pointers = 1
                    var dragging = false
                    var scrolled = false
                    var longPressFired = false
                    var totalMovement = 0f
                    var scrollRemainder = 0f
                    var last = down.position

                    while (true) {
                        val event = awaitPointerEvent()
                        val changes = event.changes.filter { it.pressed }
                        pointers = maxOf(pointers, changes.size)

                        if (changes.isEmpty()) break

                        val primary = changes.first()
                        val movement = primary.positionChange()
                        totalMovement += abs(movement.x) + abs(movement.y)

                        if (changes.size >= 2) {
                            // Two fingers: scroll. Accumulated so that slow movement still produces
                            // whole wheel notches rather than being rounded away to nothing.
                            scrolled = true
                            scrollRemainder += movement.y

                            val notches = (scrollRemainder / SCROLL_PIXELS_PER_NOTCH).toInt()
                            if (notches != 0) {
                                scrollRemainder -= notches * SCROLL_PIXELS_PER_NOTCH
                                onScroll(notches.toFloat())
                            }

                            changes.forEach { it.consume() }
                            last = primary.position
                            continue
                        }

                        val heldLongEnough = System.currentTimeMillis() - startedAt > LONG_PRESS_MS

                        if (!dragging && !scrolled && !longPressFired &&
                            heldLongEnough && totalMovement < touchSlop
                        ) {
                            // Held in place: a right click, and the gesture ends there so it cannot
                            // also become a drag.
                            longPressFired = true
                            onLongPress(current.toDesktop(down.position.x, down.position.y))
                        }

                        if (!longPressFired && !scrolled && totalMovement > touchSlop) {
                            if (!dragging) {
                                dragging = true
                                onDragStart(current.toDesktop(down.position.x, down.position.y))
                            }

                            onDrag(
                                current.toDesktop(primary.position.x, primary.position.y),
                                primary.position.x - last.x,
                                primary.position.y - last.y,
                            )
                        }

                        last = primary.position

                        if (dragging || scrolled) {
                            primary.consume()
                        }
                    }

                    if (dragging) {
                        onDragEnd()
                        return@awaitEachGesture
                    }

                    if (scrolled || longPressFired || pointers > 1) return@awaitEachGesture

                    // A plain tap. Outside the image it means "show or hide the controls" rather
                    // than a click somewhere the owner did not touch.
                    val point = current.toDesktop(down.position.x, down.position.y)

                    if (point == null && mode == PointerModeUi.DIRECT) {
                        onBackgroundTap()
                        return@awaitEachGesture
                    }

                    if (!enabled) {
                        onBackgroundTap()
                        return@awaitEachGesture
                    }

                    // Wait briefly for a second tap before committing to a single click.
                    val second = withTimeoutOrNull(DOUBLE_TAP_MS) {
                        awaitFirstDown(requireUnconsumed = false)
                    }

                    if (second != null) onDoubleTap(point) else onTap(point)
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        if (frame != null) {
            Image(
                bitmap = frame.image,
                contentDescription = "The PC screen",
                modifier = Modifier.fillMaxSize(),
                // Fit, never Crop: a desktop is wide and a phone is tall, and cropping would hide
                // the parts of the screen the owner is most likely checking on.
                contentScale = ContentScale.Fit,
            )
        }
    }
}

/**
 * How far a finger travels for one wheel notch. Chosen so a comfortable swipe scrolls a few lines
 * rather than a whole page.
 */
private const val SCROLL_PIXELS_PER_NOTCH = 48f

private const val LONG_PRESS_MS = 450L

/**
 * How long a single tap waits to find out whether it is really a double tap.
 *
 * A double click can only be told from two single clicks by waiting, and the wait is what makes
 * ordinary tapping feel slightly delayed. Short enough not to be noticed, long enough to catch a
 * deliberate double tap.
 */
private const val DOUBLE_TAP_MS = 220L
