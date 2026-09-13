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

        // v0.6 - live screen
        const val LIST_DISPLAYS = "screen.list_displays"
        const val STREAM_START = "screen.stream.start"
        const val STREAM_STOP = "screen.stream.stop"

        // v0.6 - remote input. INPUT_BEGIN is what actually turns typing on; the rest do nothing
        // without it.
        const val INPUT_BEGIN = "input.begin"
        const val INPUT_END = "input.end"
        const val INPUT_POINTER = "input.pointer"
        const val INPUT_BUTTON = "input.button"
        const val INPUT_SCROLL = "input.scroll"
        const val INPUT_KEY = "input.key"
        const val INPUT_TEXT = "input.text"
        const val INPUT_RELEASE_ALL = "input.release_all"

        // v0.8 - native input. INPUT_TOUCH is what Direct mode sends; INPUT_STATUS is a diagnostic
        // and never a switch, because the owner does not choose an input path.
        const val INPUT_TOUCH = "input.touch"
        const val INPUT_STATUS = "input.status"

        // v1.0 - the owner-authenticated session. These are the only things a locked Nexus may call,
        // and they exist so it can stop being locked.
        const val SESSION_STATUS = "session.status"
        const val SESSION_ENROLL_KEY = "session.enroll_key"
        const val SESSION_CHALLENGE = "session.challenge"
        const val SESSION_AUTHORIZE = "session.authorize"
        const val SESSION_RESUME = "session.resume"
        const val SESSION_LOCK = "session.lock"

        // v0.7 - the security workflow. The phone receives the alert, so it can inspect the incident
        // and answer it. Deleting evidence and changing security settings are deliberately absent:
        // those stay at the PC, behind Windows Hello.
        const val SECURITY_SESSIONS_LIST = "security.sessions.list"
        const val SECURITY_SESSIONS_GET = "security.sessions.get"
        const val SECURITY_EVIDENCE_GET = "security.evidence.get"
        const val SECURITY_THIS_WAS_ME = "security.this_was_me"
        const val SECURITY_NOT_ME = "security.not_me"

        // Live camera and microphone monitoring. FullRemote only on the PC side.
        const val MEDIA_LIVE_START = "media.live.start"
        const val MEDIA_LIVE_STOP = "media.live.stop"
        const val MEDIA_LIVE_STATUS = "media.live.status"
        const val MEDIA_LIVE_KEYFRAME = "media.live.keyframe"
        const val MEDIA_LIVE_REPORT = "media.live.report"

        // v1.2 - the video pipeline. KEYFRAME is how a decoder that lost its place asks to be given
        // one again; REPORT is how the PC finds out that the phone, rather than the link, is the
        // bottleneck; MEDIA_TIME is what makes a latency figure a measurement rather than the
        // difference between two unsynchronised clocks.
        const val STREAM_KEYFRAME = "screen.stream.keyframe"
        const val STREAM_REPORT = "screen.stream.report"
        const val MEDIA_QUALITY_GET = "media.quality.get"
        const val MEDIA_QUALITY_SET = "media.quality.set"
        const val MEDIA_TIME = "media.time"

        // v1.3 - updating this app from its PC. Both are reachable before the owner unlocks, and by
        // an app too old for anything else, so an out-of-date phone can always fetch the fix.
        const val UPDATE_CHECK = "update.check"
        const val UPDATE_APK_GET = "update.apk.get"

        // v1.4 - Files and Clipboard. All of them need the owner session on the PC side.
        const val FILES_ROOTS = "files.roots"
        const val FILES_READ = "files.read"
        const val FILES_HASH = "files.hash"
        const val FILES_UPLOAD_BEGIN = "files.upload.begin"
        const val FILES_UPLOAD_CHUNK = "files.upload.chunk"
        const val FILES_UPLOAD_FINISH = "files.upload.finish"
        const val FILES_UPLOAD_CANCEL = "files.upload.cancel"
        const val CLIPBOARD_GET = "clipboard.get"
        const val CLIPBOARD_SET = "clipboard.set"
    }

    object Events {
        const val CHALLENGE = "auth.challenge"
        const val SECURITY_STATE_CHANGED = "security.state_changed"

        /** A security incident began. The one event that becomes a high-priority notification. */
        const val SECURITY_SESSION_STARTED = "security.session_started"

        /** More evidence, more timeline. Updates the screen; never raises a second alert. */
        const val SECURITY_SESSION_UPDATED = "security.session_updated"

        const val SECURITY_SESSION_RESOLVED = "security.session_resolved"
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
        const val RATE_LIMITED = "rate_limited"
        const val INTERNAL_ERROR = "internal_error"

        /** This app is older than the PC serves; only the update path is open. */
        const val UPDATE_REQUIRED = "update_required"

        // v1.4 - Files and Clipboard.
        const val ALREADY_EXISTS = "already_exists"
        const val FILE_IN_USE = "file_in_use"
        const val FILE_CHANGED = "file_changed"
        const val DISK_FULL = "disk_full"
        const val CHECKSUM_MISMATCH = "checksum_mismatch"
        const val CLIPBOARD_UNAVAILABLE = "clipboard_unavailable"
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

        /**
         * The owner-session challenge. Newline-separated rather than NUL-separated like the two
         * above, and signed with an EC key rather than HMAC'd - it is a different mechanism proving
         * a different thing, so it deliberately shares nothing with them but a challenge.
         */
        const val OWNER_SESSION = "nexus.owner-session.v1"
    }
}
