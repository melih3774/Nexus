package dev.nexus.control.files

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Log
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import dev.nexus.control.protocol.FileEntry
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

enum class TransferDirection { Download, Upload }

/** What a download is for: looking at it now, or keeping it in the phone's Downloads. */
enum class DownloadPurpose { Open, Save }

sealed interface TransferStatus {
    data object Queued : TransferStatus
    data object Running : TransferStatus
    data object Verifying : TransferStatus
    data object Done : TransferStatus
    data class Failed(val reason: String, val resumable: Boolean) : TransferStatus

    /** The PC already has a file with this name. Waiting for the owner to replace it or not. */
    data object Conflict : TransferStatus
}

data class Transfer(
    val id: Long,
    val direction: TransferDirection,
    val name: String,

    /** A download's file on the PC, or the folder an upload goes into. */
    val remotePath: String,
    val totalBytes: Long,
    val doneBytes: Long = 0,
    val status: TransferStatus = TransferStatus.Queued,
    val purpose: DownloadPurpose? = null,
) {
    val active: Boolean get() = status is TransferStatus.Queued || status is TransferStatus.Running || status is TransferStatus.Verifying

    val progress: Float get() = if (totalBytes <= 0) (if (status == TransferStatus.Done) 1f else 0f) else (doneBytes.toFloat() / totalBytes).coerceIn(0f, 1f)
}

/**
 * Every file moving between this phone and the PC.
 *
 * In the container rather than a screen's view model, so leaving Files does not cancel a download
 * and coming back shows where it is. Two run at a time; the rest queue.
 *
 * **What happens when the link drops.** Each request fails, and the transfer stops as a resumable
 * failure - never a bar that sits at 40% forever. The PC holds nothing for a download and a partial
 * file for an upload, and when the owner session is back the interrupted transfers resume from what
 * both ends kept, once. Anything else waits for a tap.
 */
class TransferManager(
    private val context: Context,
    private val scope: CoroutineScope,
    private val client: FileClient,

    /** Goes up each time the PC grants the owner session; see `SessionGate.authorizations`. */
    ownerAuthorizations: Flow<Long>,
) {
    private val _transfers = MutableStateFlow<List<Transfer>>(emptyList())
    val transfers: StateFlow<List<Transfer>> = _transfers.asStateFlow()

    private val _finished = MutableSharedFlow<Transfer>(extraBufferCapacity = 16)

    /** Each transfer that completed, once. The Files screen opens what was downloaded to open, and refreshes after an upload. */
    val finished: SharedFlow<Transfer> = _finished.asSharedFlow()

    private val ids = AtomicLong(0)
    private val slots = Semaphore(MAX_RUNNING)
    private val jobs = ConcurrentHashMap<Long, Job>()
    private val work = ConcurrentHashMap<Long, Work>()
    private val resolver: ContentResolver get() = context.contentResolver

    private val cacheRoot = File(context.cacheDir, CACHE_FOLDER)

    init {
        scope.launch {
            ownerAuthorizations.distinctUntilChanged().collect { count ->
                if (count > 0) resumeInterrupted()
            }
        }
    }

    fun download(entry: FileEntry, purpose: DownloadPurpose): Long {
        val cached = cachedFileFor(entry)

        // Already on the phone from an earlier Open, and the PC still reports the same file.
        if (purpose == DownloadPurpose.Open && cached.isFile && cached.length() == (entry.sizeBytes ?: -1)) {
            val transfer = Transfer(ids.incrementAndGet(), TransferDirection.Download, entry.name, entry.path, cached.length(), cached.length(), TransferStatus.Done, purpose)
            work[transfer.id] = Work.Download(FileDownloadSink(cached), identity = null, localUri = contentUriFor(cached))
            _transfers.update { it + transfer }
            _finished.tryEmit(transfer)
            return transfer.id
        }

        val sink: DownloadSink = when (purpose) {
            DownloadPurpose.Open -> FileDownloadSink(cached)
            DownloadPurpose.Save -> MediaStoreDownloadSink(resolver, entry.name, mimeTypeOf(entry.name))
        }

        val transfer = Transfer(ids.incrementAndGet(), TransferDirection.Download, entry.name, entry.path, entry.sizeBytes ?: 0, purpose = purpose)
        work[transfer.id] = Work.Download(sink, identity = null, localUri = null)
        _transfers.update { it + transfer }
        start(transfer.id)
        return transfer.id
    }

    fun upload(uri: Uri, directory: String): Long {
        val source = ContentUploadSource(resolver, uri)
        val transfer = Transfer(ids.incrementAndGet(), TransferDirection.Upload, source.name, directory, source.sizeBytes)

        work[transfer.id] = Work.Upload(source, overwrite = false)
        _transfers.update { it + transfer }

        if (source.sizeBytes < 0) {
            set(transfer.id) { it.copy(status = TransferStatus.Failed("Android did not say how large this file is, so it cannot be uploaded.", resumable = false)) }
        } else {
            start(transfer.id)
        }

        return transfer.id
    }

    /** Try again: resumable failures continue, the rest start over. */
    fun retry(id: Long) {
        val transfer = find(id) ?: return
        if (transfer.active) return
        start(id)
    }

    /** The owner said yes to replacing the file already on the PC. */
    fun replace(id: Long) {
        (work[id] as? Work.Upload)?.overwrite = true
        retry(id)
    }

    /**
     * Stops a transfer and forgets it. A download's partial bytes are deleted here; an upload tells the
     * PC to delete its partial file as it stops (see [FileUploader]).
     */
    fun cancel(id: Long) {
        jobs.remove(id)?.cancel()
        remove(id, discardPartial = true)
    }

    /** Removes a transfer that is not running. A finished download stays where it was put. */
    fun dismiss(id: Long) {
        val transfer = find(id) ?: return
        if (transfer.active) return cancel(id)

        remove(id, discardPartial = transfer.status != TransferStatus.Done)
    }

    private fun remove(id: Long, discardPartial: Boolean) {
        _transfers.update { list -> list.filterNot { it.id == id } }

        val item = work.remove(id)
        if (discardPartial) (item as? Work.Download)?.sink?.let { runCatching { it.discard() } }
    }

    fun clearFinished() {
        _transfers.value.filter { it.status == TransferStatus.Done }.forEach { dismiss(it.id) }
    }

    /** Cancels everything and empties the cache. For forgetting the PC. */
    fun cancelAll() {
        _transfers.value.forEach { cancel(it.id) }
        cacheRoot.deleteRecursively()
    }

    /** An intent that shows a finished download, or null when there is nothing to show. */
    fun viewIntent(id: Long): Intent? {
        val transfer = find(id) ?: return null
        val uri = (work[id] as? Work.Download)?.localUri ?: return null

        return Intent(Intent.ACTION_VIEW)
            .setDataAndType(uri, mimeTypeOf(transfer.name))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    }

    private fun start(id: Long) {
        val job = scope.launch {
            set(id) { it.copy(status = TransferStatus.Queued) }

            slots.withPermit {
                set(id) { it.copy(status = TransferStatus.Running) }

                val outcome = when (val item = work[id]) {
                    is Work.Download -> runDownload(id, item)
                    is Work.Upload -> runUpload(id, item)
                    null -> return@withPermit
                }

                finish(id, outcome)
            }
        }

        jobs[id] = job
        job.invokeOnCompletion { jobs.remove(id, job) }
    }

    private suspend fun runDownload(id: Long, item: Work.Download): TransferOutcome {
        val transfer = find(id) ?: return TransferOutcome.Failed("Gone", false)

        return FileDownloader(client).download(
            path = transfer.remotePath,
            sink = item.sink,
            startedFrom = item.identity,
            onIdentity = { identity ->
                item.identity = identity
                set(id) { it.copy(totalBytes = identity.sizeBytes) }
            },
            onProgress = { done, total -> set(id) { it.copy(doneBytes = done, totalBytes = total) } },
            onVerifying = { set(id) { it.copy(status = TransferStatus.Verifying) } },
        ).also { outcome ->
            if (outcome is TransferOutcome.Done) {
                item.localUri = when (val sink = item.sink) {
                    is FileDownloadSink -> contentUriFor(sink.target).also { prune(keep = sink.target) }
                    is MediaStoreDownloadSink -> sink.uri
                    else -> null
                }
            }

            // A failure that is not going to be resumed has no partial bytes to keep a record for.
            if (outcome is TransferOutcome.Failed && !outcome.resumable) item.identity = null
        }
    }

    private suspend fun runUpload(id: Long, item: Work.Upload): TransferOutcome {
        val transfer = find(id) ?: return TransferOutcome.Failed("Gone", false)

        return FileUploader(client).upload(item.source, transfer.remotePath, item.overwrite) { done, total ->
            set(id) { it.copy(doneBytes = done, totalBytes = total) }
        }
    }

    private fun finish(id: Long, outcome: TransferOutcome) {
        val item = work[id]

        set(id) {
            when (outcome) {
                is TransferOutcome.Done -> it.copy(status = TransferStatus.Done, doneBytes = outcome.sizeBytes, totalBytes = outcome.sizeBytes)
                is TransferOutcome.Failed -> it.copy(status = TransferStatus.Failed(outcome.reason, outcome.resumable))
                is TransferOutcome.Conflict -> it.copy(status = TransferStatus.Conflict)
            }
        }

        item?.interrupted = outcome is TransferOutcome.Failed && outcome.resumable
        item?.autoResumed = false

        if (outcome is TransferOutcome.Done) find(id)?.let { _finished.tryEmit(it) }
    }

    /** Once per return of the owner session, each transfer the connection interrupted picks up again. */
    private fun resumeInterrupted() {
        _transfers.value
            .filter { transfer -> transfer.status is TransferStatus.Failed && work[transfer.id]?.let { it.interrupted && !it.autoResumed } == true }
            .forEach { transfer ->
                work[transfer.id]?.autoResumed = true
                Log.i(TAG, "Resuming an interrupted transfer")
                start(transfer.id)
            }
    }

    private fun find(id: Long): Transfer? = _transfers.value.firstOrNull { it.id == id }

    private fun set(id: Long, change: (Transfer) -> Transfer) {
        _transfers.update { list -> list.map { if (it.id == id) change(it) else it } }
    }

    /** One folder per PC file and version, keeping the file's own name so the app that opens it sees its extension. */
    private fun cachedFileFor(entry: FileEntry): File {
        val key = MessageDigest.getInstance("SHA-256")
            .digest("${entry.path}|${entry.sizeBytes}|${entry.modifiedUtc}".toByteArray())
            .take(12)
            .joinToString("") { "%02x".format(it) }

        return File(File(cacheRoot, key), entry.name)
    }

    private fun contentUriFor(file: File): Uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)

    /** Keeps opened files under a budget, oldest first. They are copies of PC files, in a cache Android may empty. */
    private fun prune(keep: File) {
        runCatching {
            val folders = cacheRoot.listFiles()?.filter { it.isDirectory } ?: return
            var total = folders.sumOf { folder -> folder.walkTopDown().filter { it.isFile }.sumOf { it.length() } }

            for (folder in folders.sortedBy { it.lastModified() }) {
                if (total <= CACHE_BUDGET_BYTES) break
                if (keep.path.startsWith(folder.path)) continue

                val size = folder.walkTopDown().filter { it.isFile }.sumOf { it.length() }
                if (folder.deleteRecursively()) total -= size
            }
        }
    }

    private sealed class Work {
        var interrupted: Boolean = false
        var autoResumed: Boolean = false

        class Download(val sink: DownloadSink, var identity: FileIdentity?, var localUri: Uri?) : Work()
        class Upload(val source: UploadSource, var overwrite: Boolean) : Work()
    }

    companion object {
        private const val TAG = "NexusTransfers"
        private const val MAX_RUNNING = 2
        private const val CACHE_FOLDER = "nexus-files"
        private const val CACHE_BUDGET_BYTES = 1024L * 1024 * 1024

        fun mimeTypeOf(name: String): String =
            MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase())
                ?: "application/octet-stream"
    }
}

/**
 * A download into the phone's Downloads, under `Download/Nexus`, through MediaStore - no storage
 * permission needed. The entry stays pending, invisible to other apps, until it is verified; Android
 * removes a pending entry nobody finished on its own.
 */
class MediaStoreDownloadSink(
    private val resolver: ContentResolver,
    private val name: String,
    private val mimeType: String,
) : DownloadSink {

    var uri: Uri? = null
        private set

    override fun existingBytes(): Long =
        uri?.let { runCatching { resolver.openFileDescriptor(it, "r")?.use { pfd -> pfd.statSize } }.getOrNull() } ?: 0

    override fun openAppend(): OutputStream {
        val target = uri ?: resolver.insert(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
                put(MediaStore.MediaColumns.RELATIVE_PATH, "${Environment.DIRECTORY_DOWNLOADS}/Nexus")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            },
        )?.also { uri = it } ?: throw java.io.IOException("MediaStore refused a new download")

        val descriptor = resolver.openFileDescriptor(target, "rw") ?: throw java.io.IOException("no descriptor")
        return ParcelFileDescriptor.AutoCloseOutputStream(descriptor).also { it.channel.position(descriptor.statSize) }
    }

    override fun openRead(): InputStream =
        uri?.let { resolver.openInputStream(it) } ?: throw java.io.IOException("nothing downloaded")

    override fun discard() {
        uri?.let { runCatching { resolver.delete(it, null, null) } }
        uri = null
    }

    override fun commit() {
        val target = uri ?: throw java.io.IOException("nothing downloaded")
        resolver.update(target, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
    }
}

/** A file the owner picked with the system picker. */
class ContentUploadSource(private val resolver: ContentResolver, private val uri: Uri) : UploadSource {

    override val name: String
    override val sizeBytes: Long

    init {
        var displayName: String? = null
        var size = -1L

        runCatching {
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    displayName = cursor.getString(0)
                    if (!cursor.isNull(1)) size = cursor.getLong(1)
                }
            }
        }

        name = displayName?.takeIf { it.isNotBlank() } ?: (uri.lastPathSegment ?: "upload")
        sizeBytes = size
    }

    override fun open(): InputStream = resolver.openInputStream(uri) ?: throw java.io.IOException("the file could not be opened")
}
