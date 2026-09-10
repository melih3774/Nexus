package dev.nexus.control

import dev.nexus.control.protocol.EvidenceKind
import dev.nexus.control.protocol.LiveSensorFrame
import dev.nexus.control.protocol.LiveSensorFrameHeader
import dev.nexus.control.protocol.LiveSensorKind
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The live sensor wire format, from the phone's side.
 *
 * A header that parses wrongly does not throw — it produces a frame that looks fine and means
 * something else, which is why these assert byte offsets rather than a round trip through code that
 * could be wrong in both directions at once.
 */
class LiveSensorFrameTest {

    /** Builds a frame the way `LiveSensorFrameHeader.ToMessage` does on the PC. */
    private fun frame(
        kind: Int,
        sequence: Long = 1,
        timestamp: Long = 0,
        widthOrRate: Int = 640,
        heightOrChannels: Int = 480,
        payload: ByteArray = ByteArray(0),
        version: Int = 1,
        magic: String = "NXL1",
    ): ByteArray {
        val message = ByteArray(LiveSensorFrameHeader.SIZE + payload.size)

        for (i in 0 until 4) message[i] = magic[i].code.toByte()

        message[4] = version.toByte()
        message[5] = kind.toByte()

        for (i in 0 until 8) message[8 + i] = ((sequence shr (i * 8)) and 0xFF).toByte()
        for (i in 0 until 8) message[16 + i] = ((timestamp shr (i * 8)) and 0xFF).toByte()
        for (i in 0 until 4) message[24 + i] = ((widthOrRate shr (i * 8)) and 0xFF).toByte()
        for (i in 0 until 2) message[28 + i] = ((heightOrChannels shr (i * 8)) and 0xFF).toByte()

        payload.copyInto(message, LiveSensorFrameHeader.SIZE)
        return message
    }

    @Test
    fun `a camera frame parses with its payload intact`() {
        val payload = byteArrayOf(1, 2, 3, 4, 5, 6)
        val parsed = LiveSensorFrame.parse(frame(kind = 1, payload = payload, sequence = 7))

        assertNotNull(parsed)
        assertEquals(LiveSensorKind.CAMERA, parsed!!.header.kind)
        assertEquals(7L, parsed.header.sequence)
        assertEquals(640, parsed.header.widthOrSampleRate)
        assertEquals(480, parsed.header.heightOrChannels)
        assertEquals(payload.size, parsed.header.payloadLength)
        assertArrayEquals(payload, parsed.payload())
    }

    @Test
    fun `a microphone frame carries a sample rate and a channel count`() {
        val parsed = LiveSensorFrame.parse(
            frame(kind = 2, widthOrRate = 48_000, heightOrChannels = 1, payload = ByteArray(64)),
        )

        assertNotNull(parsed)
        assertEquals(LiveSensorKind.MICROPHONE, parsed!!.header.kind)
        assertEquals(48_000, parsed.header.widthOrSampleRate)
        assertEquals(1, parsed.header.heightOrChannels)
        assertEquals(64, parsed.header.payloadLength)
    }

    /**
     * The check that keeps the two binary formats apart. A desktop frame reaching the live decoder
     * would be a picture of the screen shown as a picture of the room.
     */
    @Test
    fun `a desktop frame is not accepted as a live frame`() {
        assertNull(LiveSensorFrame.parse(frame(kind = 1, magic = "NXS1")))
    }

    @Test
    fun `an unknown version is refused`() {
        assertNull(LiveSensorFrame.parse(frame(kind = 1, version = 99)))
    }

    @Test
    fun `an unknown sensor is refused`() {
        assertNull(LiveSensorFrame.parse(frame(kind = 7)))
    }

    @Test
    fun `a truncated message is refused rather than read past its end`() {
        assertNull(LiveSensorFrame.parse(ByteArray(LiveSensorFrameHeader.SIZE - 1)))
        assertNull(LiveSensorFrame.parse(ByteArray(0)))
    }

    @Test
    fun `the wire words match what the PC accepts`() {
        assertEquals("CAMERA", LiveSensorKind.CAMERA.wire)
        assertEquals("MICROPHONE", LiveSensorKind.MICROPHONE.wire)
    }
}

/**
 * Which evidence kinds the phone can now show.
 *
 * These used to be split into "an image" and "everything else, which says playback is not available
 * in this version". The classification is what decides whether the owner sees their evidence or a
 * sentence apologising for it, so it is worth pinning.
 */
class EvidenceKindTest {

    @Test
    fun `stills are images and clips are playable`() {
        assertTrue(EvidenceKind.SCREENSHOT.isViewableImage)
        assertTrue(EvidenceKind.CAMERA_IMAGE.isViewableImage)

        assertFalse(EvidenceKind.CAMERA_IMAGE.isPlayable)
        assertFalse(EvidenceKind.SCREENSHOT.isPlayable)

        assertTrue(EvidenceKind.CAMERA_VIDEO.isPlayable)
        assertTrue(EvidenceKind.CAMERA_VIDEO.isVideo)

        assertTrue(EvidenceKind.AUDIO.isPlayable)
        assertTrue(EvidenceKind.AUDIO.isAudio)
        assertFalse(EvidenceKind.AUDIO.isVideo)

        assertTrue(EvidenceKind.SCREEN_RECORDING.isPlayable)
        assertTrue(EvidenceKind.SCREEN_RECORDING.isVideo)
    }

    /** Every kind is either shown as a picture or played. None falls through to nothing. */
    @Test
    fun `no evidence kind is left with no way to display it`() {
        for (kind in EvidenceKind.entries) {
            assertTrue(
                "${kind.name} has no display path",
                kind.isViewableImage || kind.isPlayable,
            )
        }
    }
}
