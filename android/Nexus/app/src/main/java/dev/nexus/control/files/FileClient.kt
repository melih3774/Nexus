package dev.nexus.control.files

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.ListFilesResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.StatResult
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

@Serializable
data class FileRoot(
    val path: String = "",
    val name: String = "",
    val kind: String = "folder",
    @SerialName("total_bytes") val totalBytes: Long? = null,
    @SerialName("free_bytes") val freeBytes: Long? = null,
)

@Serializable
data class FilePlace(val name: String = "", val path: String = "")

@Serializable
data class FileRootsResult(
    val roots: List<FileRoot> = emptyList(),
    val places: List<FilePlace> = emptyList(),
)

/** One range of a PC file. [data] is standard base64. */
@Serializable
data class FileChunk(
    val path: String = "",
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    @SerialName("modified_utc") val modifiedUtc: String? = null,
    val offset: Long = 0,
    @SerialName("chunk_bytes") val chunkBytes: Int = 0,
    @SerialName("end_of_file") val endOfFile: Boolean = false,
    val data: String? = null,
)

@Serializable
data class FileHash(
    val path: String = "",
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    @SerialName("modified_utc") val modifiedUtc: String? = null,
    val sha256: String = "",
)

@Serializable
data class UploadState(
    @SerialName("upload_id") val uploadId: String = "",
    val path: String = "",
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    @SerialName("received_bytes") val receivedBytes: Long = 0,
    @SerialName("max_chunk_bytes") val maxChunkBytes: Int = 0,
    val resumed: Boolean = false,
)

@Serializable
data class UploadChunkResult(
    @SerialName("upload_id") val uploadId: String = "",
    @SerialName("received_bytes") val receivedBytes: Long = 0,
)

@Serializable
data class FinishedUpload(
    val path: String = "",
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    @SerialName("modified_utc") val modifiedUtc: String? = null,
    val sha256: String = "",
)

@Serializable
data class PcClipboard(
    @SerialName("has_text") val hasText: Boolean = false,
    val text: String? = null,
    val length: Int = 0,
    val truncated: Boolean = false,
    val version: Long = 0,
    val origin: String = ORIGIN_PC,
) {
    companion object {
        const val ORIGIN_PC = "pc"
        const val ORIGIN_PHONE = "phone"

        /** Matches PC Core's `ClipboardService.MaxTextChars`. */
        const val MAX_TEXT_CHARS = 256 * 1024
    }
}

@Serializable
data class ClipboardSetResult(val changed: Boolean = false, val version: Long = 0, val length: Int = 0)

/** Where a download's bytes come from. An interface so a download can be tested without a PC. */
interface RemoteFileSource {
    suspend fun read(path: String, offset: Long, maxBytes: Int, expectedSize: Long?, expectedModifiedUtc: String?): FileChunk

    suspend fun hash(path: String, expectedSize: Long?, expectedModifiedUtc: String?): FileHash
}

/** Where an upload's bytes go. */
interface RemoteUploadTarget {
    suspend fun begin(directory: String, name: String, sizeBytes: Long, overwrite: Boolean): UploadState

    suspend fun chunk(uploadId: String, offset: Long, data: String): UploadChunkResult

    suspend fun finish(uploadId: String, sha256: String): FinishedUpload

    suspend fun cancel(uploadId: String)
}

/** Every Files and Clipboard call, and nothing that decides anything. */
class FileClient(private val connection: NexusCommandChannel) : RemoteFileSource, RemoteUploadTarget {

    suspend fun roots(): FileRootsResult = call(NexusProtocol.Actions.FILES_ROOTS, null, FileRootsResult.serializer())

    suspend fun list(path: String, includeHidden: Boolean): ListFilesResult = call(
        NexusProtocol.Actions.LIST_FILES,
        encode(ListParams.serializer(), ListParams(path, includeHidden)),
        ListFilesResult.serializer(),
        timeoutMs = 60_000,
    )

    suspend fun stat(path: String): StatResult =
        call(NexusProtocol.Actions.STAT_FILE, encode(PathParams.serializer(), PathParams(path)), StatResult.serializer())

    override suspend fun read(path: String, offset: Long, maxBytes: Int, expectedSize: Long?, expectedModifiedUtc: String?): FileChunk = call(
        NexusProtocol.Actions.FILES_READ,
        encode(ReadParams.serializer(), ReadParams(path, offset, maxBytes, expectedSize, expectedModifiedUtc)),
        FileChunk.serializer(),
        // A megabyte over a phone link on a bad day.
        timeoutMs = 60_000,
    )

    override suspend fun hash(path: String, expectedSize: Long?, expectedModifiedUtc: String?): FileHash = call(
        NexusProtocol.Actions.FILES_HASH,
        encode(HashParams.serializer(), HashParams(path, expectedSize, expectedModifiedUtc)),
        FileHash.serializer(),
        // The PC reads the whole file to answer; a large one on a spinning disk takes minutes.
        timeoutMs = 15 * 60_000L,
    )

    override suspend fun begin(directory: String, name: String, sizeBytes: Long, overwrite: Boolean): UploadState = call(
        NexusProtocol.Actions.FILES_UPLOAD_BEGIN,
        encode(BeginParams.serializer(), BeginParams(directory, name, sizeBytes, overwrite)),
        UploadState.serializer(),
    )

    override suspend fun chunk(uploadId: String, offset: Long, data: String): UploadChunkResult = call(
        NexusProtocol.Actions.FILES_UPLOAD_CHUNK,
        encode(ChunkParams.serializer(), ChunkParams(uploadId, offset, data)),
        UploadChunkResult.serializer(),
        timeoutMs = 60_000,
    )

    override suspend fun finish(uploadId: String, sha256: String): FinishedUpload = call(
        NexusProtocol.Actions.FILES_UPLOAD_FINISH,
        encode(FinishParams.serializer(), FinishParams(uploadId, sha256)),
        FinishedUpload.serializer(),
        timeoutMs = 60_000,
    )

    override suspend fun cancel(uploadId: String) {
        connection.request(NexusProtocol.Actions.FILES_UPLOAD_CANCEL, encode(CancelParams.serializer(), CancelParams(uploadId)))
    }

    suspend fun clipboard(): PcClipboard = call(NexusProtocol.Actions.CLIPBOARD_GET, null, PcClipboard.serializer())

    suspend fun setClipboard(text: String): ClipboardSetResult = call(
        NexusProtocol.Actions.CLIPBOARD_SET,
        encode(SetClipboardParams.serializer(), SetClipboardParams(text)),
        ClipboardSetResult.serializer(),
    )

    private suspend fun <T> call(action: String, params: JsonElement?, serializer: KSerializer<T>, timeoutMs: Long = 20_000): T {
        val result = connection.request(action, params, timeoutMs) ?: throw IllegalStateException("$action returned no result")
        return NexusJson.decodeFromJsonElement(serializer, result)
    }

    private fun <T> encode(serializer: KSerializer<T>, value: T): JsonElement = NexusJson.encodeToJsonElement(serializer, value)

    @Serializable
    private data class PathParams(val path: String)

    @Serializable
    private data class ListParams(val path: String, @SerialName("include_hidden") val includeHidden: Boolean)

    @Serializable
    private data class ReadParams(
        val path: String,
        val offset: Long,
        @SerialName("max_bytes") val maxBytes: Int,
        @SerialName("expected_size_bytes") val expectedSizeBytes: Long? = null,
        @SerialName("expected_modified_utc") val expectedModifiedUtc: String? = null,
    )

    @Serializable
    private data class HashParams(
        val path: String,
        @SerialName("expected_size_bytes") val expectedSizeBytes: Long? = null,
        @SerialName("expected_modified_utc") val expectedModifiedUtc: String? = null,
    )

    @Serializable
    private data class BeginParams(
        val directory: String,
        val name: String,
        @SerialName("size_bytes") val sizeBytes: Long,
        val overwrite: Boolean,
    )

    @Serializable
    private data class ChunkParams(@SerialName("upload_id") val uploadId: String, val offset: Long, val data: String)

    @Serializable
    private data class FinishParams(@SerialName("upload_id") val uploadId: String, val sha256: String)

    @Serializable
    private data class CancelParams(@SerialName("upload_id") val uploadId: String)

    @Serializable
    private data class SetClipboardParams(val text: String)
}
