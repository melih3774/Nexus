package dev.nexus.control.protocol

/** Which sensor a live frame came from. Mirrors `PcCore.Protocol.LiveSensorKind`. */
enum class LiveSensorKind {
    CAMERA,
    MICROPHONE,
    ;

    /** The wire word the PC's `media.live.*` capabilities take. */
    val wire: String get() = name

    val label: String
        get() = when (this) {
            CAMERA -> "Camera"
            MICROPHONE -> "Microphone"
        }
}

/**
 * The header on every live camera or microphone frame, mirroring
 * `PcCore.Protocol.LiveSensorFrameHeader`.
 *
 * Deliberately a different magic from [StreamFrameHeader]: a desktop frame carries display bounds
 * and a cursor, a microphone frame carries a sample rate, and the two must never be read as each
 * other. A mistake here would not throw — it would decode a plausible-looking frame that means
 * something else.
 */
data class LiveSensorFrameHeader(
    val version: Int,
    val kind: LiveSensorKind,
    val sequence: Long,
    val timestampUnixMs: Long,

    /** Camera: pixel width. Microphone: samples per second. */
    val widthOrSampleRate: Int,

    /** Camera: pixel height. Microphone: channel count. */
    val heightOrChannels: Int,

    val payloadLength: Int,

    /**
     * How the payload is encoded: 0 keeps the original meaning (JPEG for the camera, PCM for the
     * microphone), 2 is H.264.
     */
    val encoding: Int = ENCODING_DEFAULT,

    /** Keyframe and codec-config markers, meaningful only when [encoding] is H.264. */
    val flags: Int = 0,
) {
    val isH264: Boolean get() = encoding == ENCODING_H264

    val isKeyframe: Boolean get() = flags and FLAG_KEYFRAME != 0

    val isCodecConfig: Boolean get() = flags and FLAG_CODEC_CONFIG != 0

    companion object {
        const val ENCODING_DEFAULT: Int = 0
        const val ENCODING_H264: Int = 2

        const val FLAG_KEYFRAME: Int = 4
        const val FLAG_CODEC_CONFIG: Int = 8

        const val SIZE: Int = 32
        const val CURRENT_VERSION: Int = 1

        /** A camera frame is tens of kilobytes and an audio quantum is smaller still. */
        const val MAX_PAYLOAD_BYTES: Int = 4 * 1024 * 1024

        private val MAGIC = byteArrayOf('N'.code.toByte(), 'X'.code.toByte(), 'L'.code.toByte(), '1'.code.toByte())

        fun parse(message: ByteArray): LiveSensorFrameHeader? {
            if (message.size < SIZE) return null

            for (i in MAGIC.indices) {
                if (message[i] != MAGIC[i]) return null
            }

            if ((message[4].toInt() and 0xFF) != CURRENT_VERSION) return null

            val kind = when (message[5].toInt() and 0xFF) {
                1 -> LiveSensorKind.CAMERA
                2 -> LiveSensorKind.MICROPHONE
                else -> return null
            }

            val payloadLength = message.size - SIZE
            if (payloadLength < 0 || payloadLength > MAX_PAYLOAD_BYTES) return null

            val encoding = message[6].toInt() and 0xFF
            if (encoding > ENCODING_H264) return null

            return LiveSensorFrameHeader(
                version = CURRENT_VERSION,
                kind = kind,
                sequence = int64(message, 8),
                timestampUnixMs = int64(message, 16),
                widthOrSampleRate = int32(message, 24),
                heightOrChannels = int16(message, 28),
                payloadLength = payloadLength,
                encoding = encoding,
                flags = message[7].toInt() and 0xFF,
            )
        }

        private fun int16(b: ByteArray, at: Int): Int =
            (b[at].toInt() and 0xFF) or ((b[at + 1].toInt() and 0xFF) shl 8)

        private fun int32(b: ByteArray, at: Int): Int =
            (b[at].toInt() and 0xFF) or
                ((b[at + 1].toInt() and 0xFF) shl 8) or
                ((b[at + 2].toInt() and 0xFF) shl 16) or
                ((b[at + 3].toInt() and 0xFF) shl 24)

        private fun int64(b: ByteArray, at: Int): Long {
            var value = 0L
            for (i in 7 downTo 0) {
                value = (value shl 8) or (b[at + i].toLong() and 0xFF)
            }
            return value
        }
    }
}

/** One live frame: its header, and the bytes behind it. */
class LiveSensorFrame(
    val header: LiveSensorFrameHeader,

    /** The whole received message. A video decoder reads the payload in place rather than copying it. */
    val message: ByteArray,
) {
    /** JPEG bytes for the camera, signed 16-bit little-endian PCM for the microphone. */
    fun payload(): ByteArray =
        message.copyOfRange(LiveSensorFrameHeader.SIZE, LiveSensorFrameHeader.SIZE + header.payloadLength)

    companion object {
        fun parse(message: ByteArray): LiveSensorFrame? {
            val header = LiveSensorFrameHeader.parse(message) ?: return null
            return LiveSensorFrame(header, message)
        }
    }
}
