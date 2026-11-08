package dev.nexus.control.remote

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalViewConfiguration
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.viewinterop.AndroidView
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.Surface
import kotlin.math.roundToInt
import kotlinx.coroutines.flow.StateFlow

/**
 * What the owner touches: the PC's desktop, and the gestures over it.
 *
 * The gesture set is deliberately small and mostly what a phone already teaches — tap, long press,
 * drag, two-finger scroll, pinch. There are no three-finger chords or edge swipes to memorise, because a
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
    onSurface: (Surface?) -> Unit,
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
    cursor: StateFlow<DesktopPoint?>,
    modifier: Modifier = Modifier,
) {
    var viewport by remember { mutableStateOf(0f to 0f) }
    var view by remember { mutableStateOf(DesktopView()) }

    val fitted = RenderedDesktop(
        viewportWidth = viewport.first,
        viewportHeight = viewport.second,
        imageWidth = frame?.width ?: 0,
        imageHeight = frame?.height ?: 0,
    )

    val rendered = view.within(fitted).applyTo(fitted)

    // Read inside the gesture loop, which never restarts, so it must see the current values rather
    // than the ones captured when it started.
    val current by rememberUpdatedState(rendered)
    val currentFit by rememberUpdatedState(fitted)
    val mode by rememberUpdatedState(pointerMode)
    val enabled by rememberUpdatedState(inputEnabled)
    val touchSlop = LocalViewConfiguration.current.touchSlop

    // On a zoomed picture the touchpad's pointer would leave the screen; it pushes the view along.
    // Collected here rather than read as state, because the pointer moves on nearly every frame and
    // most of those moves change nothing about the view.
    LaunchedEffect(cursor) {
        cursor.collect { point ->
            if (point != null && mode == PointerModeUi.TOUCHPAD) {
                view = view.revealing(point, currentFit)
            }
        }
    }

    fun act(outcome: GestureOutcome) {
        // With control off nothing else can reach the rest of a zoomed picture, and in Direct mode
        // one finger is already the pointer. A touchpad keeps its scroll: its pointer moves the view.
        val twoFingersMoveThePicture = view.isZoomed && (mode == PointerModeUi.DIRECT || !enabled)

        when (outcome) {
            is GestureOutcome.Pinch -> {
                view = view.pinched(outcome.factor, outcome.focusX, outcome.focusY, currentFit)
                    .panned(outcome.dx, outcome.dy, currentFit)
            }

            is GestureOutcome.Pan -> if (twoFingersMoveThePicture) {
                view = view.panned(outcome.dx, outcome.dy, currentFit)
            }

            is GestureOutcome.Scroll -> if (!twoFingersMoveThePicture) onScroll(outcome.notches)

            is GestureOutcome.Move -> if (!enabled && view.isZoomed) {
                view = view.panned(outcome.dx, outcome.dy, currentFit)
            } else {
                // Divided by the zoom so that a finger's movement crosses the same part of the
                // screen however far in the picture is.
                onMove(current.toDesktop(outcome.x, outcome.y), outcome.dx / view.zoom, outcome.dy / view.zoom)
            }

            is GestureOutcome.DragStart -> onDragStart(current.toDesktop(outcome.x, outcome.y))

            GestureOutcome.DragEnd -> onDragEnd()

            is GestureOutcome.RightClick -> onLongPress(current.toDesktop(outcome.x, outcome.y))

            // Handled by the gesture loop, because deciding between one click and two means waiting.
            is GestureOutcome.Tap, is GestureOutcome.DoubleTap -> Unit
        }
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .clipToBounds()
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
                                .onEach(::act)
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
                            changes.getOrNull(1)?.position?.x ?: primary.position.x,
                            changes.getOrNull(1)?.position?.y ?: primary.position.y,
                        )

                        outcomes.forEach(::act)

                        // Consumed once the gesture is doing something, so a parent scroll container
                        // cannot take the rest of it away mid-drag.
                        if (changes.size >= 2) {
                            changes.forEach { it.consume() }
                        } else if (outcomes.isNotEmpty()) {
                            primary.consume()
                        }
                    }

                    val tap = tapped ?: return@awaitEachGesture

                    // A plain tap. Outside the image it means "show or hide the controls" in both
                    // modes, and with control on or off: it is the only way to get the panel out of
                    // the way in landscape without giving up control. Only a tap: a finger moving
                    // across the black bars in Touchpad mode still moves the pointer.
                    val point = current.toDesktop(tap.x, tap.y)

                    if (point == null || !enabled) {
                        onBackgroundTap()
                        return@awaitEachGesture
                    }

                    // Wait briefly for a second tap before committing to a single click.
                    val second = withTimeoutOrNull(DOUBLE_TAP_MS) {
                        awaitFirstDown(requireUnconsumed = false)
                    }

                    if (second == null) {
                        onTap(point)
                        return@awaitEachGesture
                    }

                    if (mode != PointerModeUi.TOUCHPAD) {
                        onDoubleTap(point)
                        return@awaitEachGesture
                    }

                    // On a touchpad the second touch is not over yet: what it does next decides
                    // whether this was a double click or the start of a drag.
                    val following = PointerGestureRecognizer(touchSlop, afterTap = true)

                    following.down(second.position.x, second.position.y, System.currentTimeMillis())

                    while (true) {
                        val event = awaitPointerEvent()
                        val changes = event.changes.filter { it.pressed }
                        val now = System.currentTimeMillis()

                        val outcomes = if (changes.isEmpty()) {
                            following.up(now)
                        } else {
                            val primary = changes.first()
                            following.move(
                                primary.position.x,
                                primary.position.y,
                                now,
                                changes.size,
                                changes.getOrNull(1)?.position?.x ?: primary.position.x,
                                changes.getOrNull(1)?.position?.y ?: primary.position.y,
                            )
                        }

                        outcomes.forEach {
                            when (it) {
                                is GestureOutcome.Tap -> onTap(point)
                                is GestureOutcome.DoubleTap -> onDoubleTap(point)
                                else -> act(it)
                            }
                        }

                        if (changes.isEmpty()) break

                        if (outcomes.isNotEmpty()) changes.forEach { it.consume() }
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        // The video surface, sized to exactly the rectangle the desktop occupies. The decoder's
        // output is scaled to the view by the compositor, so shaping the view to the stream's aspect
        // ratio is the whole of "fit"; the pointer mapping above uses the same rectangle, which is
        // what keeps a touch and the pixel under it in agreement.
        //
        // A SurfaceView rather than a TextureView: it is composited directly rather than drawn
        // through the app's own GPU pass, which is one frame less between the decoder and the glass.
        val density = LocalDensity.current
        val currentOnSurface by rememberUpdatedState(onSurface)

        val videoModifier = if (rendered.isUsable) {
            with(density) {
                // requiredSize, because a zoomed picture is larger than the space it is laid out in.
                Modifier
                    .offset { IntOffset(rendered.panX.roundToInt(), rendered.panY.roundToInt()) }
                    .requiredSize(rendered.drawnWidth.toDp(), rendered.drawnHeight.toDp())
            }
        } else {
            Modifier.fillMaxSize()
        }

        AndroidView(
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = currentOnSurface(holder.surface)

                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

                        // Synchronous on purpose: the decoder must let go of the surface before this
                        // returns, or it renders into memory the system is already reclaiming.
                        override fun surfaceDestroyed(holder: SurfaceHolder) = currentOnSurface(null)
                    })
                }
            },
            modifier = videoModifier,
        )

        if (frame?.image != null) {
            Image(
                bitmap = frame.image,
                contentDescription = "The PC screen",
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer {
                        scaleX = rendered.zoom
                        scaleY = rendered.zoom
                        translationX = rendered.panX
                        translationY = rendered.panY
                    },
                // Fit, never Crop: a desktop is wide and a phone is tall, and cropping would hide
                // the parts of the screen the owner is most likely checking on.
                contentScale = ContentScale.Fit,
            )
        }
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
