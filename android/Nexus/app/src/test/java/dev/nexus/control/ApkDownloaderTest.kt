package dev.nexus.control

import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ProtocolError
import dev.nexus.control.update.AndroidPackageChunk
import dev.nexus.control.update.AndroidUpdateInfo
import dev.nexus.control.update.ApkDownload
import dev.nexus.control.update.ApkDownloader
import dev.nexus.control.update.PackageChunkSource
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import java.util.Base64
import kotlin.random.Random

/**
 * Fetching the update APK from the PC: in ranges, resumed after an interruption, and never handed on
 * unless the whole file hashes to what the PC's release recorded.
 */
class ApkDownloaderTest {

    private val directory: File = Files.createTempDirectory("nexus-update-test").toFile()
    private val apk = Random(42).nextBytes(2_500_000)
    private val info = AndroidUpdateInfo(
        versionName = "1.3.1",
        versionCode = 16,
        sizeBytes = apk.size.toLong(),
        sha256 = sha256(apk),
        signingCertSha256 = "a".repeat(64),
        updateAvailable = true,
    )

    @After
    fun tearDown() {
        directory.deleteRecursively()
    }

    /** A PC serving [bytes], which can be told to drop the link after some number of chunks. */
    private inner class FakePc(
        private val bytes: ByteArray = apk,
        private val versionCode: Int = 16,
        private val sha: String = info.sha256,
        var failAfterChunks: Int = Int.MAX_VALUE,
    ) : PackageChunkSource {
        val offsetsAsked = mutableListOf<Long>()

        override suspend fun chunk(versionCode: Int, offset: Long, maxBytes: Int): AndroidPackageChunk {
            if (offsetsAsked.size >= failAfterChunks) throw NotConnectedException()
            offsetsAsked += offset

            val end = minOf(bytes.size.toLong(), offset + maxBytes).toInt()
            val slice = bytes.copyOfRange(offset.toInt(), end)

            return AndroidPackageChunk(
                versionCode = this.versionCode,
                sizeBytes = bytes.size.toLong(),
                sha256 = sha,
                offset = offset,
                chunkBytes = slice.size,
                endOfFile = end >= bytes.size,
                data = Base64.getEncoder().encodeToString(slice),
            )
        }
    }

    @Test
    fun `the package arrives in ranges and only a matching hash is ready`() = runBlocking {
        val pc = FakePc()

        val result = ApkDownloader(directory, pc, chunkBytes = 1_000_000).download(info)

        assertTrue(result is ApkDownload.Ready)
        assertArrayEquals(apk, (result as ApkDownload.Ready).file.readBytes())
        assertEquals(listOf(0L, 1_000_000L, 2_000_000L), pc.offsetsAsked)
    }

    @Test
    fun `an interrupted download resumes where it stopped`() = runBlocking {
        val pc = FakePc(failAfterChunks = 1)
        val downloader = ApkDownloader(directory, pc, chunkBytes = 1_000_000)

        val first = downloader.download(info)

        assertTrue(first is ApkDownload.Failed)
        assertTrue((first as ApkDownload.Failed).resumable)

        // The link comes back.
        pc.failAfterChunks = Int.MAX_VALUE
        val second = downloader.download(info)

        assertTrue(second is ApkDownload.Ready)
        assertArrayEquals(apk, (second as ApkDownload.Ready).file.readBytes())

        // The second attempt did not start again from zero.
        assertEquals(listOf(0L, 1_000_000L, 2_000_000L), pc.offsetsAsked)
    }

    @Test
    fun `bytes that do not hash to the release record are discarded, not installed`() = runBlocking {
        val tampered = apk.copyOf().also { it[1234] = (it[1234].toInt() xor 0xFF).toByte() }

        val result = ApkDownloader(directory, FakePc(bytes = tampered), chunkBytes = 1_000_000).download(info)

        assertTrue(result is ApkDownload.Failed)
        assertFalse((result as ApkDownload.Failed).resumable)
        assertTrue(result.reason.contains("checksum"))
        assertEquals(0, directory.listFiles()!!.size)
    }

    @Test
    fun `a package that changes mid-download is never stitched together`() = runBlocking {
        val result = ApkDownloader(directory, FakePc(versionCode = 17), chunkBytes = 1_000_000).download(info)

        assertTrue(result is ApkDownload.Failed)
        assertFalse((result as ApkDownload.Failed).resumable)
        assertEquals(0, directory.listFiles()!!.size)
    }

    @Test
    fun `a build the PC no longer offers ends the download cleanly`() = runBlocking {
        val gone = PackageChunkSource { _, _, _ ->
            throw NexusRequestException(ProtocolError(NexusProtocol.Errors.NOT_FOUND, "no longer offered"))
        }

        val result = ApkDownloader(directory, gone).download(info)

        assertTrue(result is ApkDownload.Failed)
        assertFalse((result as ApkDownload.Failed).resumable)
    }

    @Test
    fun `an already downloaded package is not fetched twice`() = runBlocking {
        val pc = FakePc()
        val downloader = ApkDownloader(directory, pc, chunkBytes = 1_000_000)

        downloader.download(info)
        val asked = pc.offsetsAsked.size
        val again = downloader.download(info)

        assertTrue(again is ApkDownload.Ready)
        assertEquals(asked, pc.offsetsAsked.size)
    }

    private fun sha256(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
}
