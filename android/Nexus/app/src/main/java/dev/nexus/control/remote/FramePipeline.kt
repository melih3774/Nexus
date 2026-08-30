package dev.nexus.control.remote

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.SystemClock
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

/** One decoded desktop frame, ready to draw. A new instance per frame, so Compose recomposes. */
data class DecodedFrame(
    val image: ImageBitmap,
    val sequence: Long,
    val width: Int,
    val height: Int,
    val displayIndex: Int,
)

/** Local diagnostics. Never sent anywhere; shown behind a toggle on the remote screen. */
data class FrameStats(
    val received: Long = 0,
    val rendered: Long = 0,
    val dropped: Long = 0,
    val lastFrameBytes: Int = 0,
    val decodeMillis: Long = 0,
    val receiveFps: Double = 0.0,
) {
    val summary: String
        get() = "%.1f fps · %d KB · %d ms decode · %d dropped".format(
            receiveFps,
            lastFrameBytes / 1024,
            decodeMillis,
            dropped,
        )
}

/**
 * Decodes incoming frames off the UI thread, keeping only the newest.
 *
 * **Latest frame wins, again.** The PC already drops superseded frames before sending; this does the
 * same thing on arrival, because the phone can also be the slow part. A queue here would spend
 * memory to render a desktop that is already out of date — and on a long session the queue, not the
 * desktop, would be what the owner noticed.
 *
 * **Bounded allocation.** A 1280x720 bitmap is 3.7 MB, and decoding a fresh one fifteen times a
 * second is 55 MB/s of garbage. Instead a small pool is reused through `inBitmap`. Three buffers
 * rather than one: the frame being drawn must not be the frame being written, and by the time the
 * pool comes round again two newer frames have been published, so Compose has long since moved on.
 */
class FramePipeline(
    private val scope: CoroutineScope,
    private val dispatcher: CoroutineDispatcher = Dispatchers.Default,
) {
    private val incoming = Channel<StreamFrame>(
        capacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
        onUndeliveredElement = { dropped++ },
    )

    private val _frames = MutableStateFlow<DecodedFrame?>(null)
    val frames: StateFlow<DecodedFrame?> = _frames.asStateFlow()

    private val _stats = MutableStateFlow(FrameStats())
    val stats: StateFlow<FrameStats> = _stats.asStateFlow()

    private var job: Job? = null

    private val pool = arrayOfNulls<Bitmap>(POOL_SIZE)
    private var next = 0
    private var poolWidth = 0
    private var poolHeight = 0

    @Volatile
    private var dropped = 0L

    private var received = 0L
    private var rendered = 0L
    private var windowStart = 0L
    private var windowFrames = 0
    private var receiveFps = 0.0

    fun start() {
        if (job?.isActive == true) return

        job = scope.launch(dispatcher) {
            for (frame in incoming) {
                decode(frame)
            }
        }
    }

    /** Offers a frame. Never blocks and never fails: an older undecoded frame is discarded instead. */
    fun submit(frame: StreamFrame) {
        received++
        countRate()
        incoming.trySend(frame)
    }

    /** Ends decoding and releases the pool. Called when the stream stops or the screen closes. */
    fun stop() {
        job?.cancel()
        job = null

        _frames.value = null
        recycle()
    }

    private fun decode(frame: StreamFrame) {
        if (frame.header.encoding != StreamFrameHeader.ENCODING_JPEG) return

        val started = SystemClock.elapsedRealtime()

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

        rendered++

        _frames.value = DecodedFrame(
            image = bitmap.asImageBitmap(),
            sequence = frame.header.sequence,
            width = frame.header.width,
            height = frame.header.height,
            displayIndex = frame.header.displayIndex,
        )

        _stats.value = FrameStats(
            received = received,
            rendered = rendered,
            dropped = dropped,
            lastFrameBytes = frame.header.payloadLength,
            decodeMillis = SystemClock.elapsedRealtime() - started,
            receiveFps = receiveFps,
        )
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

    private fun countRate() {
        val now = SystemClock.elapsedRealtime()

        if (windowStart == 0L) {
            windowStart = now
            windowFrames = 0
        }

        windowFrames++

        val elapsed = now - windowStart
        if (elapsed >= RATE_WINDOW_MS) {
            receiveFps = windowFrames * 1000.0 / elapsed
            windowStart = now
            windowFrames = 0
        }
    }

    private companion object {
        const val POOL_SIZE = 3
        const val RATE_WINDOW_MS = 1000L
    }
}
