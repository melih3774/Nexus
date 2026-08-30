package dev.nexus.control

import dev.nexus.control.protocol.StreamFrame
import dev.nexus.control.protocol.StreamFrameHeader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The binary frame parser.
 *
 * These cover the malformed cases, which is where a parser reading straight off a socket earns its
 * keep. The agreement with PC Core about what a *well-formed* header looks like is pinned separately
 * by the shared vectors, because only a byte-for-byte comparison can catch a field written at the
 * wrong offset.
 */
class StreamFrameTest {

    private fun header(
        version: Int = StreamFrameHeader.CURRENT_VERSION,
        encoding: Int = StreamFrameHeader.ENCODING_JPEG,
        payloadLength: Int = 4,
    ): ByteArray {
        val buffer = ByteBuffer.allocate(StreamFrameHeader.SIZE).order(ByteOrder.LITTLE_ENDIAN)

        buffer.put('N'.code.toByte()).put('X'.code.toByte()).put('S'.code.toByte()).put('1'.code.toByte())
        buffer.put(version.toByte())
        buffer.put(encoding.toByte())
        buffer.put((StreamFrameHeader.FLAG_COMPLETE or StreamFrameHeader.FLAG_CURSOR_BAKED_IN).toByte())
        buffer.put(2)

        buffer.putInt(0x11223344)
        buffer.putLong(0x0102030405060708L)
        buffer.putLong(1_788_000_000_123L)
        buffer.putShort(1280)
        buffer.putShort(720)
        buffer.putInt(-1920)
        buffer.putInt(-120)
        buffer.putInt(1920)
        buffer.putInt(1080)
        buffer.putInt(640)
        buffer.putInt(360)
        buffer.put(1)
        buffer.put(0).put(0).put(0)
        buffer.putInt(payloadLength)

        return buffer.array()
    }

    private fun message(payloadLength: Int = 4): ByteArray =
        header(payloadLength = payloadLength) + ByteArray(payloadLength) { it.toByte() }

    @Test
    fun `a well formed header parses`() {
        val parsed = StreamFrameHeader.parse(header())

        assertNotNull(parsed)
        parsed!!

        assertEquals(1, parsed.version)
        assertEquals(StreamFrameHeader.ENCODING_JPEG, parsed.encoding)
        assertEquals(2, parsed.displayIndex)
        assertEquals(0x11223344L, parsed.streamId)
        assertEquals(0x0102030405060708L, parsed.sequence)
        assertEquals(1_788_000_000_123L, parsed.timestampUnixMs)
        assertEquals(1280, parsed.width)
        assertEquals(720, parsed.height)
        assertEquals(1920, parsed.desktopWidth)
        assertEquals(640, parsed.cursorX)
        assertTrue(parsed.cursorVisible)
        assertTrue(parsed.cursorBakedIn)
        assertTrue(parsed.isComplete)
    }

    @Test
    fun `a monitor left of the primary keeps its negative origin`() {
        // Dropping the sign would put every click meant for that screen on the wrong one.
        val parsed = StreamFrameHeader.parse(header())!!

        assertEquals(-1920, parsed.desktopLeft)
        assertEquals(-120, parsed.desktopTop)
    }

    @Test
    fun `a large stream id does not come back negative`() {
        // stream_id is unsigned on the wire and Kotlin has no unsigned Int in this position, so the
        // widening has to be explicit or ids past two billion arrive as negative numbers.
        val bytes = header()
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(8, -1)

        assertEquals(0xFFFFFFFFL, StreamFrameHeader.parse(bytes)!!.streamId)
    }

    @Test
    fun `a truncated header is refused`() {
        for (length in intArrayOf(0, 1, 32, StreamFrameHeader.SIZE - 1)) {
            assertNull("length $length", StreamFrameHeader.parse(ByteArray(length)))
        }
    }

    @Test
    fun `the wrong magic is refused`() {
        val bytes = header()
        bytes[1] = 'Y'.code.toByte()

        assertNull(StreamFrameHeader.parse(bytes))
    }

    @Test
    fun `a version this build does not know is refused`() {
        // Refused rather than parsed optimistically: a later version may move fields, and rendering
        // garbage is worse than saying the frame cannot be read.
        assertNull(StreamFrameHeader.parse(header(version = 2)))
        assertNull(StreamFrameHeader.parse(header(version = 0)))
    }

    @Test
    fun `an unknown encoding is refused`() {
        assertNull(StreamFrameHeader.parse(header(encoding = 99)))
    }

    @Test
    fun `an impossible payload length is refused`() {
        // The number a reader would allocate from. It must never reach an allocation.
        assertNull(StreamFrameHeader.parse(header(payloadLength = -1)))
        assertNull(StreamFrameHeader.parse(header(payloadLength = Int.MIN_VALUE)))
        assertNull(StreamFrameHeader.parse(header(payloadLength = StreamFrameHeader.MAX_PAYLOAD_BYTES + 1)))
    }

    @Test
    fun `a complete frame carries its payload`() {
        val frame = StreamFrame.parse(message(payloadLength = 4))

        assertNotNull(frame)
        assertEquals(StreamFrameHeader.SIZE, frame!!.payloadOffset)
        assertEquals(4, frame.header.payloadLength)
        assertEquals(StreamFrameHeader.SIZE + 4, frame.message.size)
    }

    @Test
    fun `a frame whose payload is shorter than the header claims is refused`() {
        // The shape that turns into an out-of-bounds read in the decoder if it is trusted.
        val truncated = header(payloadLength = 64) + ByteArray(10)

        assertNull(StreamFrame.parse(truncated))
    }

    @Test
    fun `a frame with trailing bytes beyond the stated payload is refused`() {
        val padded = header(payloadLength = 4) + ByteArray(40)

        assertNull(StreamFrame.parse(padded))
    }

    @Test
    fun `frames compare by content rather than array identity`() {
        val first = StreamFrame.parse(message())!!
        val second = StreamFrame.parse(message())!!

        assertEquals(first, second)
        assertEquals(first.hashCode(), second.hashCode())

        val different = StreamFrame.parse(message(payloadLength = 8))!!
        assertFalse(first == different)
    }
}
