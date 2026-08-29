package dev.nexus.control.protocol

/**
 * The wire constants shared with PC Core.
 *
 * These are copied deliberately rather than fetched: they are a contract, and a contract that can
 * change under you at runtime is not one. `PcCore.Protocol` on the other side holds the same
 * values, and `shared/protocol/protocol-vectors.json` proves the two agree.
 */
object NexusProtocol {

    /** Bumped only when the message shapes change incompatibly. PC Core rejects anything else. */
    const val VERSION: Int = 1

    /** Offered on the WebSocket upgrade; PC Core echoes it when it recognises it. */
    const val SUBPROTOCOL: String = "nexus.v1"

    const val PATH: String = "/nexus"

    /**
     * The one fixed port the remote transport listens on. A pairing code may name a different one
     * — the owner can reconfigure PC Core — but the address it names must still be a Tailscale
     * address, which is the check that actually matters.
     */
    const val DEFAULT_PORT: Int = 47812

    object Actions {
        const val AUTHENTICATE = "auth.authenticate"
        const val ENROLL = "pairing.enroll"

        const val SYSTEM_INFO = "system.get_info"
        const val LIST_PROCESSES = "system.list_processes"
        const val LIST_FILES = "files.list"
        const val STAT_FILE = "files.stat"
        const val CAPTURE_SCREEN = "screen.capture"
        const val GET_SECURITY_STATE = "security.get_state"
        const val ARM = "security.arm"
        const val DISARM = "security.disarm"
    }

    object Events {
        const val CHALLENGE = "auth.challenge"
        const val SECURITY_STATE_CHANGED = "security.state_changed"
    }

    /**
     * The closed set PC Core promises. Anything else is treated as an unexpected failure rather
     * than quietly ignored — a new code the phone has never heard of is worth showing plainly.
     */
    object Errors {
        const val INVALID_REQUEST = "invalid_request"
        const val UNSUPPORTED_PROTOCOL_VERSION = "unsupported_protocol_version"
        const val UNKNOWN_ACTION = "unknown_action"
        const val INVALID_PARAMETERS = "invalid_parameters"
        const val PATH_NOT_ALLOWED = "path_not_allowed"
        const val NOT_FOUND = "not_found"
        const val ACCESS_DENIED = "access_denied"
        const val UNSUPPORTED = "unsupported"
        const val INVALID_STATE_TRANSITION = "invalid_state_transition"
        const val ALREADY_PAIRED = "already_paired"
        const val PAIRING_FAILED = "pairing_failed"
        const val AUTHENTICATION_FAILED = "authentication_failed"
        const val NOT_AUTHORIZED = "not_authorized"
        const val INTERNAL_ERROR = "internal_error"
    }

    object MessageTypes {
        const val REQUEST = "request"
        const val RESPONSE = "response"
        const val EVENT = "event"
    }

    /** Domain separation for the two HMAC proofs. Mirrors `DeviceAuthenticator` / `RemoteEnrollmentService`. */
    object Context {
        const val AUTH = "nexus-auth-v1"
        const val PAIRING = "nexus-pair-v1"
    }
}
