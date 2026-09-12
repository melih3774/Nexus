package dev.nexus.control.remote

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/** What the decoder is doing, sampled rather than accumulated forever. */
data class DecodeStats(
    val rendered: Long = 0,
    val dropped: Long = 0,
    val decodeMs: Double = 0.0,
    val queued: Int = 0,

    /** When, on the PC's clock, the picture just put on screen was captured. */
    val renderedCaptureUnixMs: Long = 0,
)

/**
 * H.264 into a `Surface`, through the phone's own hardware decoder.
 *
 * **Why a Surface and not a Bitmap.** The old JPEG path decoded into a `Bitmap` and handed it to
 * Compose, which meant every frame crossed from the decoder into managed memory and then into a
 * texture — three megabytes a frame at 720p, thirty times a second, all of it garbage. A decoder
 * configured with a `Surface` writes straight into the buffer the compositor will show. Nothing is
 * copied and nothing is allocated per frame.
 *
 * **Why the asynchronous API.** A video decoder has two independent flows — buffers it wants filled
 * and pictures it has finished — and the synchronous API makes one thread poll both, where a wait on
 * either starves the other. The callback form lets the codec drive, which is also what makes
 * `releaseOutputBuffer(index, true)` the moment a picture is ready mean "show this now" rather than
 * "show this whenever the poll loop comes round".
 *
 * **Losing a frame is not like losing a JPEG.** Every picture but a keyframe is coded against the
 * one before it, so a gap does not cost one repaint — it corrupts everything drawn until the next
 * IDR. So this never silently skips: if it falls behind it discards everything it is holding, stops
 * feeding until a keyframe arrives, and asks for one through [onKeyframeNeeded]. A second of the
 * last good picture is better than ten seconds of smear.
 */
class VideoDecoder(
    private val onKeyframeNeeded: () -> Unit,
    private val onStats: (DecodeStats) -> Unit = {},
) {
    private val lock = Any()

    private var codec: MediaCodec? = null
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var surface: Surface? = null

    private var configuredWidth = 0
    private var configuredHeight = 0
    private var parameterSets: ByteArray? = null

    private val pending = ArrayDeque<AccessUnit>()
    private val freeInputs = ArrayDeque<Int>()
    private val submitted = LinkedHashMap<Long, Long>()

    private var awaitingKeyframe = true
    private var rendered = 0L
    private var dropped = 0L
    private var decodeMs = 0.0

    /** The surface to draw on, or null when the view has gone away. */
    fun attach(target: Surface?) {
        synchronized(lock) {
            if (surface === target) return

            surface = target
            teardownLocked()

            // A new surface means a new codec, and a new codec can only start on a keyframe.
            awaitingKeyframe = true
            onKeyframeNeeded()
        }
    }

    /**
     * Offers one access unit.
     *
     * [payload] is the whole received message; [offset] and [length] delimit the Annex-B bytes
     * inside it, so nothing is copied until the codec's own buffer is filled.
     */
    fun submit(
        payload: ByteArray,
        offset: Int,
        length: Int,
        width: Int,
        height: Int,
        captureUnixMs: Long,
        isKeyframe: Boolean,
        isCodecConfig: Boolean,
    ) {
        if (length <= 0) return

        synchronized(lock) {
            if (isCodecConfig) {
                // Parameter sets are not a picture. They are what the codec has to be configured
                // with before it can decode one, and they may arrive again if the PC changes the
                // stream's shape — in which case the codec has to be rebuilt around them.
                val sets = payload.copyOfRange(offset, offset + length)

                if (!sameParameterSets(sets, parameterSets) || width != configuredWidth || height != configuredHeight) {
                    parameterSets = sets
                    configuredWidth = width
                    configuredHeight = height
                    teardownLocked()
                    awaitingKeyframe = true
                }

                return
            }

            if (awaitingKeyframe && !isKeyframe) {
                // Feeding a predicted frame to a codec with no reference produces either an error or
                // a picture made of noise. Waiting costs at most one GOP, and one has been asked for.
                dropped++
                return
            }

            if (isKeyframe) {
                awaitingKeyframe = false

                // Every keyframe from the PC carries its own SPS and PPS. Taking them from here as
                // well as from the configuration frame means a phone that joined late, or missed that
                // one frame, can still start — rather than waiting for a configuration nobody resends.
                val unit = payload.copyOfRange(offset, offset + length)

                if (splitParameterSets(unit) != null) {
                    val sets = parameterSetsOf(unit)

                    if (!sameParameterSets(sets, parameterSets) || width != configuredWidth || height != configuredHeight) {
                        parameterSets = sets
                        configuredWidth = width
                        configuredHeight = height
                        teardownLocked()
                    }
                }
            }

            if (codec == null && !startLocked()) {
                return
            }

            if (pending.size >= MAX_PENDING) {
                // The decoder is behind. Throwing away part of the queue would break every frame
                // after it, so the whole queue goes and the stream restarts at the next keyframe.
                dropped += pending.size.toLong()
                pending.clear()
                submitted.clear()
                awaitingKeyframe = true
                onKeyframeNeeded()
                return
            }

            pending.addLast(
                AccessUnit(payload, offset, length, captureUnixMs, isKeyframe),
            )

            pumpLocked()
        }
    }

    /** Forgets everything in flight. Used when the stream stops or restarts. */
    fun reset() {
        synchronized(lock) {
            teardownLocked()
            awaitingKeyframe = true
            parameterSets = null
            configuredWidth = 0
            configuredHeight = 0
            rendered = 0
            dropped = 0
            decodeMs = 0.0
        }
    }

    fun release() {
        synchronized(lock) {
            teardownLocked()
            surface = null
        }
    }

    // --- Internals ---------------------------------------------------------------------------------

    private fun startLocked(): Boolean {
        val target = surface ?: return false
        val sets = parameterSets ?: return false

        if (configuredWidth <= 0 || configuredHeight <= 0) return false

        val (sps, pps) = splitParameterSets(sets) ?: return false

        return try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, configuredWidth, configuredHeight)
            format.setByteBuffer("csd-0", ByteBuffer.wrap(sps))
            format.setByteBuffer("csd-1", ByteBuffer.wrap(pps))

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                // Tells the decoder not to buffer pictures for reordering. The stream has no
                // B-frames, so there is nothing to reorder, and the default behaviour would hold a
                // picture back waiting for one that is never coming.
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }

            val worker = HandlerThread("nexus-video-decoder").apply { start() }
            val post = Handler(worker.looper)

            val created = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            created.setCallback(Callbacks(), post)
            created.configure(format, target, null, 0)
            created.start()

            thread = worker
            handler = post
            codec = created
            freeInputs.clear()
            submitted.clear()

            Log.i(TAG, "Decoder started at ${configuredWidth}x$configuredHeight")
            true
        } catch (e: Exception) {
            Log.w(TAG, "The video decoder could not start: ${e.javaClass.simpleName}: ${e.message}")
            teardownLocked()

            // The unit that prompted the start may be a predicted frame, and the next attempt must
            // not begin on one.
            awaitingKeyframe = true
            false
        }
    }

    private fun teardownLocked() {
        val existing = codec
        codec = null

        try {
            existing?.stop()
        } catch (e: IllegalStateException) {
            // Already stopped or never started; release still has to happen.
        }

        try {
            existing?.release()
        } catch (e: Exception) {
            Log.d(TAG, "Releasing the decoder failed: ${e.javaClass.simpleName}")
        }

        thread?.quitSafely()
        thread = null
        handler = null

        pending.clear()
        freeInputs.clear()
        submitted.clear()
    }

    private fun pumpLocked() {
        val active = codec ?: return

        while (pending.isNotEmpty() && freeInputs.isNotEmpty()) {
            val index = freeInputs.removeFirst()
            val unit = pending.removeFirst()

            try {
                val buffer = active.getInputBuffer(index) ?: continue
                buffer.clear()
                buffer.put(unit.payload, unit.offset, unit.length)

                // The presentation time carries the moment the pixels were captured on the PC, in
                // microseconds. It is never used for scheduling — every picture is shown as soon as
                // it is decoded — but it survives the codec, which makes it the only way to know how
                // old the picture on screen actually is.
                val pts = unit.captureUnixMs * 1000

                submitted[pts] = SystemClock.elapsedRealtime()

                while (submitted.size > MAX_TRACKED) {
                    submitted.remove(submitted.keys.first())
                }

                active.queueInputBuffer(index, 0, unit.length, pts, 0)
            } catch (e: IllegalStateException) {
                Log.d(TAG, "The decoder rejected a buffer; restarting at the next keyframe.")
                teardownLocked()
                awaitingKeyframe = true
                onKeyframeNeeded()
                return
            }
        }
    }

    private inner class Callbacks : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            synchronized(lock) {
                if (this@VideoDecoder.codec !== codec) return

                freeInputs.addLast(index)
                pumpLocked()
            }
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            var stats: DecodeStats? = null

            synchronized(lock) {
                if (this@VideoDecoder.codec !== codec) return

                val started = submitted.remove(info.presentationTimeUs)

                if (started != null) {
                    val elapsed = (SystemClock.elapsedRealtime() - started).toDouble()

                    // A rolling mean rather than a total: what matters is what the decoder is doing
                    // now, and an average over a ten-minute session cannot say.
                    decodeMs = if (rendered == 0L) elapsed else (decodeMs * 0.9) + (elapsed * 0.1)
                }

                rendered++
                stats = DecodeStats(rendered, dropped, decodeMs, pending.size, info.presentationTimeUs / 1000)
            }

            try {
                // true: render it, now. There is no presentation clock here on purpose — a remote
                // desktop wants the newest picture on screen as soon as it exists, not at the time
                // it would have been shown in a recording.
                codec.releaseOutputBuffer(index, true)
            } catch (e: IllegalStateException) {
                Log.d(TAG, "An output buffer could not be rendered.")
            }

            stats?.let(onStats)
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            Log.i(TAG, "Decoder output format: $format")
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            Log.w(TAG, "Decoder error (recoverable=${e.isRecoverable}, transient=${e.isTransient}): ${e.message}")

            synchronized(lock) {
                if (this@VideoDecoder.codec !== codec) return

                teardownLocked()
                awaitingKeyframe = true
            }

            onKeyframeNeeded()
        }
    }

    private data class AccessUnit(
        val payload: ByteArray,
        val offset: Int,
        val length: Int,
        val captureUnixMs: Long,
        val isKeyframe: Boolean,
    )

    companion object {
        private const val TAG = "NexusVideo"

        /**
         * Access units held while the decoder catches up.
         *
         * Small on purpose. Every unit waiting here is a picture that will be shown late, and a
         * remote desktop that is half a second behind the finger is worse than one that skipped to
         * the present. Three is enough to absorb a scheduling hiccup and not enough to hide a
         * decoder that genuinely cannot keep up.
         */
        private const val MAX_PENDING = 3

        private const val MAX_TRACKED = 32

        /**
         * Splits Annex-B parameter sets into the two buffers `MediaCodec` wants.
         *
         * `csd-0` must be the SPS and `csd-1` the PPS, each with its start code. Handing over both
         * concatenated in one buffer works on some decoders and silently produces nothing on others,
         * which is the kind of difference that only shows up on somebody else's phone.
         */
        fun splitParameterSets(annexB: ByteArray): Pair<ByteArray, ByteArray>? {
            var sps: ByteArray? = null
            var pps: ByteArray? = null

            forEachNalUnit(annexB) { type, offset, length ->
                when (type) {
                    7 -> if (sps == null) sps = annexB.copyOfRange(offset, offset + length)
                    8 -> if (pps == null) pps = annexB.copyOfRange(offset, offset + length)
                }
            }

            val foundSps = sps ?: return null
            val foundPps = pps ?: return null

            return foundSps to foundPps
        }

        /**
         * Whether two sets of parameter sets say the same thing.
         *
         * Compared without their start codes: the configuration frame and the copy inlined in each
         * keyframe can come from different places in the encoder and use three-byte and four-byte
         * start codes respectively. Comparing raw bytes would call them different, rebuild the
         * decoder at every keyframe, and put a visible hitch in the stream every two seconds.
         */
        fun sameParameterSets(a: ByteArray?, b: ByteArray?): Boolean {
            if (a == null || b == null) return a === b
            return bodies(a) == bodies(b)
        }

        private fun bodies(annexB: ByteArray): List<String> {
            val units = mutableListOf<String>()

            forEachNalUnit(annexB) { _, offset, length ->
                var start = offset
                while (start < offset + length && annexB[start].toInt() == 0) start++
                if (start < offset + length) start++ // the 0x01 of the start code
                units += annexB.copyOfRange(start, offset + length).joinToString("") { "%02x".format(it) }
            }

            return units
        }

        /** The SPS and PPS units of an access unit, concatenated in Annex-B form. */
        fun parameterSetsOf(annexB: ByteArray): ByteArray {
            val out = java.io.ByteArrayOutputStream()

            forEachNalUnit(annexB) { type, offset, length ->
                if (type == 7 || type == 8) out.write(annexB, offset, length)
            }

            return out.toByteArray()
        }

        /** Walks Annex-B NAL units, reporting each type and its extent including the start code. */
        fun forEachNalUnit(stream: ByteArray, visit: (type: Int, offset: Int, length: Int) -> Unit) {
            var from = 0

            while (true) {
                val start = nextStartCode(stream, from) ?: return
                val payload = start.first + start.second

                if (payload >= stream.size) return

                val next = nextStartCode(stream, payload)
                val end = next?.first ?: stream.size

                visit(stream[payload].toInt() and 0x1F, start.first, end - start.first)

                if (next == null) return
                from = next.first
            }
        }

        /** Returns the offset of the next start code and its length, or null. */
        private fun nextStartCode(stream: ByteArray, from: Int): Pair<Int, Int>? {
            var i = maxOf(0, from)

            while (i + 2 < stream.size) {
                if (stream[i].toInt() == 0 && stream[i + 1].toInt() == 0) {
                    if (stream[i + 2].toInt() == 1) return i to 3

                    if (i + 3 < stream.size && stream[i + 2].toInt() == 0 && stream[i + 3].toInt() == 1) {
                        return i to 4
                    }
                }

                i++
            }

            return null
        }
    }
}
