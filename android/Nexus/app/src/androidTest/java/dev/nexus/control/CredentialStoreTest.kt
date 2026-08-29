package dev.nexus.control

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.nexus.control.data.CredentialStore
import dev.nexus.control.data.NexusCredential
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * The credential at rest, against the real Android Keystore.
 *
 * This has to be an instrumented test: the Keystore is a device service, and a JVM stub would prove
 * nothing about the thing that actually protects the secret. The property being checked is not "the
 * code calls the crypto API" but "the bytes on disk are useless" — so the file itself is read back
 * and searched.
 */
@RunWith(AndroidJUnit4::class)
class CredentialStoreTest {

    /** Disposable values. Nothing here is, or has ever been, a real paired credential. */
    private val secret = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA"

    private val credential = NexusCredential(
        pcInstanceId = "8f14e45f-ea9c-4d2b-9a34-0f7c6b1d5e20",
        deviceId = "bYWObWzU6kQ9UMMZT3kCog",
        sharedSecret = secret,
        host = "100.101.102.103",
        port = 47812,
        pairedAt = "2026-08-29T12:34:56.789+00:00",
    )

    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private lateinit var store: CredentialStore

    private val file: File get() = File(context.filesDir, "nexus-credential.bin")

    @Before
    fun setUp() {
        store = CredentialStore(context)
        store.clear()
    }

    @After
    fun tearDown() {
        store.clear()
    }

    @Test
    fun savedCredentialRoundTrips() {
        store.save(credential)

        val loaded = store.load()
        assertNotNull("the credential did not survive a save and load", loaded)
        assertEquals(credential, loaded)
    }

    @Test
    fun aFreshStoreObjectReadsWhatAnotherWrote() {
        // The real case: the app is closed and reopened, so the object that reads is not the one
        // that wrote. The Keystore key has to be found again, not held in memory.
        store.save(credential)

        val reopened = CredentialStore(context).load()

        assertEquals(credential.deviceId, reopened?.deviceId)
        assertEquals(credential.sharedSecret, reopened?.sharedSecret)
    }

    @Test
    fun nothingReadableIsWrittenToDisk() {
        store.save(credential)

        val bytes = file.readBytes()
        val text = String(bytes, Charsets.ISO_8859_1)

        // Not just the secret: nothing that identifies the pairing should be legible either.
        assertFalse("the shared secret is on disk in the clear", text.contains(secret))
        assertFalse(text.contains(credential.deviceId))
        assertFalse(text.contains(credential.pcInstanceId))
        assertFalse(text.contains("shared_secret"))
        assertFalse(text.contains("100.101.102.103"))
    }

    @Test
    fun everySaveUsesAFreshIv() {
        store.save(credential)
        val first = file.readBytes()

        store.save(credential)
        val second = file.readBytes()

        // Identical plaintext, different bytes: IV reuse under GCM is a serious failure, and
        // setRandomizedEncryptionRequired is what prevents it. This is that guarantee, observed.
        assertNotEquals(
            "the same plaintext sealed twice produced identical bytes",
            first.toList(),
            second.toList(),
        )
    }

    @Test
    fun aTamperedFileFailsToOpenRatherThanDecodingToSomethingElse() {
        store.save(credential)

        val bytes = file.readBytes()
        bytes[bytes.size - 1] = (bytes[bytes.size - 1].toInt() xor 0xff).toByte()
        file.writeBytes(bytes)

        // AES-GCM authenticates the ciphertext, so a single flipped bit is a refusal, not a
        // differently-decoded credential. The app treats that as unpaired and asks to pair again.
        assertNull(store.load())
    }

    @Test
    fun aTruncatedFileIsRefused() {
        store.save(credential)

        val bytes = file.readBytes()
        file.writeBytes(bytes.copyOfRange(0, bytes.size / 2))

        assertNull(store.load())
    }

    @Test
    fun clearRemovesTheCredentialAndItsKey() {
        store.save(credential)
        assertNotNull(store.load())

        store.clear()

        assertFalse("the sealed file is still on disk", file.exists())
        assertNull(store.load())

        // And a later save still works, on a newly generated key.
        store.save(credential)
        assertEquals(credential.deviceId, store.load()?.deviceId)
    }

    @Test
    fun anAbsentCredentialIsNullRatherThanAnError() {
        assertNull(store.load())
    }

    @Test
    fun theCredentialDoesNotPrintItsSecret() {
        // Guards the accidental Log.d("$credential") and any crash report that stringifies state.
        val printed = credential.toString()

        assertFalse(printed.contains(secret))
        assertTrue(printed.contains(credential.deviceId))

        // The safe projection the UI holds carries no secret field at all.
        val described = credential.describe().toString()
        assertFalse(described.contains(secret))
    }
}
