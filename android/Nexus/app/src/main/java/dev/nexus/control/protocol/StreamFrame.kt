package dev.nexus.control.protocol

/**
 * The header on every live desktop frame, mirroring `PcCore.Protocol.StreamFrameHeader`.
 *
 * Frames travel as binary WebSocket messages on the same authenticated connection that carries
 * commands and events. That is the whole reason this class exists rather than another `@Serializable`
 * data class: a 70 KB JPEG becomes 90 KB of base64 inside a JSON string, fifteen times a second, and
 * both sides pay for the escaping and the parsing.
 *
 * The layout is fixed, little-endian, and pinned by `shared/protocol/protocol-vectors.json`. A
 * mistake here does not produce an exception — it produces a frame that parses perfectly and means
 * something else — which is why the vectors assert byte offsets rather than a round trip.
 */
data class StreamFrameHeader(
    val version: Int,
    val encoding: Int,
    val flags: Int,
    val displayIndex: Int,
    val streamId: Long,
    val sequence: Long,
    val timestampUnixMs: Long,
    val width: Int,
    val height: Int,
    val desktopLeft: Int,
    val desktopTop: Int,
    val desktopWidth: Int,
    val desktopHeight: Int,
    val cursorX: Int,
    val cursorY: Int,
    val cursorVisible: Boolean,
    val payloadLength: Int,
) {
    /** The pointer is already drawn into the image; drawing another would show two. */
    val cursorBakedIn: Boolean get() = flags and FLAG_CURSOR_BAKED_IN != 0

    val isComplete: Boolean get() = flags and FLAG_COMPLETE != 0

    companion object {
        const val SIZE: Int = 64
        const val CURRENT_VERSION: Int = 1

        const val ENCODING_JPEG: Int = 1

        const val FLAG_CURSOR_BAKED_IN: Int = 1
        const val FLAG_COMPLETE: Int = 2

        /** Matches the PC's bound. A larger stated length is a bug or a hostile peer. */
        const val MAX_PAYLOAD_BYTES: Int = 16 * 1024 * 1024

        private val MAGIC = byteArrayOf('N'.code.toByte(), 'X'.code.toByte(), 'S'.code.toByte(), '1'.code.toByte())

        /**
         * Parses a header, or returns null.
         *
         * Null rather than an exception for everything malformed: these are bytes off a network, and
         * a bad frame is an ordinary event that should cost one dropped repaint, not a crash.
         */
        fun parse(message: ByteArray): StreamFrameHeader? {
            if (message.size < SIZE) return null

            for (i in MAGIC.indices) {
                if (message[i] != MAGIC[i]) return null
            }

            val version = message[4].toInt() and 0xFF
            if (version != CURRENT_VERSION) return null

            val encoding = message[5].toInt() and 0xFF
            if (encoding > ENCODING_JPEG) return null

            val payloadLength = int32(message, 60)
            if (payloadLength < 0 || payloadLength > MAX_PAYLOAD_BYTES) return null

            return StreamFrameHeader(
                version = version,
                encoding = encoding,
                flags = message[6].toInt() and 0xFF,
                displayIndex = message[7].toInt() and 0xFF,
                streamId = uint32(message, 8),
                sequence = int64(message, 12),
                timestampUnixMs = int64(message, 20),
                width = uint16(message, 28),
                height = uint16(message, 30),
                desktopLeft = int32(message, 32),
                desktopTop = int32(message, 36),
                desktopWidth = int32(message, 40),
                desktopHeight = int32(message, 44),
                cursorX = int32(message, 48),
                cursorY = int32(message, 52),
                cursorVisible = message[56].toInt() != 0,
                payloadLength = payloadLength,
            )
        }

        private fun uint16(bytes: ByteArray, offset: Int): Int =
            (bytes[offset].toInt() and 0xFF) or ((bytes[offset + 1].toInt() and 0xFF) shl 8)

        private fun int32(bytes: ByteArray, offset: Int): Int =
            (bytes[offset].toInt() and 0xFF) or
                ((bytes[offset + 1].toInt() and 0xFF) shl 8) or
                ((bytes[offset + 2].toInt() and 0xFF) shl 16) or
                ((bytes[offset + 3].toInt() and 0xFF) shl 24)

        private fun uint32(bytes: ByteArray, offset: Int): Long = int32(bytes, offset).toLong() and 0xFFFFFFFFL

        private fun int64(bytes: ByteArray, offset: Int): Long {
            var value = 0L
            for (i in 7 downTo 0) {
                value = (value shl 8) or (bytes[offset + i].toLong() and 0xFF)
            }
            return value
        }
    }
}

/**
 * A complete frame: its header, and where its payload sits inside the received message.
 *
 * The payload is referenced by offset rather than copied, because the copy would be a second
 * allocation of a JPEG that is about to be decoded and thrown away.
 */
data class StreamFrame(
    val header: StreamFrameHeader,
    val message: ByteArray,
) {
    val payloadOffset: Int get() = StreamFrameHeader.SIZE

    companion object {
        /** Returns null when the message is not a well-formed frame, including a truncated payload. */
        fun parse(message: ByteArray): StreamFrame? {
            val header = StreamFrameHeader.parse(message) ?: return null

            // The stated length has to match what actually arrived. A header promising more than
            // the message holds is exactly the shape that turns into an out-of-bounds read.
            if (message.size != StreamFrameHeader.SIZE + header.payloadLength) return null

            return StreamFrame(header, message)
        }
    }

    // ByteArray identity: data-class equality would compare references and mislead in tests.
    override fun equals(other: Any?): Boolean =
        this === other || (other is StreamFrame && header == other.header && message.contentEquals(other.message))

    override fun hashCode(): Int = 31 * header.hashCode() + message.contentHashCode()
}
