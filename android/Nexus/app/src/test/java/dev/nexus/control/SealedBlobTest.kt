package dev.nexus.control

import dev.nexus.control.data.SealedBlob
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * The framing around the sealed credential.
 *
 * The Keystore round trip needs a real device and lives in the instrumented tests. This covers the
 * part that is ordinary code and the part most likely to break silently: an IV recovered one byte
 * short decrypts to garbage rather than failing, which is exactly the kind of bug that only shows
 * up as "you have been unpaired" months later.
 */
class SealedBlobTest {

    @Test
    fun `a packed blob unpacks to the same iv and ciphertext`() {
        val iv = ByteArray(12) { it.toByte() }
        val ciphertext = ByteArray(120) { (it * 7).toByte() }

        val (recoveredIv, recoveredCiphertext) = SealedBlob.unpack(SealedBlob.pack(iv, ciphertext))!!

        assertArrayEquals(iv, recoveredIv)
        assertArrayEquals(ciphertext, recoveredCiphertext)
    }

    @Test
    fun `the header is version then iv length`() {
        val blob = SealedBlob.pack(ByteArray(12), ByteArray(4))

        assertEquals(SealedBlob.VERSION, blob[0].toInt())
        assertEquals(12, blob[1].toInt())
        assertEquals(2 + 12 + 4, blob.size)
    }

    @Test
    fun `an unfamiliar version is refused rather than misread`() {
        val blob = SealedBlob.pack(ByteArray(12), ByteArray(4))
        blob[0] = 9

        assertNull(SealedBlob.unpack(blob))
    }

    @Test
    fun `truncated and empty blobs are refused`() {
        val blob = SealedBlob.pack(ByteArray(12) { 1 }, ByteArray(16) { 2 })

        assertNull(SealedBlob.unpack(ByteArray(0)))
        assertNull(SealedBlob.unpack(byteArrayOf(1)))
        assertNull(SealedBlob.unpack(byteArrayOf(1, 12)))
        assertNull(SealedBlob.unpack(blob.copyOfRange(0, 13)))
    }

    @Test
    fun `a blob claiming no iv is refused`() {
        assertNull(SealedBlob.unpack(byteArrayOf(SealedBlob.VERSION.toByte(), 0, 1, 2, 3)))
    }

    @Test
    fun `an unusable iv length is rejected at pack time`() {
        assertThrows(IllegalArgumentException::class.java) {
            SealedBlob.pack(ByteArray(0), ByteArray(4))
        }
        assertThrows(IllegalArgumentException::class.java) {
            SealedBlob.pack(ByteArray(256), ByteArray(4))
        }
    }

    @Test
    fun `a 255-byte iv still round trips`() {
        // The boundary, because the length is stored in one byte and 255 must not read as -1.
        val iv = ByteArray(255) { it.toByte() }
        val (recovered, _) = SealedBlob.unpack(SealedBlob.pack(iv, ByteArray(8)))!!

        assertArrayEquals(iv, recovered)
    }
}
