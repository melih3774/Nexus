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
import dev.nexus.control.protocol.NexusKey
import dev.nexus.control.protocol.PointerButton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Which pointer model the touch surface is using. */
enum class PointerModeUi { DIRECT, TOUCHPAD }

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
)

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

    val pipeline = FramePipeline(viewModelScope)

    private val _state = MutableStateFlow(RemoteUiState())
    val state: StateFlow<RemoteUiState> = _state.asStateFlow()

    val frames = pipeline.frames
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
                    pipeline.stop()

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
                val started = session.startStream(_state.value.displayIndex.takeIf { displays.size > 1 })

                pipeline.start()
                _still.value = null

                frameJob?.cancel()
                frameJob = viewModelScope.launch {
                    connection.frames.collect { frame ->
                        if (frame.header.streamId == started.streamId) {
                            pipeline.submit(frame)
                        }
                    }
                }

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
        frameJob?.cancel()
        frameJob = null

        pipeline.stop()

        val wasStreaming = _state.value.streaming
        val hadInput = _state.value.inputEnabled

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
            } else {
                session.endInput()
                _state.value = _state.value.copy(inputEnabled = false, modifiers = emptySet())
            }
        }
    }

    fun setPointerMode(mode: PointerModeUi) {
        _state.value = _state.value.copy(pointerMode = mode)
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
        pipeline.stop()
        super.onCleared()
    }

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val connection = this.nexusContainer.connection
                RemoteControlViewModel(connection, RemoteControlSession(connection))
            }
        }
    }
}
