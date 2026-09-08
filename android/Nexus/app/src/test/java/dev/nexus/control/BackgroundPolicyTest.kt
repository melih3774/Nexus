package dev.nexus.control

import dev.nexus.control.ui.BackgroundPolicy
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Whether the connection survives the app being backgrounded.
 *
 * **The bug.** With `Alert me on this phone` on, the foreground service was running, its persistent
 * notification was showing, the notification channel was correct and permitted, and no security
 * alert could ever arrive. `ON_STOP` called `disconnect()` unconditionally — v0.5 policy, written
 * when there was no background service and nothing that needed to arrive while the app was closed.
 * v0.7 added exactly such a thing and never revisited the rule, so the service held a socket the UI
 * had already closed and collected an event flow that would never emit again.
 *
 * It was invisible from inside the app: everything the owner could see said they were covered. On
 * the PC it was one line — the remote session ending the instant the app was backgrounded.
 */
class BackgroundPolicyTest {

    @Test
    fun `backgrounding drops the connection when alerts are off`() {
        // The v0.5 rule, unchanged and still the default. A phone holding a socket open all day for
        // a feature nobody asked for is a battery cost with nothing behind it.
        assertTrue(BackgroundPolicy.shouldDisconnectOnStop(alertsEnabled = false))
    }

    /** The regression. */
    @Test
    fun `backgrounding holds the connection when alerts are on`() {
        assertFalse(
            "the alert service cannot deliver an alert on a socket the UI just closed",
            BackgroundPolicy.shouldDisconnectOnStop(alertsEnabled = true),
        )
    }

    @Test
    fun `opening the app restarts the alert service the owner asked for`() {
        // Android stops services for its own reasons — a reboot, memory pressure, an app update.
        // The switch reads a stored preference, so without this it reads "on" over a service that
        // is not running, which is the one failure mode worse than being off: believing you are
        // covered when you are not.
        assertTrue(BackgroundPolicy.shouldStartAlertServiceOnStart(isPaired = true, alertsEnabled = true))
    }

    @Test
    fun `opening the app starts nothing the owner did not ask for`() {
        assertFalse(BackgroundPolicy.shouldStartAlertServiceOnStart(isPaired = true, alertsEnabled = false))
    }

    @Test
    fun `an unpaired phone never starts the alert service`() {
        // There is no PC to watch, and no credential to authenticate with. Starting a service to
        // hold a connection that cannot be made would be a permanent notification for nothing.
        assertFalse(BackgroundPolicy.shouldStartAlertServiceOnStart(isPaired = false, alertsEnabled = true))
        assertFalse(BackgroundPolicy.shouldStartAlertServiceOnStart(isPaired = false, alertsEnabled = false))
    }
}
