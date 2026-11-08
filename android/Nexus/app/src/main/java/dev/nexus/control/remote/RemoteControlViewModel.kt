package dev.nexus.control.remote

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.PcConnectionManager
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.DisplayInfo
import dev.nexus.control.protocol.NativeInputStatus
import dev.nexus.control.protocol.NexusKey
import dev.nexus.control.protocol.PointerButton
import dev.nexus.control.protocol.TouchAction
import dev.nexus.control.protocol.MediaQualityResult
import dev.nexus.control.protocol.SetMediaQualityParams
import dev.nexus.control.protocol.StreamReportParams
import android.view.Surface
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Which pointer model the touch surface is using. */
enum class PointerModeUi { DIRECT, TOUCHPAD }

/**
 * What a finger means in Direct mode.
 *
 * **This is a choice about behaviour, not about plumbing.** It does not select between input
 * mechanisms — the PC decides that on its own and the phone never sees it. It decides whether a tap
 * on the desktop image is a touch on a touch display or a mouse click at that point, which are
 * genuinely different things on Windows and which the owner may reasonably prefer differently.
 *
 * [TOUCH] is the default because it is what Direct mode is trying to be. A tap is a tap, a drag
 * drags, and applications that understand touch get touch instead of a mouse being moved around.
 * [POINTER] is there because some desktop software behaves worse under touch than under a mouse,
 * and finding that out is the owner's business rather than something to argue with.
 */
enum class DirectGesture { TOUCH, POINTER }

data class RemoteUiState(
    val streaming: Boolean = false,
    val starting: Boolean = false,
    val error: String? = null,
    val pointerMode: PointerModeUi = PointerModeUi.DIRECT,
    val inputEnabled: Boolean = false,
    val modifiers: Set<NexusKey> = emptySet(),
    val displays: List<DisplayInfo> = emptyList(),
    val displayIndex: Int = 0,
    val chromeVisible: Boolean = true,
    val keyboardVisible: Boolean = false,
    val diagnosticsVisible: Boolean = false,
    val refusal: String? = null,
    val directGesture: DirectGesture = DirectGesture.TOUCH,

    /** What the PC says about its own input path. Null until it has been asked, or on an older PC. */
    val inputStatus: NativeInputStatus? = null,
) {
    /**
     * Whether Direct mode should send touch right now.
     *
     * Both halves matter. The owner may prefer pointer behaviour, and the PC may not be able to
     * inject touch at all — in which case sending touch would still work, because the PC falls back
     * for us, but there is no reason to make it.
     */
    val directUsesTouch: Boolean
        get() = pointerMode == PointerModeUi.DIRECT &&
            directGesture == DirectGesture.TOUCH &&
            (inputStatus?.touchAvailable ?: true)
}

/**
 * The remote-control screen's state and lifecycle.
 *
 * **What it is careful about is stopping.** Starting a stream is easy; making sure one never
 * outlives the screen, the connection, or the app being backgrounded is the part that decides
 * whether the PC ends up capturing its desktop for nobody. Every exit path — navigating away,
 * losing the connection, the process going to the background, the view model being cleared — goes
 * through the same teardown.
 */
class RemoteControlViewModel(
    private val connection: PcConnectionManager,
    private val session: RemoteControlSession,
) : ViewModel() {

    /** The offset between this phone's clock and the PC's, so latency is a measurement. */
    private val clock = StreamClock()

    val pipeline = FramePipeline(
        scope = viewModelScope,
        clock = clock,
        onKeyframeNeeded = { session.requestKeyframe() },
    )

    private val qualityClient = MediaQualityClient(connection)

    private val _quality = MutableStateFlow<MediaQualityResult?>(null)

    /** The PC's streaming settings for the screen and the camera, or null until read. */
    val quality: StateFlow<MediaQualityResult?> = _quality.asStateFlow()

    /** Set once the start response names the stream; until then any stream's frames are accepted. */
    @Volatile
    private var acceptedStreamId = -1L

    private var reportJob: Job? = null

    private val _state = MutableStateFlow(RemoteUiState())
    val state: StateFlow<RemoteUiState> = _state.asStateFlow()

    val frames = pipeline.frames
    val cursor = pipeline.cursor
    val stats = pipeline.stats

    /**
     * A single screenshot, shown when the live stream cannot run.
     *
     * Kept separate from the frame pipeline rather than pushed through it: it is a different
     * capability with a different failure mode, and mixing them would be the coupling the two paths
     * were separated to avoid.
     */
    private val _still = MutableStateFlow<ImageBitmap?>(null)
    val still: StateFlow<ImageBitmap?> = _still.asStateFlow()

    private var frameJob: Job? = null
    private var connectionJob: Job? = null

    init {
        // The stream belongs to a connection. If that connection ends, whatever this screen thought
        // was running is already gone, and the local state has to agree or the UI will offer
        // controls that do nothing.
        connectionJob = viewModelScope.launch {
            connection.state.collect { state ->
                if (state !is ConnectionState.Connected && _state.value.streaming) {
                    session.forgetHeldState()
                    lastContact = null
                    connection.streamFrameSink = null
                    reportJob?.cancel()
                    pipeline.stop()
                    clock.reset()

                    _state.value = _state.value.copy(
                        streaming = false,
                        inputEnabled = false,
                        modifiers = emptySet(),
                        error = "The connection to the PC ended.",
                    )
                }
            }
        }
    }

    // --- Stream lifecycle -----------------------------------------------------------------------

    fun start() {
        if (_state.value.streaming || _state.value.starting) return

        _state.value = _state.value.copy(starting = true, error = null)

        viewModelScope.launch {
            try {
                val displays = runCatching { session.listDisplays() }.getOrDefault(emptyList())

                // The sink goes in before the stream is asked for. The PC starts capturing the moment
                // it accepts, so the configuration frame and the first keyframe can arrive before
                // the response does — and a decoder that missed the configuration has nothing to
                // start from until someone thinks to ask for another.
                pipeline.start()
                acceptedStreamId = -1
                connection.streamFrameSink = { frame ->
                    val accepted = acceptedStreamId

                    if (accepted < 0 || frame.header.streamId == accepted) {
                        pipeline.submit(frame)
                    }
                }

                val started = session.startStream(_state.value.displayIndex.takeIf { displays.size > 1 })

                acceptedStreamId = started.streamId
                pipeline.plan = started.plan
                _still.value = null

                frameJob?.cancel()
                frameJob = null

                startReporting()

                _state.value = _state.value.copy(
                    streaming = true,
                    starting = false,
                    displays = displays,
                    displayIndex = started.display.index,
                    error = null,
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(
                    starting = false,
                    streaming = false,
                    error = describe(e),
                )
            }
        }
    }

    /**
     * Stops everything this screen started.
     *
     * Input is ended before the stream: if the order were reversed, a connection that died in
     * between would leave the PC holding a button with no viewer and no obvious cause.
     */
    fun stop() {
        // Before anything else. input.end releases whatever the PC is holding, but a contact the
        // phone still believes in would make the next gesture start mid-drag after a reconnect.
        if (session.isContactDown) cancelContact()

        frameJob?.cancel()
        frameJob = null

        connection.streamFrameSink = null
        acceptedStreamId = -1
        reportJob?.cancel()
        reportJob = null

        pipeline.stop()

        val wasStreaming = _state.value.streaming
        val hadInput = _state.value.inputEnabled

        lastContact = null

        _state.value = _state.value.copy(
            streaming = false,
            starting = false,
            inputEnabled = false,
            modifiers = emptySet(),
        )

        if (!wasStreaming && !hadInput) return

        viewModelScope.launch {
            if (hadInput) session.endInput()
            if (wasStreaming) session.stopStream()
        }
    }

    /**
     * Falls back to one screenshot when streaming will not start.
     *
     * Deliberately not automatic and not repeated: a screenshot on a timer would be a slideshow that
     * costs the PC a full capture and PNG encode per frame while looking like streaming.
     */
    fun captureStill() {
        _state.value = _state.value.copy(starting = true, error = null)

        viewModelScope.launch {
            try {
                val shot = session.captureStill(maxWidth = 1280)
                val bytes = Base64.decode(shot.data, Base64.DEFAULT)
                val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)

                _still.value = bitmap?.asImageBitmap()

                _state.value = _state.value.copy(
                    starting = false,
                    error = if (bitmap == null) "The screenshot could not be decoded." else null,
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(starting = false, error = describe(e))
            }
        }
    }

    /** The video surface came or went. The decoder follows it. */
    fun attachSurface(surface: Surface?) = pipeline.attachSurface(surface)

    /**
     * Measures the clock, then tells the PC once a second what the phone is seeing.
     *
     * The report is what lets the PC tell a slow link from a slow phone, and its answer is the plan in
     * force, which is how an adaptive change reaches the stats overlay without polling for it. The
     * clock is re-measured every minute because a phone that roams from Wi-Fi to mobile data changes
     * the round trip, and with it the error bound on every latency figure.
     */
    private fun startReporting() {
        reportJob?.cancel()
        reportJob = viewModelScope.launch {
            clock.reset()
            session.synchroniseClock(clock)

            var tick = 0

            while (isActive) {
                delay(REPORT_INTERVAL_MS)

                // Only this second's pictures. A latency figure carried over from before a stall is
                // not a measurement of the stall, and the PC adapts on every number it is sent.
                val report = pipeline.takeReport()

                val result = session.report(
                    StreamReportParams(
                        latencyMs = report.latencyMs,
                        decodeMs = report.decodeMs,
                        renderedFps = report.renderedFps,
                        dropped = report.dropped,
                    ),
                )

                result?.plan?.takeIf { it.isNotBlank() }?.let { pipeline.plan = it }

                if (++tick % CLOCK_RESYNC_REPORTS == 0) {
                    session.synchroniseClock(clock, samples = 3)
                }
            }
        }
    }

    fun loadQuality() {
        viewModelScope.launch { _quality.value = qualityClient.get() ?: _quality.value }
    }

    /** Changes the screen's settings. The running stream re-plans on its next frame. */
    fun setScreenQuality(change: SetMediaQualityParams) {
        viewModelScope.launch {
            try {
                _quality.value = qualityClient.set(change.copy(target = "screen")) ?: _quality.value
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(refusal = describe(e))
            }
        }
    }

    fun selectDisplay(index: Int) {
        if (index == _state.value.displayIndex) return

        _state.value = _state.value.copy(displayIndex = index)

        if (_state.value.streaming) {
            stop()
            start()
        }
    }

    // --- Input ---------------------------------------------------------------------------------

    fun setInputEnabled(enabled: Boolean) {
        if (enabled == _state.value.inputEnabled) return

        viewModelScope.launch {
            if (enabled) {
                val ok = session.beginInput()

                _state.value = _state.value.copy(
                    inputEnabled = ok,
                    error = if (ok) _state.value.error else "The PC would not enable remote input.",
                )

                if (ok) refreshInputStatus()
            } else {
                if (session.isContactDown) cancelContact()
                session.endInput()
                lastContact = null
                _state.value = _state.value.copy(inputEnabled = false, modifiers = emptySet())
            }
        }
    }

    fun setPointerMode(mode: PointerModeUi) {
        if (mode == _state.value.pointerMode) return

        // A contact belonging to the mode being left has nothing to lift it later.
        if (session.isContactDown) cancelContact()

        _state.value = _state.value.copy(pointerMode = mode)
    }

    fun setDirectGesture(gesture: DirectGesture) {
        if (gesture == _state.value.directGesture) return

        if (session.isContactDown) cancelContact()

        _state.value = _state.value.copy(directGesture = gesture)
    }

    /**
     * Asks the PC which input path it is using.
     *
     * Called when the screen opens and after input is enabled, not on a timer: the answer changes
     * when the broker starts or stops, which is rare, and polling it would put a round trip on the
     * same connection the frames are coming down.
     */
    fun refreshInputStatus() {
        viewModelScope.launch {
            val status = session.inputStatus()

            if (status != null) {
                _state.value = _state.value.copy(inputStatus = status)
            }
        }
    }

    fun toggleChrome() {
        _state.value = _state.value.copy(chromeVisible = !_state.value.chromeVisible)
    }

    fun setKeyboardVisible(visible: Boolean) {
        _state.value = _state.value.copy(keyboardVisible = visible)
    }

    fun toggleDiagnostics() {
        _state.value = _state.value.copy(diagnosticsVisible = !_state.value.diagnosticsVisible)
    }

    fun dismissError() {
        _state.value = _state.value.copy(error = null, refusal = null)
    }

    // --- Touch ---------------------------------------------------------------------------------

    /** True while a contact is down, which is what makes a move a drag rather than a hover. */
    val isDragging: Boolean get() = session.isContactDown

    fun touchDown(point: DesktopPoint) = touch(TouchAction.DOWN, point)

    fun touchMove(point: DesktopPoint) = touch(TouchAction.MOVE, point)

    fun touchUp(point: DesktopPoint) = touch(TouchAction.UP, point)

    /**
     * Finishes a drag by lifting the contact where it actually is.
     *
     * Not a cancel: cancelling tells Windows to abandon the gesture, which would drop whatever was
     * being dragged back where it started rather than where the owner let go. The last position is
     * remembered here because Windows refuses a release at any other point.
     */
    fun endTouchDrag() {
        val last = lastContact

        if (last != null) {
            touchUp(last)
        } else {
            cancelContact()
        }
    }

    /**
     * Lifts a contact the phone believes is still down, wherever it was.
     *
     * The position is not needed: the PC remembers where the contact last was, because Windows
     * refuses a touch release at a different place than the last update and a refused release is how
     * a finger gets stuck on the desktop.
     */
    fun cancelContact() {
        if (!session.isContactDown) return

        session.touch(
            TouchAction.CANCEL,
            lastContact ?: DesktopPoint(0.0, 0.0),
            _state.value.displayIndex,
        )

        lastContact = null
    }

    private fun touch(action: TouchAction, point: DesktopPoint) {
        if (!_state.value.inputEnabled) return

        lastContact = when (action) {
            TouchAction.DOWN, TouchAction.MOVE -> point
            TouchAction.UP, TouchAction.CANCEL -> null
        }

        session.touch(action, point, _state.value.displayIndex)
    }

    /** Where the live contact was last seen. Windows refuses a release anywhere else. */
    private var lastContact: DesktopPoint? = null

    // --- Pointer -------------------------------------------------------------------------------

    fun moveTo(point: DesktopPoint) {
        if (!_state.value.inputEnabled) return
        session.moveTo(point, _state.value.displayIndex)
    }

    fun moveBy(dx: Double, dy: Double) {
        if (!_state.value.inputEnabled) return
        session.moveBy(dx, dy)
    }

    fun click(button: PointerButton = PointerButton.LEFT) = onInput { session.click(button) }

    fun doubleClick() = onInput { session.doubleClick() }

    fun pressPointer() = onInput { session.pressButton() }

    fun releasePointer() = onInput { session.releaseButton() }

    fun scroll(vertical: Double, horizontal: Double = 0.0) {
        if (!_state.value.inputEnabled) return
        session.scroll(vertical, horizontal)
    }

    // --- Keyboard ------------------------------------------------------------------------------

    fun toggleModifier(key: NexusKey) {
        if (!_state.value.inputEnabled) return

        session.toggleModifier(key)
        _state.value = _state.value.copy(modifiers = session.activeModifiers)
    }

    fun clearModifiers() {
        session.clearModifiers()
        _state.value = _state.value.copy(modifiers = session.activeModifiers)
    }

    fun pressKey(key: NexusKey) = onInput {
        session.pressKey(key)
        _state.value = _state.value.copy(modifiers = session.activeModifiers)
    }

    fun type(text: String) = onInput { session.type(text) }

    private fun onInput(action: suspend () -> Unit): Job? {
        if (!_state.value.inputEnabled) return null

        return viewModelScope.launch {
            try {
                action()
                _state.value = _state.value.copy(
                    modifiers = session.activeModifiers,
                    refusal = session.lastRefusal,
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(refusal = describe(e))
            }
        }
    }

    private fun describe(cause: Throwable): String = when (cause) {
        is dev.nexus.control.net.NotConnectedException -> "Not connected to the PC."

        is dev.nexus.control.net.NexusRequestException -> when (cause.error.code) {
            dev.nexus.control.protocol.NexusProtocol.Errors.UNSUPPORTED ->
                "This PC cannot stream its screen right now."

            dev.nexus.control.protocol.NexusProtocol.Errors.NOT_AUTHORIZED ->
                "Remote control is not enabled. Turn on Control first."

            dev.nexus.control.protocol.NexusProtocol.Errors.RATE_LIMITED ->
                "Slow down — the PC is limiting how fast input can arrive."

            else -> cause.error.message.ifBlank { "The PC refused that." }
        }

        else -> "Something went wrong talking to the PC."
    }

    override fun onCleared() {
        // The last line of defence. Whatever route the screen left by, nothing is left running.
        stop()
        connectionJob?.cancel()
        pipeline.release()
        super.onCleared()
    }

    companion object {
        private const val REPORT_INTERVAL_MS = 1_000L
        private const val CLOCK_RESYNC_REPORTS = 60

        val Factory = viewModelFactory {
            initializer {
                val connection = this.nexusContainer.connection
                RemoteControlViewModel(connection, RemoteControlSession(connection))
            }
        }
    }
}
