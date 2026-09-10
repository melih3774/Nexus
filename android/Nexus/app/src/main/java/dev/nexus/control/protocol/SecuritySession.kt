package dev.nexus.control.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** How an incident ended, if it has. Mirrors `SessionResolution` on the PC. */
@Serializable
enum class SessionResolution {
    @SerialName("ACTIVE")
    ACTIVE,

    @SerialName("ACKNOWLEDGED_OWNER")
    ACKNOWLEDGED_OWNER,

    @SerialName("CONFIRMED_NOT_OWNER")
    CONFIRMED_NOT_OWNER,
    ;

    /** What the owner reads on a history row. */
    val label: String
        get() = when (this) {
            ACTIVE -> "Active"
            ACKNOWLEDGED_OWNER -> "This was me"
            CONFIRMED_NOT_OWNER -> "Not me"
        }
}

@Serializable
enum class EvidenceKind {
    @SerialName("SCREENSHOT")
    SCREENSHOT,

    @SerialName("SCREEN_RECORDING")
    SCREEN_RECORDING,

    @SerialName("CAMERA_IMAGE")
    CAMERA_IMAGE,

    @SerialName("CAMERA_VIDEO")
    CAMERA_VIDEO,

    @SerialName("AUDIO")
    AUDIO,
    ;

    /** Rendered as a bitmap. */
    val isViewableImage: Boolean get() = this == SCREENSHOT || this == CAMERA_IMAGE

    /** Played with a surface: a moving picture with optional sound. */
    val isVideo: Boolean get() = this == CAMERA_VIDEO || this == SCREEN_RECORDING

    /** Played without a surface. */
    val isAudio: Boolean get() = this == AUDIO

    /** Anything that goes through the player rather than the bitmap decoder. */
    val isPlayable: Boolean get() = isVideo || isAudio

    val label: String
        get() = when (this) {
            SCREENSHOT -> "Screenshot"
            SCREEN_RECORDING -> "Screen recording"
            CAMERA_IMAGE -> "Camera image"
            CAMERA_VIDEO -> "Camera clip"
            AUDIO -> "Audio clip"
        }
}

@Serializable
enum class IncidentTrigger {
    @SerialName("POINTER_ACTIVITY")
    POINTER_ACTIVITY,

    @SerialName("KEYBOARD_ACTIVITY")
    KEYBOARD_ACTIVITY,

    @SerialName("SESSION_UNLOCKED")
    SESSION_UNLOCKED,

    @SerialName("INTERACTIVE_LOGON")
    INTERACTIVE_LOGON,
    ;

    val label: String
        get() = when (this) {
            POINTER_ACTIVITY -> "Mouse activity"
            KEYBOARD_ACTIVITY -> "Keyboard activity"
            SESSION_UNLOCKED -> "Workstation unlocked"
            INTERACTIVE_LOGON -> "Someone signed in"
        }
}

/**
 * An incident, as a history row.
 *
 * Carries counts rather than the evidence itself: a list of forty incidents should not pull down
 * forty sets of camera images to render.
 */
@Serializable
data class SecuritySessionSummary(
    @SerialName("session_id") val sessionId: String = "",
    @SerialName("started_at_utc") val startedAtUtc: String = "",
    @SerialName("ended_at_utc") val endedAtUtc: String? = null,
    val trigger: IncidentTrigger = IncidentTrigger.POINTER_ACTIVITY,
    val resolution: SessionResolution = SessionResolution.ACTIVE,
    @SerialName("high_alert") val highAlert: Boolean = false,
    @SerialName("duration_seconds") val durationSeconds: Long = 0,
    @SerialName("timeline_count") val timelineCount: Int = 0,
    @SerialName("screenshot_count") val screenshotCount: Int = 0,
    @SerialName("camera_count") val cameraCount: Int = 0,
    @SerialName("audio_count") val audioCount: Int = 0,
) {
    val isOpen: Boolean get() = resolution != SessionResolution.ACKNOWLEDGED_OWNER

    val evidenceCount: Int get() = screenshotCount + cameraCount + audioCount

    /** "3m 12s". Short enough for a list row. */
    val duration: String
        get() = when {
            durationSeconds < 60 -> "${durationSeconds}s"
            durationSeconds < 3600 -> "${durationSeconds / 60}m ${durationSeconds % 60}s"
            else -> "${durationSeconds / 3600}h ${(durationSeconds % 3600) / 60}m"
        }
}

/**
 * One line of what happened.
 *
 * The PC writes these to be read months later, and deliberately never puts content in them — that
 * keyboard activity occurred, never what was typed.
 */
@Serializable
data class SecurityTimelineEntry(
    @SerialName("at_utc") val atUtc: String = "",
    val code: String = "",
    val message: String = "",
)

@Serializable
data class EvidenceSummary(
    @SerialName("evidence_id") val evidenceId: String = "",
    val kind: EvidenceKind = EvidenceKind.SCREENSHOT,
    @SerialName("created_at_utc") val createdAtUtc: String = "",
    @SerialName("content_type") val contentType: String = "",
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    @SerialName("duration_ms") val durationMs: Long = 0,
    val sha256: String = "",
    val note: String? = null,
) {
    val sizeLabel: String
        get() = if (sizeBytes < 1024 * 1024) "${sizeBytes / 1024} KB" else "%.1f MB".format(sizeBytes / 1048576.0)

    /** First eight characters of the digest — enough to compare by eye, short enough to show. */
    val shortDigest: String get() = sha256.take(8)
}

@Serializable
data class SessionDetail(
    val summary: SecuritySessionSummary = SecuritySessionSummary(),
    val timeline: List<SecurityTimelineEntry> = emptyList(),
    val evidence: List<EvidenceSummary> = emptyList(),
)

@Serializable
data class ListSessionsResult(
    val count: Int = 0,
    val sessions: List<SecuritySessionSummary> = emptyList(),
)

@Serializable
data class GetSessionResult(
    val session: SessionDetail? = null,
    @SerialName("has_active_session") val hasActiveSession: Boolean = false,
    val state: String = "",
) {
    val securityState: SecurityState? get() = SecurityState.fromWire(state)
}

/** Whether the bytes are actually on the PC. */
@Serializable
enum class EvidenceAvailability {
    @SerialName("AVAILABLE")
    AVAILABLE,

    /** Listed by the incident, but the file is gone — deleted, or past retention. */
    @SerialName("MISSING")
    MISSING,

    @SerialName("UNREADABLE")
    UNREADABLE,
}

/**
 * One chunk of one piece of evidence.
 *
 * Ranged, because a camera clip is megabytes: the phone asks for a window at a time and appends it
 * to a file rather than holding a whole recording as a base64 string, a byte array and a decoded
 * buffer at once.
 */
@Serializable
data class GetEvidenceResult(
    @SerialName("evidence_id") val evidenceId: String = "",
    val kind: EvidenceKind = EvidenceKind.SCREENSHOT,
    @SerialName("content_type") val contentType: String = "",
    val encoding: String = "base64",
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    val sha256: String = "",
    val availability: EvidenceAvailability = EvidenceAvailability.AVAILABLE,
    val offset: Long = 0,
    @SerialName("chunk_bytes") val chunkBytes: Int = 0,
    @SerialName("end_of_file") val endOfFile: Boolean = true,
    val data: String? = null,
    val unavailable: String? = null,
)

@Serializable
data class ResolveSessionResult(
    val resolved: Boolean = false,
    val session: SecuritySessionSummary? = null,
    val state: String = "",
) {
    val securityState: SecurityState? get() = SecurityState.fromWire(state)
}

@Serializable
data class SessionIdParams(@SerialName("session_id") val sessionId: String)

@Serializable
data class GetEvidenceParams(
    @SerialName("session_id") val sessionId: String,
    @SerialName("evidence_id") val evidenceId: String,
    val offset: Long = 0,
    @SerialName("max_bytes") val maxBytes: Int? = null,
)
