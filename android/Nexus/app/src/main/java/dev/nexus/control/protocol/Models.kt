package dev.nexus.control.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The capability results the app reads, mirroring the records PC Core returns.
 *
 * Only the fields the UI uses are declared. `ignoreUnknownKeys` covers the rest, so PC Core can add
 * fields without breaking a phone that has not been rebuilt.
 */

/**
 * The owner's security state.
 *
 * Modelled as a string on the wire and mapped here rather than decoded straight into an enum: a
 * future PC Core could introduce a fourth state, and a phone that crashes on an unrecognised value
 * would be a poor way to find out. Unknown maps to null and the UI says so plainly.
 */
enum class SecurityState(val wire: String) {
    OWNER_PRESENT("OWNER_PRESENT"),
    ARMED("ARMED"),
    SECURITY_SESSION("SECURITY_SESSION"),
    ;

    companion object {
        fun fromWire(value: String?): SecurityState? = entries.firstOrNull { it.wire == value }
    }
}

@Serializable
data class AuthenticatedSession(
    val authenticated: Boolean = false,
    @SerialName("actor_type") val actorType: String = "",
    @SerialName("device_id") val deviceId: String? = null,
    @SerialName("session_id") val sessionId: String? = null,
    @SerialName("pc_instance_id") val pcInstanceId: String? = null,
    @SerialName("allowed_roots") val allowedRoots: List<String> = emptyList(),

    /**
     * Whether the PC will refuse everything but the session handshake until the owner authenticates.
     * Defaults to false so a phone built against this protocol still works against an older PC Core.
     */
    @SerialName("owner_authorization_required") val ownerAuthorizationRequired: Boolean = false,

    /** Whether this phone has already registered its owner-authentication key with the PC. */
    @SerialName("owner_key_registered") val ownerKeyRegistered: Boolean = false,

    /**
     * The PC serves only newer builds of this app. Until it updates, `update.check` and
     * `update.apk.get` are all this session may do. False from a PC Core that predates the check.
     */
    @SerialName("update_required") val updateRequired: Boolean = false,

    @SerialName("minimum_client_version_code") val minimumClientVersionCode: Int = 0,
)

/** What `session.status` reports about this connection. */
@Serializable
data class OwnerSessionStatus(
    val authorized: Boolean = false,
    @SerialName("owner_key_registered") val ownerKeyRegistered: Boolean = false,
    @SerialName("expires_at_utc") val expiresAtUtc: String? = null,
    val reason: String = "",
)

/** The result of `session.authorize` or `session.resume`. */
@Serializable
data class OwnerSessionGrant(
    val authorized: Boolean = false,

    /**
     * Kept in memory only, and never written to disk. Losing it on process death is precisely what
     * makes a cold launch ask for a fingerprint again.
     */
    val ticket: String = "",
    @SerialName("expires_at_utc") val expiresAtUtc: String = "",
    @SerialName("idle_minutes") val idleMinutes: Int = 15,
)

/** The result of `session.enroll_key`. */
@Serializable
data class OwnerKeyRegistration(
    val registered: Boolean = false,
    val reason: String = "",
)

@Serializable
data class EnrollmentResult(
    @SerialName("pc_instance_id") val pcInstanceId: String,
    @SerialName("device_id") val deviceId: String,
    @SerialName("shared_secret") val sharedSecret: String,
    @SerialName("paired_at_utc") val pairedAtUtc: String = "",
)

@Serializable
data class AuthChallenge(
    @SerialName("challenge_id") val challengeId: String,
    val challenge: String,
    @SerialName("expires_at_utc") val expiresAtUtc: String = "",
)

@Serializable
data class SystemInfo(
    @SerialName("machine_name") val machineName: String = "",
    @SerialName("os_description") val osDescription: String = "",
    @SerialName("os_version") val osVersion: String = "",
    @SerialName("os_architecture") val osArchitecture: String = "",
    @SerialName("pc_core_version") val pcCoreVersion: String = "",
    @SerialName("runtime_version") val runtimeVersion: String = "",
    @SerialName("processor_count") val processorCount: Int = 0,
    @SerialName("uptime_seconds") val uptimeSeconds: Long = 0,
    @SerialName("timestamp_local") val timestampLocal: String = "",
    @SerialName("is_elevated") val isElevated: Boolean = false,
)

@Serializable
data class ProcessEntry(
    val pid: Int = 0,
    val name: String = "",
    @SerialName("working_set_bytes") val workingSetBytes: Long? = null,
)

@Serializable
data class ProcessList(
    val count: Int = 0,
    val processes: List<ProcessEntry> = emptyList(),
)

@Serializable
data class FileEntry(
    val name: String = "",

    /**
     * The full path, as PC Core resolved it. Used verbatim when navigating: the app never joins
     * paths itself, so there is no client-side idea of what a separator is or where a root ends.
     */
    val path: String = "",

    val type: String = "file",

    @SerialName("size_bytes") val sizeBytes: Long? = null,
    @SerialName("modified_utc") val modifiedUtc: String? = null,
    @SerialName("is_hidden") val isHidden: Boolean = false,
    @SerialName("is_read_only") val isReadOnly: Boolean = false,
    @SerialName("is_link") val isLink: Boolean = false,
) {
    val isDirectory: Boolean get() = type.equals("directory", ignoreCase = true)
}

@Serializable
data class ListFilesResult(
    val path: String = "",
    @SerialName("entry_count") val entryCount: Int = 0,
    val entries: List<FileEntry> = emptyList(),
    @SerialName("skipped_count") val skippedCount: Int = 0,

    /** The folder above, as PC Core resolved it; null at a root. Absent from a PC Core before v1.4. */
    val parent: String? = null,

    /** More entries exist than the PC returns in one listing. */
    val truncated: Boolean = false,
)

@Serializable
data class StatResult(
    val path: String = "",
    val entry: FileEntry = FileEntry(),
    @SerialName("accessed_utc") val accessedUtc: String? = null,
)

@Serializable
data class CaptureScreenResult(
    val display: String = "",
    val width: Int = 0,
    val height: Int = 0,
    @SerialName("source_width") val sourceWidth: Int = 0,
    @SerialName("source_height") val sourceHeight: Int = 0,
    @SerialName("content_type") val contentType: String = "",
    val encoding: String = "",

    /** Base64 (standard alphabet, not base64url — PC Core uses `Convert.ToBase64String` here). */
    val data: String = "",

    @SerialName("captured_at_utc") val capturedAtUtc: String = "",
)

@Serializable
data class SecurityStateResult(
    val state: String = "",
    @SerialName("previous_state") val previousState: String? = null,
    @SerialName("since_utc") val sinceUtc: String = "",
) {
    val securityState: SecurityState? get() = SecurityState.fromWire(state)
}

@Serializable
data class SecurityCommandResult(
    /** False when the state was already what was asked for. Both are successes. */
    val changed: Boolean = false,
    val state: String = "",
    @SerialName("previous_state") val previousState: String? = null,
    @SerialName("since_utc") val sinceUtc: String = "",
) {
    val securityState: SecurityState? get() = SecurityState.fromWire(state)
}

/**
 * The payload of `security.state_changed`.
 *
 * Note that it is *not* the same shape as `security.get_state`: the event names the two states as
 * `previous_state` and `current_state`, and carries no timestamp of its own — the envelope's
 * `timestamp_utc` is when it happened.
 */
@Serializable
data class SecurityStateChanged(
    @SerialName("previous_state") val previousState: String = "",
    @SerialName("current_state") val currentState: String = "",
) {
    val securityState: SecurityState? get() = SecurityState.fromWire(currentState)
}
