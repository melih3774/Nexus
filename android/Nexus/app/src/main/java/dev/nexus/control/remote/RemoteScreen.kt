package dev.nexus.control.remote

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.nexus.control.protocol.PointerButton

/**
 * The remote-control screen: the PC's desktop, and everything needed to drive it.
 *
 * The desktop fills the screen and the controls sit over the bottom of it, hidden by a tap on the
 * letterbox so the whole phone can be desktop when the owner is reading something. Landscape is
 * where this is genuinely usable — a 16:9 desktop in a portrait viewport is mostly black bars — and
 * the layout adapts rather than the orientation being forced, because forcing it would make getting
 * to the screen from the home list feel broken.
 */
@Composable
fun RemoteScreen(
    modifier: Modifier = Modifier,
    viewModel: RemoteControlViewModel = viewModel(factory = RemoteControlViewModel.Factory),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val frame by viewModel.frames.collectAsStateWithLifecycle()
    val still by viewModel.still.collectAsStateWithLifecycle()
    val stats by viewModel.stats.collectAsStateWithLifecycle()
    val quality by viewModel.quality.collectAsStateWithLifecycle()
    var qualityVisible by remember { mutableStateOf(false) }
    val pointerRemainder = remember { PointerRemainder() }

    val lifecycleOwner = LocalLifecycleOwner.current

    // Starting on entry and stopping on exit is the whole lifetime. There is no case where a stream
    // should outlive this screen.
    DisposableEffect(Unit) {
        viewModel.start()
        viewModel.loadQuality()
        onDispose { viewModel.stop() }
    }

    /*
     * Backgrounding stops the stream too. A phone in a pocket receiving a megabit of desktop it
     * cannot show is battery and bandwidth spent on nothing, and the PC would keep its display
     * duplicated for a viewer that has gone.
     */
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_STOP -> viewModel.stop()
                Lifecycle.Event.ON_START -> viewModel.start()
                else -> Unit
            }
        }

        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    Box(modifier = modifier.fillMaxSize().background(Color.Black)) {
        DesktopSurface(
            frame = frame,
            onSurface = viewModel::attachSurface,
            pointerMode = state.pointerMode,
            inputEnabled = state.inputEnabled,
            onTap = { point ->
                if (state.directUsesTouch && point != null) {
                    // A real tap: the contact goes down and comes straight back up. Windows turns
                    // that into a click for applications that only understand a mouse, and into an
                    // actual tap for the ones that understand touch.
                    viewModel.touchDown(point)
                    viewModel.touchUp(point)
                } else {
                    if (state.pointerMode == PointerModeUi.DIRECT && point != null) viewModel.moveTo(point)
                    viewModel.click()
                }
            },
            onDoubleTap = { point ->
                if (state.directUsesTouch && point != null) {
                    viewModel.touchDown(point)
                    viewModel.touchUp(point)
                    viewModel.touchDown(point)
                    viewModel.touchUp(point)
                } else {
                    if (state.pointerMode == PointerModeUi.DIRECT && point != null) viewModel.moveTo(point)
                    viewModel.doubleClick()
                }
            },
            // Deliberately still a real right click rather than a held touch contact. Windows does
            // turn press-and-hold into a right click on its own, but only after its own timer — and
            // the phone has already waited 450 ms to decide this was a long press. Waiting twice
            // would make the commonest gesture on a desktop feel broken.
            onLongPress = { point ->
                if (state.pointerMode == PointerModeUi.DIRECT && point != null) viewModel.moveTo(point)
                viewModel.click(PointerButton.RIGHT)
            },
            // Only ever reached after a deliberate hold-then-move or, in Touchpad mode, a tap and
            // then a touch that moves. Ordinary movement goes through
            // onMove and never touches a button — the two are separate actions, which is the whole
            // point of the v0.7.1 gesture fix.
            onDragStart = { point ->
                if (state.directUsesTouch && point != null) {
                    viewModel.touchDown(point)
                } else {
                    // Direct: the button must go down where the finger is, not wherever the pointer
                    // was left, so the move comes first.
                    if (state.pointerMode == PointerModeUi.DIRECT && point != null) viewModel.moveTo(point)
                    viewModel.pressPointer()
                }
            },
            onMove = { point, dx, dy ->
                if (state.pointerMode == PointerModeUi.DIRECT) {
                    if (state.directUsesTouch && viewModel.isDragging && point != null) {
                        // A contact is down: this is a drag, and the contact follows the finger.
                        viewModel.touchMove(point)
                    } else {
                        // No contact down. Touch has no equivalent of moving without touching, so
                        // the pointer moves instead — which is also the only way the owner can see
                        // where they are on the streamed image before committing to a tap.
                        point?.let(viewModel::moveTo)
                    }
                } else {
                    // Touchpad: the pointer moves by what the finger moved, scaled so a movement
                    // covers the same amount of desktop however far the stream is scaled down.
                    val rendered = RenderedDesktop(
                        viewportWidth = 1f,
                        viewportHeight = 1f,
                        imageWidth = frame?.width ?: 0,
                        imageHeight = frame?.height ?: 0,
                    )

                    val scale = if (rendered.imageWidth > 0) TOUCHPAD_SENSITIVITY else 0f
                    val (wholeX, wholeY) = pointerRemainder.take((dx * scale).toDouble(), (dy * scale).toDouble())
                    viewModel.moveBy(wholeX, wholeY)
                }
            },
            onDragEnd = {
                if (state.directUsesTouch && viewModel.isDragging) {
                    viewModel.endTouchDrag()
                } else {
                    viewModel.releasePointer()
                }
            },
            onScroll = { notches -> viewModel.scroll(notches.toDouble()) },
            onBackgroundTap = { viewModel.toggleChrome() },
            cursor = viewModel.cursor,
        )

        if (state.starting && frame == null) {
            CircularProgressIndicator(modifier = Modifier.align(Alignment.Center))
        }

        // The screenshot fallback, drawn behind the controls when live capture is unavailable.
        if (frame == null && still != null) {
            Image(
                bitmap = still!!,
                contentDescription = "A screenshot of the PC",
                modifier = Modifier.fillMaxSize(),
                contentScale = ContentScale.Fit,
            )
        }

        if (!state.streaming && !state.starting && frame == null && still == null) {
            Column(
                modifier = Modifier.align(Alignment.Center).padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(
                    state.error ?: "The live screen is not running.",
                    color = Color.White,
                    textAlign = TextAlign.Center,
                    style = MaterialTheme.typography.bodyMedium,
                )

                TextButton(onClick = { viewModel.dismissError(); viewModel.start() }) { Text("Try again") }

                // screen.capture does not go through the streaming pipeline at all, so it is still
                // available when live capture is not.
                TextButton(onClick = viewModel::captureStill) { Text("Take a screenshot instead") }
            }
        }

        AnimatedVisibility(
            visible = state.chromeVisible,
            modifier = Modifier.align(Alignment.BottomCenter),
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.92f))
                    // Claims the touches on the panel's own gaps, which would otherwise fall through
                    // to the desktop underneath: a near miss on a button must not close the panel.
                    .pointerInput(Unit) {}
                    .navigationBarsPadding()
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                state.refusal?.let { refusal ->
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            refusal,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.error,
                            modifier = Modifier.weight(1f),
                        )

                        TextButton(onClick = viewModel::dismissError) { Text("OK") }
                    }
                }

                PointerControls(
                    mode = state.pointerMode,
                    inputEnabled = state.inputEnabled,
                    directGesture = state.directGesture,
                    inputStatus = state.inputStatus,
                    onModeChange = viewModel::setPointerMode,
                    onDirectGestureChange = viewModel::setDirectGesture,
                    onInputEnabledChange = viewModel::setInputEnabled,
                    onClick = viewModel::click,
                    onDoubleClick = viewModel::doubleClick,
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    TextButton(onClick = { viewModel.setKeyboardVisible(!state.keyboardVisible) }) {
                        Text(if (state.keyboardVisible) "Hide keyboard" else "Keyboard")
                    }

                    if (quality != null) {
                        QualityToggle(expanded = qualityVisible, onToggle = { qualityVisible = !qualityVisible })
                    }

                    TextButton(onClick = viewModel::toggleDiagnostics) {
                        Text(if (state.diagnosticsVisible) stats.summary else "Stats")
                    }
                }

                // The full measurement, not just the headline: what arrived, what was drawn, how old
                // the picture on screen is and how sure that figure is.
                AnimatedVisibility(visible = state.diagnosticsVisible) {
                    Text(
                        stats.detail,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                AnimatedVisibility(visible = qualityVisible && quality != null) {
                    quality?.let { settings ->
                        Column(Modifier.heightIn(max = 300.dp).verticalScroll(rememberScrollState())) {
                            QualitySettingsPanel(
                                title = "Screen quality",
                                view = settings.screen,
                                encoder = settings.encoder,
                                onChange = viewModel::setScreenQuality,
                            )
                        }
                    }
                }

                AnimatedVisibility(visible = state.keyboardVisible) {
                    KeyboardControls(
                        inputEnabled = state.inputEnabled,
                        modifiers = state.modifiers,
                        onType = viewModel::type,
                        onKey = viewModel::pressKey,
                        onToggleModifier = viewModel::toggleModifier,
                        onClearModifiers = viewModel::clearModifiers,
                    )
                }
            }
        }
    }
}

/**
 * How far the pointer moves per pixel of finger movement in touchpad mode.
 *
 * Slightly above 1 so the pointer crosses a desktop in a comfortable swipe rather than several. Not
 * a setting: one number that feels right beats a slider nobody adjusts twice.
 */
private const val TOUCHPAD_SENSITIVITY = 1.6f
