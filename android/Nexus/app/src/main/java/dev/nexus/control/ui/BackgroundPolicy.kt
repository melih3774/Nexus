package dev.nexus.control.ui

/**
 * What happens to the connection when the app stops being looked at.
 *
 * **v0.5 wrote this rule and v0.7 broke it without noticing.** The original policy was simple and
 * right for what existed at the time: a connection exists while the app is being used and is
 * dropped when it is not, because nothing needed to arrive while the app was closed and a phone
 * holding a WebSocket open all day is a battery cost with no feature behind it.
 *
 * Then v0.7 added the one feature that does need to arrive while the app is closed — the security
 * alert — and gave it an opt-in foreground service to hold the connection open. Nobody revisited
 * `ON_STOP`. So on a real phone the service started, held a *dead* socket, and the alert could
 * never arrive: the PC logged the remote session ending the moment the app was backgrounded, and
 * the service sat there collecting an event flow that would never emit again.
 *
 * The rule is now stated once, here, where it can be read and tested:
 *
 * - alerts off (the default): unchanged from v0.5. Background the app, drop the connection.
 * - alerts on: hold it. That is not a battery regression sneaking in — it is the entire thing the
 *   owner opted into, and the permanent notification says so.
 */
object BackgroundPolicy {

    /**
     * Whether backgrounding should drop the connection.
     *
     * @param alertsEnabled whether the owner turned on `Alert me on this phone`.
     */
    fun shouldDisconnectOnStop(alertsEnabled: Boolean): Boolean = !alertsEnabled

    /**
     * Whether opening the app should (re)start the alert service.
     *
     * The switch on the Security screen reads a stored preference, not a running service, and the
     * two drift apart every time Android stops the app — a reboot, a force stop, an install, or
     * memory pressure. Without this the switch reads "on" while nothing is running and the owner
     * believes they are covered when they are not.
     *
     * Only ever on opening the app, and only when the owner already asked for it. That is not a
     * mechanism for surviving a force stop, and is not meant to be: if the owner stops Nexus, it
     * stays stopped until they open it again.
     */
    fun shouldStartAlertServiceOnStart(isPaired: Boolean, alertsEnabled: Boolean): Boolean =
        isPaired && alertsEnabled
}
