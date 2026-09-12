package dev.nexus.control

import dev.nexus.control.protocol.LiveSensorFrameHeader
import dev.nexus.control.protocol.StreamFrameHeader
import dev.nexus.control.remote.PlaybackWindow
import dev.nexus.control.remote.StreamClock
import dev.nexus.control.remote.VideoDecoder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The parts of the video path that are arithmetic rather than hardware.
 *
 * `MediaCodec` cannot run in a unit test, and whether this phone's decoder takes the stream is a
 * question for the phone. What can go wrong here without one is quieter and worse: a clock offset
 * with the sign flipped reports a latency that looks plausible and is wrong, parameter sets split at
 * the wrong byte configure a decoder that then produces nothing, and a keyframe flag read from the
 * wrong bit makes a phone wait forever for a picture it already has.
 */
class VideoPipelineTest {

    // --- The clock -------------------------------------------------------------------------------

    @Test
    fun `the offset is taken from the midpoint of the round trip`() {
        val clock = StreamClock()

        // Phone clock 10 000 ms ahead of the PC. Sent at 20 000, answered when the PC read 10 050,
        // received at 20 100: the PC's reading belongs to the phone's 20 050.
        assertTrue(clock.accept(sentAtMs = 20_000, pcUnixMs = 10_050, receivedAtMs = 20_100))

        assertEquals(10_000L, clock.offsetMs)
        assertEquals(100L, clock.roundTripMs)
    }

    @Test
    fun `a sample with a longer round trip does not replace a better one`() {
        val clock = StreamClock()

        clock.accept(sentAtMs = 0, pcUnixMs = 5, receivedAtMs = 10)
        assertFalse(clock.accept(sentAtMs = 100, pcUnixMs = 400, receivedAtMs = 500))

        assertEquals(10L, clock.roundTripMs)
        assertEquals(0L, clock.offsetMs)
    }

    @Test
    fun `a clock that stepped backwards mid exchange is ignored`() {
        val clock = StreamClock()

        assertFalse(clock.accept(sentAtMs = 1_000, pcUnixMs = 900, receivedAtMs = 990))
        assertNull(clock.offsetMs)
    }

    @Test
    fun `age is measured on the PC's clock, not the phone's`() {
        val clock = StreamClock()
        clock.accept(sentAtMs = 20_000, pcUnixMs = 10_050, receivedAtMs = 20_100)

        // Captured at PC time 10 900; phone reads 21 000, which is PC time 11 000.
        assertEquals(100L, clock.ageOf(captureUnixMs = 10_900, phoneNowMs = 21_000))
    }

    @Test
    fun `no offset means no latency figure rather than a guess`() {
        assertNull(StreamClock().ageOf(captureUnixMs = 1, phoneNowMs = 2))
    }

    // --- Parameter sets ---------------------------------------------------------------------------

    private val sps = byteArrayOf(0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x1F, 0x12)
    private val pps = byteArrayOf(0, 0, 0, 1, 0x68, 0xEE.toByte(), 0x3C, 0x80.toByte())
    private val idr = byteArrayOf(0, 0, 0, 1, 0x65, 0x11, 0x22, 0x33)

    @Test
    fun `SPS and PPS are split into separate buffers with their start codes`() {
        val split = VideoDecoder.splitParameterSets(sps + pps + idr)
        assertNotNull(split)
        val (first, second) = split!!

        assertArrayEquals(sps, first)
        assertArrayEquals(pps, second)
    }

    @Test
    fun `a keyframe without parameter sets yields none`() {
        assertNull(VideoDecoder.splitParameterSets(idr))
    }

    @Test
    fun `parameter sets are the same whatever start code length carried them`() {
        val short = byteArrayOf(0, 0, 1, 0x67, 0x4D, 0x00, 0x1F, 0x12, 0, 0, 1, 0x68, 0xEE.toByte(), 0x3C, 0x80.toByte())

        // Reported as different, this would rebuild the decoder at every keyframe.
        assertTrue(VideoDecoder.sameParameterSets(sps + pps, short))
        assertFalse(VideoDecoder.sameParameterSets(sps + pps, sps))
    }

    @Test
    fun `only parameter sets are taken out of a keyframe`() {
        assertArrayEquals(sps + pps, VideoDecoder.parameterSetsOf(sps + pps + idr))
    }

    @Test
    fun `NAL types are read from the byte after the start code`() {
        val types = mutableListOf<Int>()
        VideoDecoder.forEachNalUnit(sps + pps + idr) { type, _, _ -> types += type }

        assertEquals(listOf(7, 8, 5), types)
    }

    // --- Header flags -------------------------------------------------------------------------------

    private fun desktopHeader(encoding: Int, flags: Int): ByteArray {
        val buffer = ByteBuffer.allocate(StreamFrameHeader.SIZE).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put("NXS1".toByteArray())
        buffer.put(StreamFrameHeader.CURRENT_VERSION.toByte())
        buffer.put(encoding.toByte())
        buffer.put(flags.toByte())
        buffer.put(0)
        buffer.position(60)
        buffer.putInt(0)
        return buffer.array()
    }

    @Test
    fun `an H264 keyframe is recognised as one`() {
        val header = StreamFrameHeader.parse(
            desktopHeader(
                StreamFrameHeader.ENCODING_H264,
                StreamFrameHeader.FLAG_COMPLETE or StreamFrameHeader.FLAG_KEYFRAME,
            ),
        )!!

        assertTrue(header.isH264)
        assertTrue(header.isKeyframe)
        assertFalse(header.isCodecConfig)
    }

    @Test
    fun `a configuration frame is not a keyframe`() {
        val header = StreamFrameHeader.parse(
            desktopHeader(StreamFrameHeader.ENCODING_H264, StreamFrameHeader.FLAG_CODEC_CONFIG),
        )!!

        assertTrue(header.isCodecConfig)
        assertFalse(header.isKeyframe)
    }

    @Test
    fun `an encoding beyond H264 is still refused`() {
        assertNull(StreamFrameHeader.parse(desktopHeader(3, 0)))
    }

    @Test
    fun `a live camera frame reads its encoding and flags from the formerly reserved bytes`() {
        val message = ByteArray(LiveSensorFrameHeader.SIZE + 2)
        "NXL1".toByteArray().copyInto(message)
        message[4] = 1
        message[5] = 1
        message[6] = LiveSensorFrameHeader.ENCODING_H264.toByte()
        message[7] = (LiveSensorFrameHeader.FLAG_KEYFRAME).toByte()

        val header = LiveSensorFrameHeader.parse(message)!!

        assertTrue(header.isH264)
        assertTrue(header.isKeyframe)
        assertFalse(header.isCodecConfig)
    }

    @Test
    fun `an old camera frame with zero in those bytes is still a JPEG`() {
        val message = ByteArray(LiveSensorFrameHeader.SIZE + 2)
        "NXL1".toByteArray().copyInto(message)
        message[4] = 1
        message[5] = 1

        val header = LiveSensorFrameHeader.parse(message)!!

        assertFalse(header.isH264)
        assertEquals(LiveSensorFrameHeader.ENCODING_DEFAULT, header.encoding)
    }

    // --- What is reported to the PC ------------------------------------------------------------------

    @Test
    fun `a report covers only the pictures rendered since the previous one`() {
        val window = PlaybackWindow()

        window.onRendered(ageMs = 2_593, nowMs = 0)
        window.onRendered(ageMs = 2_400, nowMs = 500)
        window.take(nowMs = 1_000)

        window.onRendered(ageMs = 120, nowMs = 1_200)
        window.onRendered(ageMs = 140, nowMs = 1_700)
        val second = window.take(nowMs = 2_000)

        assertEquals(130.0, second.latencyMs!!, 0.001)
        assertEquals(2, second.rendered)
        assertEquals(2.0, second.renderedFps, 0.001)
    }

    @Test
    fun `a second with nothing rendered reports no latency rather than the last one`() {
        // The regression from the phone: a decoder waiting for a keyframe drew nothing, and the last
        // spike was reported again and again until the PC had stepped down twice on it.
        val window = PlaybackWindow()

        window.onRendered(ageMs = 2_593, nowMs = 0)
        window.take(nowMs = 1_000)

        val stalled = window.take(nowMs = 2_000)

        assertNull(stalled.latencyMs)
        assertEquals(0, stalled.rendered)
        assertEquals(0.0, stalled.renderedFps, 0.001)
    }

    @Test
    fun `pictures shown before the clock is synchronised count as rendered but not as latency`() {
        val window = PlaybackWindow()

        window.onRendered(ageMs = null, nowMs = 0)
        window.onRendered(ageMs = null, nowMs = 500)
        val sample = window.take(nowMs = 1_000)

        assertNull(sample.latencyMs)
        assertEquals(2, sample.rendered)
    }

    @Test
    fun `a codec that is still starting may hold a keyframe and what follows it`() {
        // Three was applied while the codec had not offered a single input buffer yet, so every
        // restart threw away the keyframe that started it. The limit only tightens once it is running.
        assertTrue(VideoDecoder.pendingLimit(codecAcceptingInput = false) >= 15)
        assertEquals(3, VideoDecoder.pendingLimit(codecAcceptingInput = true))
    }
}
