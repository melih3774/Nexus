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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalViewConfiguration

/**
 * What the owner touches: the PC's desktop, and the gestures over it.
 *
 * The gesture set is deliberately small and mostly what a phone already teaches — tap, long press,
 * drag, two-finger scroll. There are no three-finger chords or edge swipes to memorise, because a
 * remote desktop is already an unfamiliar thing to operate and a gesture nobody can remember is a
 * feature nobody uses. Anything ambiguous has an explicit button on the toolbar instead.
 *
 * Which gesture is which is decided by [PointerGestureRecognizer], which is plain Kotlin and
 * tested. This function only turns Compose pointer events into calls on it and its answers into
 * callbacks — deliberately, because the v0.7.0 bug where every pointer movement dragged with the
 * left button held lived in exactly this loop and had no test that could reach it.
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
    onMove: (DesktopPoint?, Float, Float) -> Unit,
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
                    val gesture = PointerGestureRecognizer(touchSlop)

                    gesture.down(down.position.x, down.position.y, System.currentTimeMillis())

                    var tapped: GestureOutcome.Tap? = null

                    while (true) {
                        val event = awaitPointerEvent()
                        val changes = event.changes.filter { it.pressed }

                        if (changes.isEmpty()) {
                            tapped = gesture.up(System.currentTimeMillis())
                                .onEach { dispatch(it, current, onDragStart, onMove, onDragEnd, onScroll, onLongPress) }
                                .filterIsInstance<GestureOutcome.Tap>()
                                .firstOrNull()

                            break
                        }

                        val primary = changes.first()

                        val outcomes = gesture.move(
                            primary.position.x,
                            primary.position.y,
                            System.currentTimeMillis(),
                            changes.size,
                        )

                        outcomes.forEach {
                            dispatch(it, current, onDragStart, onMove, onDragEnd, onScroll, onLongPress)
                        }

                        // Consumed once the gesture is doing something, so a parent scroll container
                        // cannot take the rest of it away mid-drag.
                        if (changes.size >= 2) {
                            changes.forEach { it.consume() }
                        } else if (outcomes.isNotEmpty()) {
                            primary.consume()
                        }
                    }

                    val tap = tapped ?: return@awaitEachGesture

                    // A plain tap. Outside the image it means "show or hide the controls" rather
                    // than a click somewhere the owner did not touch.
                    val point = current.toDesktop(tap.x, tap.y)

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
 * Turns one recogniser answer into the matching callback.
 *
 * Every branch is a straight hand-off. [GestureOutcome.Tap] is the exception and is handled by the
 * caller, because deciding between one click and two means waiting, and waiting belongs in the
 * gesture coroutine rather than here.
 */
private fun dispatch(
    outcome: GestureOutcome,
    rendered: RenderedDesktop,
    onDragStart: (DesktopPoint?) -> Unit,
    onMove: (DesktopPoint?, Float, Float) -> Unit,
    onDragEnd: () -> Unit,
    onScroll: (Float) -> Unit,
    onLongPress: (DesktopPoint?) -> Unit,
) {
    when (outcome) {
        is GestureOutcome.Move -> onMove(
            rendered.toDesktop(outcome.x, outcome.y),
            outcome.dx,
            outcome.dy,
        )

        is GestureOutcome.DragStart -> onDragStart(rendered.toDesktop(outcome.x, outcome.y))

        GestureOutcome.DragEnd -> onDragEnd()

        is GestureOutcome.Scroll -> onScroll(outcome.notches)

        is GestureOutcome.RightClick -> onLongPress(rendered.toDesktop(outcome.x, outcome.y))

        is GestureOutcome.Tap -> Unit
    }
}

/**
 * How long a single tap waits to find out whether it is really a double tap.
 *
 * A double click can only be told from two single clicks by waiting, and the wait is what makes
 * ordinary tapping feel slightly delayed. Short enough not to be noticed, long enough to catch a
 * deliberate double tap.
 */
private const val DOUBLE_TAP_MS = 220L
