package dev.nexus.control.update

import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.Base64

/** What a download produced. */
sealed interface ApkDownload {

    /** The whole package is in [file] and its SHA-256 is the one the PC's release recorded. */
    data class Ready(val file: File) : ApkDownload

    /**
     * It did not finish. [resumable] means the bytes already received were kept and the next attempt
     * continues from them; otherwise they were discarded because they cannot be trusted.
     */
    data class Failed(val reason: String, val resumable: Boolean) : ApkDownload
}

/**
 * Fetches the APK from the PC in ranges, the way evidence clips already are.
 *
 * **Interrupted downloads resume.** Bytes go into a `.part` file named for the exact build and hash
 * being fetched; a later attempt for the same package asks from the length already on disk. A
 * different package - a newer publish - starts clean.
 *
 * **Nothing unverified is ever returned.** The finished file is hashed as a whole, after any number of
 * resumptions, and only a match is renamed to its final name. A mismatch deletes everything, because a
 * `.part` that produced a wrong digest has no good prefix worth keeping.
 */
class ApkDownloader(
    private val directory: File,
    private val source: PackageChunkSource,
    private val chunkBytes: Int = CHUNK_BYTES,
) {

    suspend fun download(info: AndroidUpdateInfo, onProgress: (Float) -> Unit = {}): ApkDownload = withContext(Dispatchers.IO) {
        directory.mkdirs()

        val stem = "nexus-${info.versionCode}-${info.sha256.take(16).lowercase()}"
        val target = File(directory, "$stem.apk")
        val partial = File(directory, "$stem.apk.part")

        discardOthers(keep = setOf(target.name, partial.name))

        if (target.isFile && sha256(target).equals(info.sha256, ignoreCase = true)) {
            onProgress(1f)
            return@withContext ApkDownload.Ready(target)
        }

        target.delete()

        if (partial.length() > info.sizeBytes) {
            partial.delete()
        }

        var offset = partial.length()
        onProgress(progress(offset, info.sizeBytes))

        try {
            FileOutputStream(partial, true).use { out ->
                while (offset < info.sizeBytes) {
                    val chunk = source.chunk(info.versionCode, offset, chunkBytes)

                    if (chunk.versionCode != info.versionCode || !chunk.sha256.equals(info.sha256, ignoreCase = true)) {
                        partial.delete()
                        return@withContext ApkDownload.Failed("The PC is offering a different package now. Check for updates again.", resumable = false)
                    }

                    if (chunk.offset != offset) {
                        return@withContext ApkDownload.Failed("The PC answered for the wrong part of the package.", resumable = true)
                    }

                    val bytes = chunk.data?.let { Base64.getDecoder().decode(it) } ?: ByteArray(0)

                    if (bytes.isEmpty() && !chunk.endOfFile) {
                        return@withContext ApkDownload.Failed("The PC sent no data.", resumable = true)
                    }

                    out.write(bytes)
                    out.flush()
                    offset += bytes.size
                    onProgress(progress(offset, info.sizeBytes))

                    if (chunk.endOfFile) break
                }

                out.fd.sync()
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: NexusRequestException) {
            val gone = e.error.code == NexusProtocol.Errors.NOT_FOUND

            if (gone) partial.delete()

            return@withContext ApkDownload.Failed(
                if (gone) "That update is no longer offered by the PC. Check for updates again." else e.error.message,
                resumable = !gone,
            )
        } catch (e: Exception) {
            // The link dropped. What arrived stays in .part for the next attempt.
            return@withContext ApkDownload.Failed("The download was interrupted. Try again to continue where it stopped.", resumable = true)
        }

        if (partial.length() != info.sizeBytes || !sha256(partial).equals(info.sha256, ignoreCase = true)) {
            partial.delete()
            return@withContext ApkDownload.Failed("The downloaded update did not match its checksum and was discarded.", resumable = false)
        }

        if (!partial.renameTo(target)) {
            partial.delete()
            return@withContext ApkDownload.Failed("The update could not be stored on this phone.", resumable = false)
        }

        onProgress(1f)
        ApkDownload.Ready(target)
    }

    /** Only ever one package's worth of space: the one being fetched. */
    private fun discardOthers(keep: Set<String>) {
        directory.listFiles()?.filter { it.isFile && it.name !in keep }?.forEach { it.delete() }
    }

    companion object {
        const val CHUNK_BYTES = 1024 * 1024

        fun sha256(file: File): String {
            val digest = MessageDigest.getInstance("SHA-256")

            file.inputStream().use { input ->
                val buffer = ByteArray(256 * 1024)
                while (true) {
                    val read = input.read(buffer)
                    if (read < 0) break
                    digest.update(buffer, 0, read)
                }
            }

            return digest.digest().joinToString("") { "%02x".format(it) }
        }

        private fun progress(done: Long, total: Long): Float =
            if (total <= 0) 0f else (done.toFloat() / total).coerceIn(0f, 1f)
    }
}
