package dev.nexus.control

import android.content.ContentUris
import android.provider.MediaStore
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.nexus.control.files.DownloadPurpose
import dev.nexus.control.files.FileClient
import dev.nexus.control.files.MediaStoreDownloadSink
import dev.nexus.control.files.TransferManager
import dev.nexus.control.files.TransferStatus
import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.FileEntry
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.ui.ClipboardPanel
import dev.nexus.control.ui.FilesScreen
import dev.nexus.control.ui.theme.NexusTheme
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlinx.serialization.json.put
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.security.MessageDigest
import java.util.Base64
import kotlin.random.Random

/**
 * The Android half of Files that a JVM test cannot reach: MediaStore, FileProvider, and the screens
 * rendering against the real application container.
 */
@RunWith(AndroidJUnit4::class)
class FilesOnDeviceTest {

    @get:Rule
    val compose = createComposeRule()

    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val content = Random(11).nextBytes(3_300_000)
    private val modified = "2026-09-13T10:00:00+00:00"
    private val cleanup = mutableListOf<() -> Unit>()

    @After
    fun tearDown() {
        cleanup.forEach { runCatching { it() } }
        scope.cancel()
    }

    /** A PC that serves one file through the real FileClient request shapes. */
    private inner class FakePc : NexusCommandChannel {
        override suspend fun request(action: String, params: JsonElement?, timeoutMs: Long): JsonElement? {
            val p = params?.jsonObject

            return when (action) {
                NexusProtocol.Actions.FILES_READ -> {
                    val offset = p!!["offset"]!!.jsonPrimitive.long.toInt()
                    val max = p["max_bytes"]!!.jsonPrimitive.long.toInt()
                    val end = minOf(content.size, offset + max)

                    buildJsonObject {
                        put("path", """C:\Users\Owner\clip.mp4""")
                        put("size_bytes", content.size.toLong())
                        put("modified_utc", modified)
                        put("offset", offset.toLong())
                        put("chunk_bytes", end - offset)
                        put("end_of_file", end >= content.size)
                        put("data", Base64.getEncoder().encodeToString(content.copyOfRange(offset, end)))
                    }
                }

                NexusProtocol.Actions.FILES_HASH -> buildJsonObject {
                    put("path", """C:\Users\Owner\clip.mp4""")
                    put("size_bytes", content.size.toLong())
                    put("sha256", MessageDigest.getInstance("SHA-256").digest(content).joinToString("") { "%02x".format(it) })
                }

                else -> error("unexpected $action")
            }
        }

        override fun notify(action: String, params: JsonElement?): Boolean = false
    }

    private val entry = FileEntry(name = "nexus-device-test-${System.nanoTime()}.mp4", path = """C:\Users\Owner\clip.mp4""", type = "file", sizeBytes = 3_300_000, modifiedUtc = modified)

    @Test
    fun aDownloadIntoMediaStoreResumesAndBecomesVisibleOnlyWhenComplete() {
        val resolver = context.contentResolver
        val sink = MediaStoreDownloadSink(resolver, "nexus-sink-test-${System.nanoTime()}.bin", "application/octet-stream")
        cleanup += { sink.discard() }

        sink.openAppend().use { it.write(content, 0, 1_000_000) }
        assertEquals(1_000_000L, sink.existingBytes())

        // A second attempt appends rather than truncating.
        sink.openAppend().use { it.write(content, 1_000_000, content.size - 1_000_000) }
        assertEquals(content.size.toLong(), sink.existingBytes())

        assertArrayEquals(content, sink.openRead().use { it.readBytes() })

        val uri = assertNotNull(sink.uri).let { sink.uri!! }
        assertEquals(1, pendingFlag(uri))

        sink.commit()
        assertEquals(0, pendingFlag(uri))
    }

    @Test
    fun theTransferManagerSavesToDownloadsAndOpensThroughFileProvider() = runBlocking {
        val manager = TransferManager(context, scope, FileClient(FakePc()), MutableStateFlow(0L))

        val saved = manager.download(entry, DownloadPurpose.Save)
        val savedDone = withTimeout(30_000) { manager.transfers.first { list -> list.any { it.id == saved && it.status == TransferStatus.Done } } }
        assertEquals(content.size.toLong(), savedDone.first { it.id == saved }.doneBytes)

        val savedIntent = assertNotNull(manager.viewIntent(saved)).let { manager.viewIntent(saved)!! }
        cleanup += { context.contentResolver.delete(savedIntent.data!!, null, null) }
        assertArrayEquals(content, context.contentResolver.openInputStream(savedIntent.data!!)!!.use { it.readBytes() })

        val opened = manager.download(entry, DownloadPurpose.Open)
        withTimeout(30_000) { manager.transfers.first { list -> list.any { it.id == opened && it.status == TransferStatus.Done } } }

        val openIntent = manager.viewIntent(opened)!!
        assertEquals("${context.packageName}.files", openIntent.data!!.authority)
        assertEquals("video/mp4", openIntent.type)
        assertArrayEquals(content, context.contentResolver.openInputStream(openIntent.data!!)!!.use { it.readBytes() })

        cleanup += { manager.cancelAll() }
    }

    @Test
    fun filesAndClipboardRenderWithoutAConnectionAndSaySo() {
        compose.setContent {
            NexusTheme {
                androidx.compose.foundation.layout.Column {
                    ClipboardPanel(enabled = false)
                    FilesScreen()
                }
            }
        }

        compose.onNodeWithText("Copy from PC").assertIsDisplayed()
        compose.onNodeWithText("Send to PC").assertIsDisplayed()
        compose.waitUntil(10_000) { runCatching { compose.onNodeWithText("Not connected to the PC.").assertExists() }.isSuccess }
    }

    private fun pendingFlag(uri: android.net.Uri): Int {
        val id = ContentUris.parseId(uri)
        context.contentResolver.query(
            MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY),
            arrayOf(MediaStore.MediaColumns.IS_PENDING),
            "${MediaStore.MediaColumns._ID} = ?",
            arrayOf(id.toString()),
            null,
        )?.use { cursor ->
            // Pending entries are only returned to their owner when asked for explicitly.
            if (cursor.moveToFirst()) return cursor.getInt(0)
        }

        val bundle = android.os.Bundle().apply {
            putString(android.content.ContentResolver.QUERY_ARG_SQL_SELECTION, "${MediaStore.MediaColumns._ID} = ?")
            putStringArray(android.content.ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, arrayOf(id.toString()))
            putInt(MediaStore.QUERY_ARG_MATCH_PENDING, MediaStore.MATCH_INCLUDE)
        }

        context.contentResolver.query(MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), arrayOf(MediaStore.MediaColumns.IS_PENDING), bundle, null)?.use { cursor ->
            if (cursor.moveToFirst()) return cursor.getInt(0)
        }

        return -1
    }
}
