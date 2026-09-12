package dev.nexus.control.remote

/** What the phone saw of a stream between two reports to the PC. */
data class PlaybackSample(
    /** Mean capture-to-render age of the pictures shown in the window, or null if none were measured. */
    val latencyMs: Double?,
    val renderedFps: Double,
    val rendered: Int,
)

/**
 * Collects what was actually rendered between two reports, and forgets it once reported.
 *
 * **Why not the smoothed figure the overlay shows.** That one only moves when a picture is drawn. A
 * decoder waiting for a keyframe draws nothing, so the smoothed latency sat on its last value — the
 * spike from the stall itself — and was sent to the PC again every second. The PC read each copy as
 * fresh evidence and stepped the stream down twice on one measurement. A report built from this
 * window says only what happened since the last one: no pictures means no latency, which the PC
 * treats as "unknown" rather than as strain.
 *
 * Not thread-safe; the caller holds its own lock.
 */
class PlaybackWindow {
    private var started = -1L
    private var rendered = 0
    private var latencySum = 0.0
    private var latencyCount = 0

    /** One picture put on screen, with its age on the PC's clock if that is known. */
    fun onRendered(ageMs: Long?, nowMs: Long) {
        if (started < 0) started = nowMs

        rendered++

        if (ageMs != null && ageMs >= 0) {
            latencySum += ageMs
            latencyCount++
        }
    }

    /** Everything since the last call, and starts a new window. */
    fun take(nowMs: Long): PlaybackSample {
        val elapsed = if (started < 0) 0L else nowMs - started

        val sample = PlaybackSample(
            latencyMs = if (latencyCount > 0) latencySum / latencyCount else null,
            renderedFps = if (elapsed > 0) rendered * 1000.0 / elapsed else 0.0,
            rendered = rendered,
        )

        started = nowMs
        rendered = 0
        latencySum = 0.0
        latencyCount = 0

        return sample
    }

    fun reset() {
        started = -1L
        rendered = 0
        latencySum = 0.0
        latencyCount = 0
    }
}
