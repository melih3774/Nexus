package dev.nexus.control

import dev.nexus.control.protocol.NativeInputStatus
import dev.nexus.control.remote.DirectGesture
import dev.nexus.control.remote.PointerModeUi
import dev.nexus.control.remote.RemoteUiState
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * When Direct mode sends touch, and when it does not.
 *
 * Three separate facts decide it, and all three have to line up: the owner has to be in Direct mode
 * at all, they have to want touch behaviour, and the PC has to be able to inject touch. Getting this
 * wrong in either direction is quiet rather than loud — sending touch to a PC that cannot inject it
 * still works, because the PC falls back for us, and sending pointer events when touch was wanted
 * simply feels like the old behaviour — so it is worth a test rather than a reading.
 */
class DirectGestureTest {

    private fun status(touchAvailable: Boolean) = NativeInputStatus(
        path = "NATIVE_BROKER",
        state = "READY",
        reachesElevatedWindows = true,
        touchAvailable = touchAvailable,
    )

    @Test
    fun `direct mode with touch available sends touch`() {
        val state = RemoteUiState(
            pointerMode = PointerModeUi.DIRECT,
            directGesture = DirectGesture.TOUCH,
            inputStatus = status(touchAvailable = true),
        )

        assertTrue(state.directUsesTouch)
    }

    @Test
    fun `touchpad mode never sends touch`() {
        // Touchpad is a relative pointer by definition. A touch contact has no meaning there, and
        // sending one would put the pointer somewhere the finger is not.
        val state = RemoteUiState(
            pointerMode = PointerModeUi.TOUCHPAD,
            directGesture = DirectGesture.TOUCH,
            inputStatus = status(touchAvailable = true),
        )

        assertFalse(state.directUsesTouch)
    }

    @Test
    fun `the owner can ask for mouse behaviour in direct mode`() {
        val state = RemoteUiState(
            pointerMode = PointerModeUi.DIRECT,
            directGesture = DirectGesture.POINTER,
            inputStatus = status(touchAvailable = true),
        )

        assertFalse(state.directUsesTouch)
    }

    @Test
    fun `a PC that cannot inject touch is not asked to`() {
        val state = RemoteUiState(
            pointerMode = PointerModeUi.DIRECT,
            directGesture = DirectGesture.TOUCH,
            inputStatus = status(touchAvailable = false),
        )

        assertFalse(state.directUsesTouch)
    }

    /**
     * Before the PC has answered — or on a PC too old to know the question — Direct mode still sends
     * touch. It is safe: `input.touch` falls back to a pointer and a button on the PC, so the worst
     * case is the behaviour Nexus had before touch existed.
     */
    @Test
    fun `an unknown status still sends touch`() {
        val state = RemoteUiState(
            pointerMode = PointerModeUi.DIRECT,
            directGesture = DirectGesture.TOUCH,
            inputStatus = null,
        )

        assertTrue(state.directUsesTouch)
    }

    @Test
    fun `touch is the default`() {
        assertTrue(RemoteUiState().directUsesTouch)
    }

    @Test
    fun `a degraded broker is not reported as fully native`() {
        val degraded = NativeInputStatus(
            path = "NATIVE_BROKER",
            state = "DEGRADED",
            reachesElevatedWindows = false,
            touchAvailable = true,
        )

        assertFalse(degraded.isFullyNative)
    }
}
