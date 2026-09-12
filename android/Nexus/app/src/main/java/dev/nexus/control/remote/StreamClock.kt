package dev.nexus.control.remote

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.MediaTimeResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.coroutines.CancellationException

/**
 * The difference between the phone's clock and the PC's, measured rather than assumed.
 *
 * **Why this exists.** Every frame carries the moment its pixels were captured, in the PC's clock.
 * The obvious latency figure — phone time now minus that — is really the difference between two
 * clocks that were last synchronised who knows when: a phone on mobile data can be a second or more
 * out, which reports a latency that is negative, or ten times the truth. A figure like that is worse
 * than none, because it looks like a measurement.
 *
 * So the offset is measured the way NTP does it. The phone notes when it asked, the PC says what its
 * clock read when it answered, and the phone notes when the answer arrived. The PC's reading
 * corresponds to the midpoint of the round trip, and the error is bounded by half the round trip —
 * which is why, of several samples, the one with the shortest round trip is kept.
 *
 * Pure arithmetic, deliberately: the part that is easy to get subtly wrong has no Android in it.
 */
class StreamClock {

    /** Phone clock minus PC clock, in milliseconds. Null until a sample has been accepted. */
    @Volatile
    var offsetMs: Long? = null
        private set

    /** The round trip of the sample the offset came from, which bounds its error to half of it. */
    @Volatile
    var roundTripMs: Long? = null
        private set

    /**
     * Considers one exchange.
     *
     * @param sentAtMs phone clock when the request left.
     * @param pcUnixMs the PC's clock as it reported it.
     * @param receivedAtMs phone clock when the answer arrived.
     * @return true when this sample was better than the one held, and replaced it.
     */
    fun accept(sentAtMs: Long, pcUnixMs: Long, receivedAtMs: Long): Boolean {
        val roundTrip = receivedAtMs - sentAtMs

        // A negative round trip is a clock that stepped backwards mid-exchange. Nothing useful can
        // be learned from it.
        if (roundTrip < 0) return false

        val held = roundTripMs

        if (held != null && roundTrip >= held) return false

        offsetMs = (sentAtMs + roundTrip / 2) - pcUnixMs
        roundTripMs = roundTrip
        return true
    }

    /** Forgets the offset. A new connection may be a different route with a different delay. */
    fun reset() {
        offsetMs = null
        roundTripMs = null
    }

    /**
     * How long ago, on the PC's clock, something captured at [captureUnixMs] was captured.
     *
     * Null when no offset has been measured yet — a guess would be exactly the unreliable figure
     * this class exists to avoid.
     */
    fun ageOf(captureUnixMs: Long, phoneNowMs: Long = System.currentTimeMillis()): Long? {
        val offset = offsetMs ?: return null
        return (phoneNowMs - offset) - captureUnixMs
    }
}

/**
 * Measures the offset against the PC with a few quick round trips.
 *
 * Several rather than one, keeping the shortest, because the error in any one sample is half its
 * round trip and a single sample can be the one that waited behind a keyframe on the same socket.
 * A PC without `media.time` simply leaves latency unmeasured.
 */
suspend fun StreamClock.synchronise(channel: NexusCommandChannel, samples: Int = 5) {
    repeat(samples) {
        try {
            val sent = System.currentTimeMillis()
            val result = channel.request(NexusProtocol.Actions.MEDIA_TIME, timeoutMs = 3_000) ?: return
            val received = System.currentTimeMillis()
            val pc = NexusJson.decodeFromJsonElement(MediaTimeResult.serializer(), result)

            if (pc.unixMs > 0) accept(sent, pc.unixMs, received)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            return
        }
    }
}
