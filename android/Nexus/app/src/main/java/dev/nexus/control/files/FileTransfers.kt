package dev.nexus.control.files

import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.Base64

/** How a transfer ended. */
sealed interface TransferOutcome {

    /** Everything arrived and the digest on both ends agreed. */
    data class Done(val remotePath: String, val sizeBytes: Long) : TransferOutcome

    /**
     * It did not finish. [resumable] means what already moved was kept and trying again continues from
     * it - a dropped link, a locked phone. Otherwise it was discarded, because it cannot be trusted or
     * there is nothing to continue.
     */
    data class Failed(val reason: String, val resumable: Boolean) : TransferOutcome

    /** An upload whose name is already taken on the PC. Replacing it is the owner's decision. */
    data class Conflict(val name: String) : TransferOutcome
}

/** The size and modification time a download started from, so a resumed download can insist on the same file. */
data class FileIdentity(val sizeBytes: Long, val modifiedUtc: String?)

/** Where downloaded bytes are kept on the phone until they are complete. */
interface DownloadSink {

    /** Bytes kept from an earlier attempt. */
    fun existingBytes(): Long

    /** Appends after [existingBytes]. */
    fun openAppend(): OutputStream

    fun openRead(): InputStream

    /** Drops everything, including what an earlier attempt kept. */
    fun discard()

    /** Makes the complete file visible where it belongs. */
    fun commit()
}

/** A download into a file: `name.part` while it arrives, renamed to `name` once it is verified. */
class FileDownloadSink(val target: File) : DownloadSink {
    private val partial = File(target.path + ".part")

    override fun existingBytes(): Long = if (partial.isFile) partial.length() else 0

    override fun openAppend(): OutputStream {
        target.parentFile?.mkdirs()
        return FileOutputStream(partial, true)
    }

    override fun openRead(): InputStream = partial.inputStream()

    override fun discard() {
        partial.delete()
        target.delete()
    }

    override fun commit() {
        target.delete()
        if (!partial.renameTo(target)) throw IOException("could not rename the finished download")
    }
}

/**
 * A PC file, to a phone, in ranges.
 *
 * **Resuming** is asking from the length already kept. A resumed download carries the [FileIdentity]
 * it started from and PC Core refuses a range of anything else (`file_changed`), so two versions of a
 * file are never stitched into one.
 *
 * **Nothing unverified is committed.** When the last range has arrived, the phone hashes what it holds
 * and asks PC Core for the hash of the file on disk. Only a match is moved into place - which also
 * catches a resume that appended in the wrong place, the one bug this design can have.
 */
class FileDownloader(
    private val source: RemoteFileSource,
    private val chunkBytes: Int = CHUNK_BYTES,
) {
    suspend fun download(
        path: String,
        sink: DownloadSink,
        startedFrom: FileIdentity?,
        onIdentity: (FileIdentity) -> Unit = {},
        onProgress: (done: Long, total: Long) -> Unit = { _, _ -> },
        onVerifying: () -> Unit = {},
    ): TransferOutcome = withContext(Dispatchers.IO) {
        var identity = startedFrom
        var offset = sink.existingBytes()

        // Bytes with no record of which file they came from cannot be continued.
        if (identity == null || offset > identity.sizeBytes) {
            sink.discard()
            offset = 0
            identity = null
        }

        try {
            if (identity == null || offset < identity.sizeBytes) {
                sink.openAppend().use { out ->
                    while (true) {
                        val chunk = source.read(path, offset, chunkBytes, identity?.sizeBytes, identity?.modifiedUtc)

                        if (identity == null) {
                            identity = FileIdentity(chunk.sizeBytes, chunk.modifiedUtc).also(onIdentity)
                        }

                        if (chunk.offset != offset) {
                            return@withContext TransferOutcome.Failed("The PC answered for the wrong part of the file.", resumable = true)
                        }

                        val bytes = chunk.data?.let { Base64.getDecoder().decode(it) } ?: ByteArray(0)
                        out.write(bytes)
                        offset += bytes.size
                        onProgress(offset, chunk.sizeBytes)

                        if (chunk.endOfFile) break

                        if (bytes.isEmpty()) {
                            return@withContext TransferOutcome.Failed("The PC sent no data.", resumable = true)
                        }
                    }

                    out.flush()
                    (out as? FileOutputStream)?.fd?.sync()
                }
            }

            val expected = identity!!
            onVerifying()

            val remote = source.hash(path, expected.sizeBytes, expected.modifiedUtc)
            val local = sink.openRead().use(::sha256)

            if (offset != remote.sizeBytes || !local.equals(remote.sha256, ignoreCase = true)) {
                sink.discard()
                return@withContext TransferOutcome.Failed("What arrived did not match the file on the PC, so it was discarded.", resumable = false)
            }

            sink.commit()
            onProgress(offset, offset)
            TransferOutcome.Done(path, offset)
        } catch (e: CancellationException) {
            // Two things arrive as a cancellation: the owner cancelling this transfer, which must
            // propagate, and the connection ending under a request, which is only an interruption.
            if (!currentCoroutineContext().isActive) throw e
            interrupted()
        } catch (e: NexusRequestException) {
            refusal(e) { if (!it) sink.discard() }
        } catch (e: NotConnectedException) {
            interrupted()
        } catch (e: IOException) {
            sink.discard()
            TransferOutcome.Failed("This phone could not store the file.", resumable = false)
        }
    }

    companion object {
        const val CHUNK_BYTES = 1024 * 1024
    }
}

/** A phone file, as the uploader reads it. */
interface UploadSource {
    val name: String
    val sizeBytes: Long

    fun open(): InputStream
}

/**
 * A phone file, to a folder on the PC.
 *
 * PC Core writes a hidden partial file beside the destination and puts it in place only when the
 * whole file has arrived and hashes to what this sends at the end. Resuming is beginning the same
 * upload again: PC Core answers with how much it already holds, and this reads - and hashes - that
 * much of the file before sending the rest, so the digest still covers every byte.
 */
class FileUploader(
    private val target: RemoteUploadTarget,
    private val maxChunkBytes: Int = CHUNK_BYTES,
) {
    suspend fun upload(
        source: UploadSource,
        directory: String,
        overwrite: Boolean,
        onProgress: (done: Long, total: Long) -> Unit = { _, _ -> },
    ): TransferOutcome = withContext(Dispatchers.IO) {
        var uploadId: String? = null

        try {
            val state = target.begin(directory, source.name, source.sizeBytes, overwrite)
            uploadId = state.uploadId

            val chunk = minOf(maxChunkBytes, state.maxChunkBytes.takeIf { it > 0 } ?: maxChunkBytes)
            val digest = MessageDigest.getInstance("SHA-256")
            val buffer = ByteArray(chunk)
            var offset = 0L

            source.open().use { input ->
                while (offset < state.receivedBytes) {
                    val read = readFully(input, buffer, minOf(chunk.toLong(), state.receivedBytes - offset).toInt())
                    if (read <= 0) return@withContext abandon(uploadId, "The file on this phone changed while it was uploading.")
                    digest.update(buffer, 0, read)
                    offset += read
                }

                onProgress(offset, source.sizeBytes)

                while (offset < source.sizeBytes) {
                    val read = readFully(input, buffer, minOf(chunk.toLong(), source.sizeBytes - offset).toInt())
                    if (read <= 0) return@withContext abandon(uploadId, "The file on this phone changed while it was uploading.")

                    digest.update(buffer, 0, read)
                    target.chunk(state.uploadId, offset, Base64.getEncoder().encodeToString(buffer.copyOf(read)))
                    offset += read
                    onProgress(offset, source.sizeBytes)
                }

                if (input.read() != -1) return@withContext abandon(uploadId, "The file on this phone changed while it was uploading.")
            }

            val finished = target.finish(state.uploadId, hex(digest.digest()))
            TransferOutcome.Done(finished.path, finished.sizeBytes)
        } catch (e: CancellationException) {
            if (!currentCoroutineContext().isActive) {
                // Cancelled by the owner: tell the PC so it deletes the partial file now rather than
                // after its idle timeout. Best effort, and bounded - the link may be what failed.
                uploadId?.let { id -> withContext(NonCancellable) { withTimeoutOrNull(5_000) { runCatching { target.cancel(id) } } } }
                throw e
            }
            interrupted()
        } catch (e: NexusRequestException) {
            when {
                e.error.code == NexusProtocol.Errors.ALREADY_EXISTS && uploadId == null -> TransferOutcome.Conflict(source.name)

                // Refused at the start - a name Windows cannot use, a folder outside the boundary.
                // Nothing to resume, and trying again would be refused the same way.
                e.error.code == NexusProtocol.Errors.INVALID_PARAMETERS && uploadId == null ->
                    TransferOutcome.Failed(describeFileError(e.error.code, e.error.message), resumable = false)

                else -> refusal(e) { resumable -> if (!resumable) uploadId?.let { runCatching { target.cancel(it) } } }
            }
        } catch (e: NotConnectedException) {
            interrupted()
        } catch (e: IOException) {
            abandon(uploadId, "This phone could not read the file.")
        }
    }

    private suspend fun abandon(uploadId: String?, reason: String): TransferOutcome {
        uploadId?.let { runCatching { target.cancel(it) } }
        return TransferOutcome.Failed(reason, resumable = false)
    }

    companion object {
        /** PC Core's limit per chunk; it answers with its own in `max_chunk_bytes` too. */
        const val CHUNK_BYTES = 1024 * 1024
    }
}

private fun interrupted() = TransferOutcome.Failed("The connection to the PC dropped. Resume to continue where it stopped.", resumable = true)

/** A refusal from PC Core, as something the owner can act on. [discardIfFinal] runs with whether it can be resumed. */
private inline fun refusal(e: NexusRequestException, discardIfFinal: (Boolean) -> Unit): TransferOutcome.Failed {
    val resumable = when (e.error.code) {
        // Temporary: the owner unlocks again, closes the other program, or the link recovers.
        NexusProtocol.Errors.NOT_AUTHORIZED,
        NexusProtocol.Errors.FILE_IN_USE,
        NexusProtocol.Errors.RATE_LIMITED,
        NexusProtocol.Errors.INVALID_PARAMETERS,
        -> true

        // An upload the PC no longer holds starts again from the top on retry; nothing here to keep.
        NexusProtocol.Errors.NOT_FOUND -> false

        else -> false
    }

    discardIfFinal(resumable)
    return TransferOutcome.Failed(describeFileError(e.error.code, e.error.message), resumable)
}

/** What the owner is told for a refusal from the Files and Clipboard capabilities. */
fun describeFileError(code: String, message: String): String = when (code) {
    NexusProtocol.Errors.ACCESS_DENIED -> "Windows refused access. Nexus runs as you on the PC, without administrator rights."
    NexusProtocol.Errors.NOT_FOUND -> "That is no longer on the PC."
    NexusProtocol.Errors.FILE_IN_USE -> "Another program on the PC has that file open."
    NexusProtocol.Errors.FILE_CHANGED -> "The file changed on the PC during the transfer. Start it again."
    NexusProtocol.Errors.DISK_FULL -> "The PC's disk is full."
    NexusProtocol.Errors.ALREADY_EXISTS -> "Something with that name is already there."
    NexusProtocol.Errors.CHECKSUM_MISMATCH -> "What arrived did not match the file on the phone, so the PC discarded it."
    NexusProtocol.Errors.PATH_NOT_ALLOWED -> "That place is not reachable through Files."
    NexusProtocol.Errors.NOT_AUTHORIZED -> "The PC wants the owner again. Unlock Nexus, then retry."
    NexusProtocol.Errors.CLIPBOARD_UNAVAILABLE -> "The PC's clipboard is busy. Try again."
    NexusProtocol.Errors.RATE_LIMITED -> "The PC is busy with other transfers. Try again shortly."
    NexusProtocol.Errors.UPDATE_REQUIRED -> "Update Nexus on this phone to use this."
    else -> message.ifBlank { "The PC refused the request." }
}

internal fun readFully(input: InputStream, buffer: ByteArray, length: Int): Int {
    var total = 0
    while (total < length) {
        val read = input.read(buffer, total, length - total)
        if (read < 0) break
        total += read
    }
    return if (total == 0 && length > 0) -1 else total
}

internal fun sha256(input: InputStream): String {
    val digest = MessageDigest.getInstance("SHA-256")
    val buffer = ByteArray(256 * 1024)
    while (true) {
        val read = input.read(buffer)
        if (read < 0) break
        digest.update(buffer, 0, read)
    }
    return hex(digest.digest())
}

internal fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
