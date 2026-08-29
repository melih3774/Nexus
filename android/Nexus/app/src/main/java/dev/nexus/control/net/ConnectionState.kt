package dev.nexus.control.net

/**
 * Why the phone cannot reach the PC.
 *
 * These are separated because they call for different actions from the owner, and a single
 * "couldn't connect" would hide which one applies. Nexus does not manage Tailscale — it cannot fix
 * any of these — so the least it can do is say which one happened.
 */
enum class FailureReason {
    /** The phone has no usable network at all. Nothing to do with Nexus or Tailscale. */
    NoNetwork,

    /**
     * The phone has a network but no Tailscale address of its own, so there is no private route to
     * the PC. The owner needs to open Tailscale on the phone and connect.
     */
    PrivateNetworkUnavailable,

    /**
     * The phone is on the tailnet but the PC did not answer: PC Core is not running, or Tailscale is
     * not connected on the PC, or the PC is asleep.
     */
    PcUnreachable,

    /**
     * The PC answered and rejected the credential. Not transient — reconnecting will not help, so
     * the app stops trying and the owner re-pairs.
     */
    AuthenticationFailed,

    /** The PC answered with something this build does not understand. */
    ProtocolError,
}

/**
 * What the connection is doing, as one value.
 *
 * A single sealed hierarchy rather than a handful of booleans: "connecting and authenticated" and
 * "failed but connected" are states that should not be representable.
 */
sealed interface ConnectionState {

    /** No connection wanted. The app is not trying. */
    data object Idle : ConnectionState

    /** Opening a socket. */
    data object Connecting : ConnectionState

    /** Socket open, proving possession of the shared secret. */
    data object Authenticating : ConnectionState

    /** Authenticated. Requests and events flow. */
    data class Connected(
        val deviceId: String,
        val sessionId: String,
        val pcInstanceId: String,
        val allowedRoots: List<String>,
    ) : ConnectionState

    /**
     * Not connected, with a reason.
     *
     * @param willRetry whether the manager is going to try again on its own. False for
     *   [FailureReason.AuthenticationFailed], where retrying is pointless.
     */
    data class Failed(
        val reason: FailureReason,
        val willRetry: Boolean,
        val attempt: Int,
    ) : ConnectionState

    val isConnected: Boolean get() = this is Connected
}
