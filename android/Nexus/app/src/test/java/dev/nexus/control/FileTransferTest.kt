package dev.nexus.control

import dev.nexus.control.files.FileChunk
import dev.nexus.control.files.FileDownloadSink
import dev.nexus.control.files.FileDownloader
import dev.nexus.control.files.FileHash
import dev.nexus.control.files.FileIdentity
import dev.nexus.control.files.FileUploader
import dev.nexus.control.files.FinishedUpload
import dev.nexus.control.files.RemoteFileSource
import dev.nexus.control.files.RemoteUploadTarget
import dev.nexus.control.files.TransferOutcome
import dev.nexus.control.files.UploadChunkResult
import dev.nexus.control.files.UploadSource
import dev.nexus.control.files.UploadState
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ProtocolError
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.nio.file.Files
import java.security.MessageDigest
import java.util.Base64
import kotlin.random.Random

/**
 * Files moving between the phone and the PC: in chunks, resumed after the link drops, verified
 * end to end, and cancelled cleanly - against a scripted PC rather than a real one.
 */
class FileTransferTest {

    private val directory: File = Files.createTempDirectory("nexus-files-test").toFile()
    private val content = Random(7).nextBytes(2_500_000)
    private val path = """C:\Users\Owner\video.mp4"""

    @After
    fun tearDown() {
        directory.deleteRecursively()
    }

    /** A PC holding one file, which can drop the link after some chunks or change the file. */
    private inner class FakePc(
        var bytes: ByteArray = content,
        var modified: String = "2026-09-13T10:00:00+00:00",
        var failAfterChunks: Int = Int.MAX_VALUE,
        var dropAsCancellation: Boolean = false,
    ) : RemoteFileSource, RemoteUploadTarget {
        val offsetsRead = mutableListOf<Long>()
        val chunksSent = mutableListOf<Long>()
        val cancelled = mutableListOf<String>()
        var hashRequests = 0

        // Upload side.
        var received = java.io.ByteArrayOutputStream()
        var existing: String? = null
        var uploadId = "u1"
        var active = false

        private fun maybeDrop(count: Int) {
            if (count >= failAfterChunks) {
                if (dropAsCancellation) throw CancellationException("connection ended")
                throw NotConnectedException()
            }
        }

        override suspend fun read(path: String, offset: Long, maxBytes: Int, expectedSize: Long?, expectedModifiedUtc: String?): FileChunk {
            maybeDrop(offsetsRead.size)

            if ((expectedSize != null && expectedSize != bytes.size.toLong()) || (expectedModifiedUtc != null && expectedModifiedUtc != modified)) {
                throw NexusRequestException(ProtocolError(NexusProtocol.Errors.FILE_CHANGED, "changed"))
            }

            offsetsRead += offset
            val end = minOf(bytes.size.toLong(), offset + maxBytes).toInt()
            val slice = bytes.copyOfRange(offset.toInt(), end)

            return FileChunk(path, bytes.size.toLong(), modified, offset, slice.size, end >= bytes.size, Base64.getEncoder().encodeToString(slice))
        }

        override suspend fun hash(path: String, expectedSize: Long?, expectedModifiedUtc: String?): FileHash {
            hashRequests++
            return FileHash(path, bytes.size.toLong(), modified, sha256(bytes))
        }

        override suspend fun begin(directory: String, name: String, sizeBytes: Long, overwrite: Boolean): UploadState {
            if (existing == name && !overwrite) throw NexusRequestException(ProtocolError(NexusProtocol.Errors.ALREADY_EXISTS, "exists"))
            if (!active) received = java.io.ByteArrayOutputStream()
            active = true
            return UploadState(uploadId, "$directory\\$name", sizeBytes, received.size().toLong(), 1_000_000, resumed = received.size() > 0)
        }

        override suspend fun chunk(uploadId: String, offset: Long, data: String): UploadChunkResult {
            maybeDrop(chunksSent.size)
            assertEquals("chunks must arrive in order", received.size().toLong(), offset)
            chunksSent += offset
            received.write(Base64.getDecoder().decode(data))
            return UploadChunkResult(uploadId, received.size().toLong())
        }

        override suspend fun finish(uploadId: String, sha256: String): FinishedUpload {
            val bytes = received.toByteArray()
            if (sha256 != sha256(bytes)) throw NexusRequestException(ProtocolError(NexusProtocol.Errors.CHECKSUM_MISMATCH, "mismatch"))
            active = false
            return FinishedUpload("C:\\target", bytes.size.toLong(), null, sha256)
        }

        override suspend fun cancel(uploadId: String) {
            cancelled += uploadId
            active = false
        }
    }

    private fun source(bytes: ByteArray, name: String = "photo.jpg") = object : UploadSource {
        override val name = name
        override val sizeBytes = bytes.size.toLong()
        override fun open(): InputStream = ByteArrayInputStream(bytes)
    }

    // --- Downloads ------------------------------------------------------------------------------

    @Test
    fun `a download arrives in chunks and is committed only after the PC's hash agrees`() = runBlocking {
        val pc = FakePc()
        val target = File(directory, "video.mp4")

        val outcome = FileDownloader(pc, chunkBytes = 1_000_000).download(path, FileDownloadSink(target), startedFrom = null)

        assertTrue(outcome is TransferOutcome.Done)
        assertArrayEquals(content, target.readBytes())
        assertEquals(listOf(0L, 1_000_000L, 2_000_000L), pc.offsetsRead)
        assertEquals(1, pc.hashRequests)
        assertFalse(File(directory, "video.mp4.part").exists())
    }

    @Test
    fun `a dropped link keeps what arrived and the next attempt continues from it`() = runBlocking {
        val pc = FakePc(failAfterChunks = 1)
        val sink = FileDownloadSink(File(directory, "video.mp4"))
        var identity: FileIdentity? = null
        val downloader = FileDownloader(pc, chunkBytes = 1_000_000)

        val first = downloader.download(path, sink, startedFrom = null, onIdentity = { identity = it })

        assertTrue(first is TransferOutcome.Failed && first.resumable)
        assertEquals(1_000_000L, sink.existingBytes())

        pc.failAfterChunks = Int.MAX_VALUE
        val second = downloader.download(path, sink, startedFrom = identity)

        assertTrue(second is TransferOutcome.Done)
        assertArrayEquals(content, File(directory, "video.mp4").readBytes())
        assertEquals(listOf(0L, 1_000_000L, 2_000_000L), pc.offsetsRead)
    }

    @Test
    fun `the connection ending under a request is an interruption, not a cancellation`() = runBlocking {
        // PcConnectionManager fails pending requests with a CancellationException when the socket
        // ends. The transfer's own coroutine is still active, so it must stop as resumable rather than
        // vanish as if the owner had cancelled it.
        val pc = FakePc(failAfterChunks = 1, dropAsCancellation = true)

        val outcome = FileDownloader(pc, chunkBytes = 1_000_000).download(path, FileDownloadSink(File(directory, "v.mp4")), startedFrom = null)

        assertTrue(outcome is TransferOutcome.Failed && outcome.resumable)
    }

    @Test
    fun `a file that changed on the PC is never stitched onto what arrived before`() = runBlocking {
        val pc = FakePc(failAfterChunks = 1)
        val sink = FileDownloadSink(File(directory, "video.mp4"))
        var identity: FileIdentity? = null
        val downloader = FileDownloader(pc, chunkBytes = 1_000_000)

        downloader.download(path, sink, startedFrom = null, onIdentity = { identity = it })

        pc.failAfterChunks = Int.MAX_VALUE
        pc.modified = "2026-09-13T11:00:00+00:00"
        val second = downloader.download(path, sink, startedFrom = identity)

        assertTrue(second is TransferOutcome.Failed && !second.resumable)
        assertEquals(0L, sink.existingBytes())
        assertFalse(File(directory, "video.mp4").exists())
    }

    @Test
    fun `bytes that do not hash to the PC's file are discarded`() = runBlocking {
        val pc = object : RemoteFileSource by FakePc() {
            override suspend fun hash(path: String, expectedSize: Long?, expectedModifiedUtc: String?) =
                FileHash(path, content.size.toLong(), null, "0".repeat(64))
        }
        val target = File(directory, "video.mp4")

        val outcome = FileDownloader(pc, chunkBytes = 1_000_000).download(path, FileDownloadSink(target), startedFrom = null)

        assertTrue(outcome is TransferOutcome.Failed && !outcome.resumable)
        assertFalse(target.exists())
        assertEquals(0, directory.listFiles()!!.size)
    }

    @Test
    fun `partial bytes with no record of which file they came from start over`() = runBlocking {
        val target = File(directory, "video.mp4")
        File(directory, "video.mp4.part").writeBytes(ByteArray(500) { 1 })
        val pc = FakePc()

        val outcome = FileDownloader(pc, chunkBytes = 1_000_000).download(path, FileDownloadSink(target), startedFrom = null)

        assertTrue(outcome is TransferOutcome.Done)
        assertArrayEquals(content, target.readBytes())
        assertEquals(0L, pc.offsetsRead.first())
    }

    @Test
    fun `refusals from the PC say what happened`() = runBlocking {
        for ((code, resumable) in listOf(
            NexusProtocol.Errors.ACCESS_DENIED to false,
            NexusProtocol.Errors.FILE_IN_USE to true,
            NexusProtocol.Errors.NOT_FOUND to false,
            NexusProtocol.Errors.NOT_AUTHORIZED to true,
        )) {
            val refusing = object : RemoteFileSource by FakePc() {
                override suspend fun read(path: String, offset: Long, maxBytes: Int, expectedSize: Long?, expectedModifiedUtc: String?): FileChunk =
                    throw NexusRequestException(ProtocolError(code, "raw message"))
            }

            val outcome = FileDownloader(refusing).download(path, FileDownloadSink(File(directory, "x.bin")), startedFrom = null)

            assertTrue(code, outcome is TransferOutcome.Failed)
            assertEquals(code, resumable, (outcome as TransferOutcome.Failed).resumable)
            assertFalse(code, outcome.reason == "raw message")
        }
    }

    // --- Uploads --------------------------------------------------------------------------------

    @Test
    fun `an upload goes in order-checked chunks and finishes with the whole file's digest`() = runBlocking {
        val pc = FakePc()

        val outcome = FileUploader(pc, maxChunkBytes = 1_000_000).upload(source(content), """C:\Users\Owner""", overwrite = false)

        assertTrue(outcome is TransferOutcome.Done)
        assertArrayEquals(content, pc.received.toByteArray())
        assertEquals(listOf(0L, 1_000_000L, 2_000_000L), pc.chunksSent)
    }

    @Test
    fun `a resumed upload sends only what the PC does not have, and the digest still covers everything`() = runBlocking {
        val pc = FakePc(failAfterChunks = 2)
        val uploader = FileUploader(pc, maxChunkBytes = 1_000_000)

        val first = uploader.upload(source(content), "C:\\", overwrite = false)
        assertTrue(first is TransferOutcome.Failed && first.resumable)

        pc.failAfterChunks = Int.MAX_VALUE
        val second = uploader.upload(source(content), "C:\\", overwrite = false)

        assertTrue(second is TransferOutcome.Done)
        assertArrayEquals(content, pc.received.toByteArray())

        // 0 and 1 MB before the drop; 2 MB after it. Nothing was sent twice.
        assertEquals(listOf(0L, 1_000_000L, 2_000_000L), pc.chunksSent)
    }

    @Test
    fun `a name already taken on the PC waits for the owner, and replacing it is explicit`() = runBlocking {
        val pc = FakePc().apply { existing = "photo.jpg" }
        val uploader = FileUploader(pc)

        val first = uploader.upload(source(content), "C:\\", overwrite = false)
        assertTrue(first is TransferOutcome.Conflict)
        assertTrue(pc.chunksSent.isEmpty())

        val replaced = uploader.upload(source(content), "C:\\", overwrite = true)
        assertTrue(replaced is TransferOutcome.Done)
    }

    @Test
    fun `cancelling an upload tells the PC to delete what arrived`() = runBlocking {
        val gate = CompletableDeferred<Unit>()
        val pc = object : RemoteUploadTarget by FakePc() {
            val cancelled = mutableListOf<String>()

            override suspend fun chunk(uploadId: String, offset: Long, data: String): UploadChunkResult {
                if (offset > 0) {
                    gate.complete(Unit)
                    kotlinx.coroutines.awaitCancellation()
                }
                return UploadChunkResult(uploadId, offset + Base64.getDecoder().decode(data).size)
            }

            override suspend fun cancel(uploadId: String) {
                cancelled += uploadId
            }
        }

        val running = async { FileUploader(pc, maxChunkBytes = 1_000_000).upload(source(content), "C:\\", overwrite = false) }

        gate.await()
        yield()
        running.cancel()

        assertTrue(runCatching { running.await() }.exceptionOrNull() is CancellationException)
        assertEquals(listOf("u1"), pc.cancelled)
    }

    @Test
    fun `a file that shrinks on the phone mid-upload is abandoned on both ends`() = runBlocking {
        val pc = FakePc()
        val lying = object : UploadSource {
            override val name = "grows.log"
            override val sizeBytes = 3_000_000L
            override fun open(): InputStream = ByteArrayInputStream(content)
        }

        val outcome = FileUploader(pc, maxChunkBytes = 1_000_000).upload(lying, "C:\\", overwrite = false)

        assertTrue(outcome is TransferOutcome.Failed && !outcome.resumable)
        assertEquals(listOf("u1"), pc.cancelled)
    }

    private fun sha256(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
}
