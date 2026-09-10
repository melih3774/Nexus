package dev.nexus.control.security

import android.util.Base64
import android.util.Log
import dev.nexus.control.protocol.EvidenceAvailability
import dev.nexus.control.protocol.EvidenceSummary
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest

/** What asking for a piece of evidence produced. */
sealed interface EvidenceFetch {

    /** The bytes are on this phone, in [file], and the digest matched. */
    data class Ready(val file: File, val summary: EvidenceSummary) : EvidenceFetch

    /**
     * The PC still lists it but the file is gone.
     *
     * Distinct from [Failed] on purpose: "this was deleted or expired" is a fact about the
     * incident, and showing it as an error would suggest retrying might help.
     */
    data class Missing(val reason: String) : EvidenceFetch

    data class Failed(val reason: String) : EvidenceFetch
}

/**
 * Evidence files, fetched over the authenticated session and kept on disk while they are being
 * looked at.
 *
 * **Why there is a file at all.** `MediaPlayer` plays from a file descriptor or a URL, not from a
 * byte array, and there is no local HTTP server here to point it at — the transport is the Nexus
 * WebSocket and nothing else. So a clip has to land on disk before it can be played. That is a
 * cache, not a copy of the archive: it lives in `cacheDir`, which Android is free to delete, it is
 * pruned to a budget, and nothing here ever writes to external storage or the media gallery.
 *
 * **Why it is content-addressed.** The file name is the evidence digest, so opening the same clip
 * twice costs one download, and a partially written file can never be mistaken for a complete one —
 * the bytes are written to a `.part` file and only renamed once the digest matches what the PC
 * said. A truncated download is therefore indistinguishable from no download, which is the only
 * safe way for the answer to "is this cached?" to be a file-exists check.
 *
 * **No filesystem paths cross the wire.** The request names a session and an evidence id; the PC
 * resolves the path itself and re-checks containment. This class never learns where anything lives
 * on the PC.
 */
class EvidenceCache(
    private val root: File,
    private val client: SecurityIncidentClient,
) {

    /**
     * Fetches one piece of evidence, using the cached copy when the digest already matches.
     *
     * [onProgress] receives 0f..1f while bytes are arriving, so a six-megabyte clip does not look
     * like a frozen dialog.
     */
    suspend fun fetch(
        sessionId: String,
        summary: EvidenceSummary,
        onProgress: (Float) -> Unit = {},
    ): EvidenceFetch = withContext(Dispatchers.IO) {
        try {
            if (!root.exists()) root.mkdirs()

            val target = File(root, fileNameFor(summary))

            if (target.isFile && target.length() > 0) {
                onProgress(1f)
                return@withContext EvidenceFetch.Ready(target, summary)
            }

            download(sessionId, summary, target, onProgress)
        } catch (e: Exception) {
            Log.w(TAG, "Evidence fetch failed: ${e.javaClass.simpleName}: ${e.message}")
            EvidenceFetch.Failed("This evidence could not be fetched from the PC.")
        }
    }

    private suspend fun download(
        sessionId: String,
        summary: EvidenceSummary,
        target: File,
        onProgress: (Float) -> Unit,
    ): EvidenceFetch {
        val partial = File(target.absolutePath + ".part")
        partial.delete()

        val digest = MessageDigest.getInstance("SHA-256")
        var offset = 0L
        var total = summary.sizeBytes.coerceAtLeast(1)

        partial.outputStream().use { out ->
            while (true) {
                val chunk = client.getEvidence(sessionId, summary.evidenceId, offset, CHUNK_BYTES)

                when (chunk.availability) {
                    EvidenceAvailability.MISSING -> {
                        partial.delete()
                        return EvidenceFetch.Missing(
                            chunk.unavailable
                                ?: "This evidence is no longer on the PC.",
                        )
                    }

                    EvidenceAvailability.UNREADABLE -> {
                        partial.delete()
                        return EvidenceFetch.Failed(
                            chunk.unavailable ?: "The PC could not read this evidence.",
                        )
                    }

                    EvidenceAvailability.AVAILABLE -> Unit
                }

                if (chunk.sizeBytes > 0) total = chunk.sizeBytes

                val encoded = chunk.data

                if (encoded.isNullOrEmpty()) {
                    // No bytes and not an error means the file ended exactly on a chunk boundary.
                    if (chunk.endOfFile) break

                    partial.delete()
                    return EvidenceFetch.Failed("The PC sent no data for this evidence.")
                }

                val bytes = Base64.decode(encoded, Base64.DEFAULT)
                out.write(bytes)
                digest.update(bytes)
                offset += bytes.size

                onProgress((offset.toFloat() / total).coerceIn(0f, 1f))

                if (chunk.endOfFile) break

                if (offset > MAX_EVIDENCE_BYTES) {
                    partial.delete()
                    return EvidenceFetch.Failed("This evidence is too large to open on the phone.")
                }
            }
        }

        val actual = digest.digest().joinToString("") { "%02x".format(it) }

        // The digest is the PC's own record of what it captured. A mismatch means the bytes on this
        // phone are not the evidence, and showing them anyway would be worse than showing nothing.
        if (summary.sha256.isNotEmpty() && !actual.equals(summary.sha256, ignoreCase = true)) {
            partial.delete()
            Log.w(TAG, "Evidence digest mismatch for ${summary.evidenceId}")
            return EvidenceFetch.Failed("This evidence did not match its digest and was discarded.")
        }

        prune()

        // Renamed only now: a file under the content-addressed name is complete by construction.
        if (!partial.renameTo(target)) {
            partial.delete()
            return EvidenceFetch.Failed("This evidence could not be stored on the phone.")
        }

        onProgress(1f)
        return EvidenceFetch.Ready(target, summary)
    }

    /** The digest names the file, so the same clip is never downloaded twice. */
    private fun fileNameFor(summary: EvidenceSummary): String {
        val stem = summary.sha256.ifEmpty { summary.evidenceId }.take(48)
        return "$stem${extensionFor(summary.contentType)}"
    }

    /**
     * `MediaPlayer` sniffs content, but an extension costs nothing and removes a class of
     * "it plays on one phone" problem.
     */
    private fun extensionFor(contentType: String): String = when {
        contentType.contains("mp4") -> ".mp4"
        contentType.contains("jpeg") -> ".jpg"
        contentType.contains("png") -> ".png"
        contentType.contains("webm") -> ".webm"
        contentType.contains("wav") -> ".wav"
        else -> ".bin"
    }

    /**
     * Keeps the cache under a budget, oldest first.
     *
     * Deliberately crude. This holds recordings of the owner's home; the right amount to keep on a
     * phone is "the ones being looked at", and anything cleverer would be an argument for keeping
     * more of them for longer.
     */
    private fun prune() {
        try {
            val files = root.listFiles()?.filter { it.isFile } ?: return
            var total = files.sumOf { it.length() }

            if (total <= CACHE_BUDGET_BYTES) return

            for (file in files.sortedBy { it.lastModified() }) {
                if (total <= CACHE_BUDGET_BYTES) break
                val size = file.length()
                if (file.delete()) total -= size
            }
        } catch (e: Exception) {
            Log.w(TAG, "Evidence cache prune failed: ${e.javaClass.simpleName}")
        }
    }

    /** Removes every cached file. Used when the owner unpairs. */
    fun clear() {
        try {
            root.listFiles()?.forEach { it.delete() }
        } catch (e: Exception) {
            Log.w(TAG, "Evidence cache clear failed: ${e.javaClass.simpleName}")
        }
    }

    private companion object {
        const val TAG = "NexusEvidenceCache"

        /** Matches the PC's default chunk. Asking for more would only be clamped there. */
        const val CHUNK_BYTES = 512 * 1024

        /** A clip larger than this is not something to open on a phone over a tunnel. */
        const val MAX_EVIDENCE_BYTES = 256L * 1024 * 1024

        const val CACHE_BUDGET_BYTES = 192L * 1024 * 1024
    }
}
