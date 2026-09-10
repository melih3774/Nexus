package dev.nexus.control.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import dev.nexus.control.protocol.NexusEndpoint
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * What the phone keeps after pairing.
 *
 * [sharedSecret] is the permanent credential. It exists in memory only while a connection is being
 * established, is never placed in Compose state, never logged, and never rendered. Use [describe]
 * anywhere the UI needs to talk about the paired PC.
 */
@Serializable
data class NexusCredential(
    @SerialName("pc_instance_id") val pcInstanceId: String,
    @SerialName("device_id") val deviceId: String,
    @SerialName("shared_secret") val sharedSecret: String,
    val host: String,
    val port: Int,
    @SerialName("paired_at") val pairedAt: String = "",
) {
    fun endpoint(): Result<NexusEndpoint> = NexusEndpoint.parse(host, port)

    /** The safe projection: everything the UI may know about the pairing, and nothing more. */
    fun describe(): PairedPc = PairedPc(
        pcInstanceId,
        deviceId,
        "$host:$port",
        pairedAt,
        dev.nexus.control.protocol.NexusCrypto.fingerprint(sharedSecret),
    )

    /** Deliberately overridden: an accidental `Log.d("$credential")` must not print the secret. */
    override fun toString(): String = "NexusCredential(device=$deviceId, pc=$pcInstanceId, endpoint=$host:$port)"
}

/** Credential-free description of the paired PC, safe to hold in UI state. */
data class PairedPc(
    val pcInstanceId: String,
    val deviceId: String,
    val endpoint: String,
    val pairedAt: String,

    /**
     * Identifies which credential this phone holds, without being the credential. Safe for UI
     * state and for reading out loud; see `NexusCrypto.fingerprint`.
     */
    val credentialFingerprint: String,
)

/**
 * Frames the sealed credential file: `[version][ivLength][iv][ciphertext]`.
 *
 * Separated from the Keystore work so the framing can be tested as ordinary code. The Keystore
 * round trip itself needs a real device and is covered by an instrumented test.
 */
internal object SealedBlob {
    const val VERSION: Int = 1

    fun pack(iv: ByteArray, ciphertext: ByteArray): ByteArray {
        require(iv.isNotEmpty() && iv.size <= 255) { "unusable IV length: ${iv.size}" }
        return byteArrayOf(VERSION.toByte(), iv.size.toByte()) + iv + ciphertext
    }

    /** Returns null for anything that is not a blob this version wrote. */
    fun unpack(bytes: ByteArray): Pair<ByteArray, ByteArray>? {
        if (bytes.size < 3 || bytes[0].toInt() != VERSION) return null

        val ivLength = bytes[1].toInt() and 0xff
        if (ivLength == 0 || bytes.size <= 2 + ivLength) return null

        return bytes.copyOfRange(2, 2 + ivLength) to bytes.copyOfRange(2 + ivLength, bytes.size)
    }
}

/**
 * The paired credential, sealed by a key that cannot leave this device.
 *
 * The key is generated in the Android Keystore and never exported: this class holds a handle, not
 * key material, and the sealed file on disk is useless on any other device or to any other app.
 * Encryption is AES-256-GCM — authenticated, so a tampered file fails to decrypt rather than
 * decrypting to something attacker-chosen — with a fresh random IV per write, which is why
 * `setRandomizedEncryptionRequired` stays on.
 *
 * No biometric or device-credential gate is required to unwrap it. That is a deliberate choice for
 * this milestone: the app reconnects on its own while it is open, and a prompt on every reconnect
 * would train the owner to tap through it. The screen lock still protects the app itself.
 */
class CredentialStore(context: Context) {

    private val appContext = context.applicationContext
    private val file: File get() = File(appContext.filesDir, FILE_NAME)
    private val json = Json { ignoreUnknownKeys = true }

    /** The stored credential, or null when unpaired or when the sealed file cannot be opened. */
    fun load(): NexusCredential? {
        val sealed = try {
            if (!file.exists()) return null
            file.readBytes()
        } catch (e: Exception) {
            // Class name only, like every other log statement here: this file is the one place
            // that touches the secret, so nothing it logs carries a value.
            Log.w(TAG, "Stored credential could not be read: ${e.javaClass.simpleName}")
            return null
        }

        val (iv, ciphertext) = SealedBlob.unpack(sealed) ?: run {
            Log.w(TAG, "Stored credential is not in a format this build wrote; ignoring it")
            return null
        }

        return try {
            val cipher = Cipher.getInstance(TRANSFORMATION).apply {
                init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, iv))
            }

            json.decodeFromString(
                NexusCredential.serializer(),
                cipher.doFinal(ciphertext).toString(Charsets.UTF_8),
            )
        } catch (e: Exception) {
            // A failure here means the key is gone (app data cleared, screen lock removed on some
            // devices) or the file was tampered with. Either way there is no credential to use, and
            // the owner re-pairs. Never log the exception's data, only that it happened.
            Log.w(TAG, "Stored credential could not be unsealed: ${e.javaClass.simpleName}")
            return null
        }
    }

    fun save(credential: NexusCredential) {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key()) }
        val plaintext = json.encodeToString(NexusCredential.serializer(), credential).toByteArray(Charsets.UTF_8)

        // Written to a temporary file and moved, so an interrupted write cannot leave a half
        // credential behind that would look like tampering on the next start.
        val temporary = File(appContext.filesDir, "$FILE_NAME.tmp")
        temporary.writeBytes(SealedBlob.pack(cipher.iv, cipher.doFinal(plaintext)))

        if (!temporary.renameTo(file)) {
            temporary.copyTo(file, overwrite = true)
            temporary.delete()
        }

        plaintext.fill(0)
    }

    /** Forgets the PC on this phone. The PC keeps its own record until the owner unpairs there. */
    fun clear() {
        file.delete()

        try {
            KeyStore.getInstance(PROVIDER).apply { load(null) }.deleteEntry(KEY_ALIAS)
        } catch (e: Exception) {
            Log.w(TAG, "Keystore entry could not be removed: ${e.javaClass.simpleName}")
        }
    }

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance(PROVIDER).apply { load(null) }
        (keyStore.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER).apply {
            init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .setRandomizedEncryptionRequired(true)
                    .build(),
            )
        }.generateKey()
    }

    private companion object {
        const val TAG = "NexusCredentials"
        const val PROVIDER = "AndroidKeyStore"
        const val KEY_ALIAS = "nexus.credential.v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val TAG_BITS = 128
        const val FILE_NAME = "nexus-credential.bin"
    }
}
