package dev.nexus.control.remote

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.SystemClock
import android.view.Surface
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import dev.nexus.control.protocol.StreamFrame
import dev.nexus.control.protocol.StreamFrameHeader
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * The shape of what is on screen, and — for the still-frame fallback only — the picture itself.
 *
 * With H.264 the picture never passes through here: the decoder draws it straight onto a surface.
 * What the rest of the screen still needs is its size and which display it is, because every touch
 * is mapped through them, so this is published whenever those change rather than once a frame.
 */
data class DecodedFrame(
    /** The decoded still, or null when the picture is on the video surface. */
    val image: ImageBitmap?,
    val sequence: Long,
    val width: Int,
    val height: Int,
    val displayIndex: Int,
)

/** What goes to the PC once a second: only what happened since the previous report. */
data class PlaybackReport(
    val latencyMs: Double?,
    val decodeMs: Double?,
    val renderedFps: Double,
    val dropped: Long,
)

/** Local diagnostics. Shown behind a toggle on the remote screen. */
data class FrameStats(
    val encoding: String = "",
    val width: Int = 0,
    val height: Int = 0,
    val receiveFps: Double = 0.0,
    val renderFps: Double = 0.0,
    val kbps: Double = 0.0,
    val latencyMs: Double = -1.0,
    val decodeMs: Double = 0.0,
    val dropped: Long = 0,
    val queued: Int = 0,
    val plan: String = "",
    val roundTripMs: Long? = null,
) {
    val summary: String
        get() = buildString {
            append("%.0f fps".format(renderFps))
            if (width > 0) append(" · ${height}p")
            append(" · %.1f Mbit/s".format(kbps / 1000))
            append(if (latencyMs >= 0) " · %.0f ms".format(latencyMs) else " · — ms")
            if (dropped > 0) append(" · $dropped dropped")
        }

    val detail: String
        get() = buildString {
            append("$encoding ${width}x$height")
            if (plan.isNotBlank()) append(" · $plan")
            append("\nreceived %.1f fps · rendered %.1f fps · %.2f Mbit/s".format(receiveFps, renderFps, kbps / 1000))
            append("\ncapture→render ")
            append(if (latencyMs >= 0) "%.0f ms".format(latencyMs) else "not measured")
            roundTripMs?.let { append(" (±${it / 2} ms)") }
            append(" · decode %.1f ms · queue $queued · dropped $dropped".format(decodeMs))
        }
}

/**
 * Routes incoming desktop frames to whichever decoder their encoding needs, and measures the result.
 *
 * **H.264 goes to the hardware decoder, in order, from the socket thread.** Nothing is copied,
 * nothing is queued here, and nothing is dropped here — a dropped predicted frame corrupts the
 * picture until the next keyframe, so the only component allowed to discard frames is the decoder,
 * which asks for a keyframe when it does. A gap in sequence numbers, which should never happen on a
 * reliable socket, is treated the same way rather than trusted to be harmless.
 *
 * **JPEG is the fallback, and keeps its old rule.** A PC without a video encoder still sends
 * independent stills, and for those latest-frame-wins is still correct: they are decoded off the UI
 * thread, newest only, into a small pool of reused bitmaps.
 *
 * **The latency figure is capture to screen.** The decoder reports the capture time of the picture
 * it has just put on the surface, and [StreamClock] turns that into an age on the PC's own clock.
 * No offset measured yet means no figure, rather than a guess.
 */
class FramePipeline(
    private val scope: CoroutineScope,
    private val clock: StreamClock,
    private val onKeyframeNeeded: () -> Unit,
    private val dispatcher: CoroutineDispatcher = Dispatchers.Default,
) {
    private val stills = Channel<StreamFrame>(
        capacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    private val _frames = MutableStateFlow<DecodedFrame?>(null)
    val frames: StateFlow<DecodedFrame?> = _frames.asStateFlow()

    private val _stats = MutableStateFlow(FrameStats())
    val stats: StateFlow<FrameStats> = _stats.asStateFlow()

    val decoder = VideoDecoder(
        onKeyframeNeeded = { requestKeyframe() },
        onStats = { onDecoded(it) },
    )

    private var job: Job? = null

    private val pool = arrayOfNulls<Bitmap>(POOL_SIZE)
    private var next = 0
    private var poolWidth = 0
    private var poolHeight = 0

    // Written on the socket thread only.
    private var lastStreamId = -1L
    private var lastSequence = -1L
    private var geometry: DecodedFrame? = null

    private val lock = Any()
    private var windowStart = 0L
    private var windowReceived = 0
    private var windowRendered = 0
    private var windowBytes = 0L
    private var receiveFps = 0.0
    private var renderFps = 0.0
    private var kbps = 0.0
    private var latencyMs = -1.0
    private var decodeMs = 0.0
    private var queued = 0
    private var dropped = 0L
    private var encoding = ""
    private val reportWindow = PlaybackWindow()

    @Volatile
    var plan: String = ""

    private var lastKeyframeRequest = 0L

    fun start() {
        if (job?.isActive == true) return

        job = scope.launch(dispatcher) {
            for (frame in stills) {
                decodeStill(frame)
            }
        }
    }

    /** The surface the video decoder draws onto, or null when the view has gone. */
    fun attachSurface(surface: Surface?) = decoder.attach(surface)

    /** Offers a frame. Called on the socket thread; never blocks. */
    fun submit(frame: StreamFrame) {
        val header = frame.header

        if (header.streamId != lastStreamId) {
            lastStreamId = header.streamId
            lastSequence = -1
        } else if (lastSequence >= 0 && header.sequence != lastSequence + 1 && header.isH264) {
            // The PC numbers every frame it sends and the socket is reliable, so a gap means a frame
            // was lost somewhere between them. Whatever the cause, the pictures after it are coded
            // against one that never arrived.
            requestKeyframe()
        }

        lastSequence = header.sequence

        synchronized(lock) {
            windowReceived++
            windowBytes += header.payloadLength
            encoding = if (header.isH264) "H.264" else "JPEG"
            roll()
        }

        if (!header.isH264) {
            stills.trySend(frame)
            return
        }

        publishGeometry(header)

        decoder.submit(
            payload = frame.message,
            offset = frame.payloadOffset,
            length = header.payloadLength,
            width = header.width,
            height = header.height,
            captureUnixMs = header.timestampUnixMs,
            isKeyframe = header.isKeyframe,
            isCodecConfig = header.isCodecConfig,
        )
    }

    /** Ends decoding and releases the pool. Called when the stream stops or the screen closes. */
    fun stop() {
        job?.cancel()
        job = null

        decoder.reset()

        _frames.value = null
        geometry = null
        lastStreamId = -1
        lastSequence = -1
        recycle()

        synchronized(lock) {
            windowStart = 0
            windowReceived = 0
            windowRendered = 0
            windowBytes = 0
            latencyMs = -1.0
            reportWindow.reset()
        }
    }

    /**
     * What to tell the PC, covering only the pictures rendered since the last call.
     *
     * Also refreshes the overlay's rates, which otherwise only move when a frame arrives — so a
     * stream that has stopped delivering shows zero rather than the last second it was working.
     */
    fun takeReport(): PlaybackReport = synchronized(lock) {
        val sample = reportWindow.take(SystemClock.elapsedRealtime())
        roll()

        PlaybackReport(
            latencyMs = sample.latencyMs,
            decodeMs = decodeMs.takeIf { encoding == "H.264" && sample.rendered > 0 },
            renderedFps = sample.renderedFps,
            dropped = dropped,
        )
    }

    fun release() {
        stop()
        decoder.release()
    }

    private fun publishGeometry(header: StreamFrameHeader) {
        val current = geometry

        if (current != null &&
            current.image == null &&
            current.width == header.width &&
            current.height == header.height &&
            current.displayIndex == header.displayIndex
        ) {
            return
        }

        val updated = DecodedFrame(null, header.sequence, header.width, header.height, header.displayIndex)
        geometry = updated
        _frames.value = updated
    }

    private fun onDecoded(decoded: DecodeStats) {
        val age = clock.ageOf(decoded.renderedCaptureUnixMs)

        synchronized(lock) {
            windowRendered++
            reportWindow.onRendered(age, SystemClock.elapsedRealtime())
            decodeMs = decoded.decodeMs
            queued = decoded.queued
            dropped = decoded.dropped

            if (age != null && age >= 0) {
                // Smoothed, because single frames are noisy — a keyframe takes longer to send and
                // longer to decode — and the figure is for judging the stream rather than one frame.
                latencyMs = if (latencyMs < 0) age.toDouble() else (latencyMs * 0.8) + (age * 0.2)
            }

            roll()
        }
    }

    /** Recomputes rates once a second. Called with [lock] held. */
    private fun roll() {
        val now = SystemClock.elapsedRealtime()

        if (windowStart == 0L) {
            windowStart = now
            return
        }

        val elapsed = now - windowStart

        if (elapsed < RATE_WINDOW_MS) return

        receiveFps = windowReceived * 1000.0 / elapsed
        renderFps = windowRendered * 1000.0 / elapsed
        kbps = windowBytes * 8.0 / elapsed
        windowStart = now
        windowReceived = 0
        windowRendered = 0
        windowBytes = 0

        val shape = geometry

        _stats.value = FrameStats(
            encoding = encoding,
            width = shape?.width ?: 0,
            height = shape?.height ?: 0,
            receiveFps = receiveFps,
            renderFps = renderFps,
            kbps = kbps,
            latencyMs = latencyMs,
            decodeMs = decodeMs,
            dropped = dropped,
            queued = queued,
            plan = plan,
            roundTripMs = clock.roundTripMs,
        )
    }

    /** Asks the PC for a keyframe, at most a couple of times a second however often it is needed. */
    private fun requestKeyframe() {
        val now = SystemClock.elapsedRealtime()

        synchronized(lock) {
            if (now - lastKeyframeRequest < KEYFRAME_REQUEST_INTERVAL_MS) return
            lastKeyframeRequest = now
        }

        onKeyframeNeeded()
    }

    // --- The still-frame fallback --------------------------------------------------------------------

    private fun decodeStill(frame: StreamFrame) {
        val bitmap = try {
            decodeJpeg(frame)
        } catch (e: OutOfMemoryError) {
            // Never fatal. Drop the pool, skip this frame, and the next one starts fresh.
            recycle()
            return
        } catch (e: IllegalArgumentException) {
            // A corrupt or truncated payload. One dropped repaint, nothing more.
            return
        } ?: return

        val decoded = DecodedFrame(
            image = bitmap.asImageBitmap(),
            sequence = frame.header.sequence,
            width = frame.header.width,
            height = frame.header.height,
            displayIndex = frame.header.displayIndex,
        )

        geometry = decoded
        _frames.value = decoded

        synchronized(lock) {
            windowRendered++

            val stillAge = clock.ageOf(frame.header.timestampUnixMs)
            reportWindow.onRendered(stillAge, SystemClock.elapsedRealtime())

            stillAge?.takeIf { it >= 0 }?.let { age ->
                latencyMs = if (latencyMs < 0) age.toDouble() else (latencyMs * 0.8) + (age * 0.2)
            }
            roll()
        }
    }

    private fun decodeJpeg(frame: StreamFrame): Bitmap? {
        val options = BitmapFactory.Options().apply {
            inMutable = true
            inPreferredConfig = Bitmap.Config.ARGB_8888
        }

        // The pool is only valid while every frame is the same size. A resolution change on the PC
        // invalidates it, and reusing a mismatched buffer would fail the decode outright.
        if (poolWidth != frame.header.width || poolHeight != frame.header.height) {
            recycle()
            poolWidth = frame.header.width
            poolHeight = frame.header.height
        } else {
            options.inBitmap = pool[next]
        }

        val decoded = try {
            BitmapFactory.decodeByteArray(frame.message, frame.payloadOffset, frame.header.payloadLength, options)
        } catch (e: IllegalArgumentException) {
            // Thrown when inBitmap cannot be reused. Retry once without it rather than losing the
            // stream over an optimisation.
            options.inBitmap = null
            BitmapFactory.decodeByteArray(frame.message, frame.payloadOffset, frame.header.payloadLength, options)
        } ?: return null

        pool[next] = decoded
        next = (next + 1) % POOL_SIZE

        return decoded
    }

    private fun recycle() {
        for (i in pool.indices) {
            pool[i] = null
        }

        next = 0
        poolWidth = 0
        poolHeight = 0
    }

    private companion object {
        const val POOL_SIZE = 3
        const val RATE_WINDOW_MS = 1000L
        const val KEYFRAME_REQUEST_INTERVAL_MS = 500L
    }
}
