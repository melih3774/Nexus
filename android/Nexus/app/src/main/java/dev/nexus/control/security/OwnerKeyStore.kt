package dev.nexus.control.security

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.UserNotAuthenticatedException
import android.util.Log
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec

/** What happened when the phone tried to prove owner presence to the PC. */
sealed interface OwnerSignature {
    data class Signed(val value: String) : OwnerSignature

    /** The key exists but Android will not use it until the owner authenticates again. */
    data object NeedsAuthentication : OwnerSignature

    /**
     * The key is gone for good — a new fingerprint was enrolled, or the screen lock was removed.
     * A new one must be generated, and the PC will refuse to register it until the owner clears the
     * old one there.
     */
    data object Invalidated : OwnerSignature

    data class Unavailable(val reason: String) : OwnerSignature
}

/**
 * The key that makes owner authentication mean something.
 *
 * **What the key is for.** The phone already proves *which device* it is with the pairing secret, on
 * every connection. That is a fact about a stored credential, and a stored credential is on the
 * phone whoever is holding it. This key proves the second thing: that the person holding the phone
 * right now satisfied the system's own authentication.
 *
 * **Why Android's guarantee is the whole point.** The private half is generated inside the Android
 * Keystore — hardware-backed on any modern phone — and cannot be read out by this app or any other.
 * It is created with `setUserAuthenticationRequired`, so the platform refuses to use it at all
 * unless the owner has authenticated within the validity window. A signature from it is therefore
 * evidence that a real biometric or device-credential prompt succeeded, produced by the part of the
 * system that actually saw it happen. Nexus never sees a fingerprint, a face or a PIN, and there is
 * nothing here it could see one with.
 *
 * **Why a fifteen-minute window rather than a prompt per use.** A phone link drops constantly, and a
 * fingerprint per reconnection would be both unusable and less safe: the reliable way to make
 * someone stop reading a prompt is to show it to them forty times a day. The window matches the
 * app's own background grace period, and the app locks itself on screen-off regardless — so the
 * window is a floor on how often the owner is asked, not the only thing asking.
 *
 * **Why the key is invalidated by new biometric enrollment.** Enrolling a new fingerprint is exactly
 * what someone who has taken the phone and coerced or guessed its PIN would do. When that happens
 * the key stops working, this app has to make a new one, and the PC refuses to register a
 * replacement until the owner clears the old one *at the PC* — somewhere a thief with the phone
 * is not.
 */
class OwnerKeyStore {

    /** The signature the PC checks, or the reason there is not one. */
    fun sign(message: ByteArray): OwnerSignature {
        val key = privateKeyOrNull() ?: return OwnerSignature.Unavailable(
            "this phone has no screen lock, so Nexus cannot prove who is holding it",
        )

        return try {
            val signature = Signature.getInstance(SIGNATURE_ALGORITHM).apply {
                initSign(key)
                update(message)
            }

            OwnerSignature.Signed(BASE64.encodeToString(signature.sign()))
        } catch (e: UserNotAuthenticatedException) {
            OwnerSignature.NeedsAuthentication
        } catch (e: KeyPermanentlyInvalidatedException) {
            // Not recoverable here on purpose. Deleting and regenerating silently would defeat the
            // protection: the point of invalidation is that the owner has to be involved again.
            Log.w(TAG, "The owner key has been invalidated by a change to this phone's screen lock.")
            OwnerSignature.Invalidated
        } catch (e: Exception) {
            Log.w(TAG, "Owner signature failed: ${e.javaClass.simpleName}")
            OwnerSignature.Unavailable("this phone could not sign the PC's challenge")
        }
    }

    /**
     * The public half, in the X.509 form PC Core imports, generating the key if it does not exist.
     * Null when the phone has no screen lock and so cannot hold an authentication-bound key.
     */
    fun publicKey(): String? {
        val entry = ensureKey() ?: return null
        return BASE64.encodeToString(entry.certificate.publicKey.encoded)
    }

    /**
     * Throws the invalidated key away so a new one can be generated.
     *
     * Only ever called after the owner has cleared the registration at the PC — otherwise the new
     * key would be refused and the app would loop generating keys nobody accepts.
     */
    fun forget() {
        try {
            keyStore().deleteEntry(ALIAS)
        } catch (e: Exception) {
            Log.w(TAG, "The owner key could not be removed: ${e.javaClass.simpleName}")
        }
    }

    private fun privateKeyOrNull(): PrivateKey? = ensureKey()?.privateKey

    private fun ensureKey(): KeyStore.PrivateKeyEntry? {
        try {
            (keyStore().getEntry(ALIAS, null) as? KeyStore.PrivateKeyEntry)?.let { return it }
        } catch (e: Exception) {
            // An unreadable entry is a gone entry. Fall through and make a new one.
            Log.w(TAG, "The stored owner key could not be read: ${e.javaClass.simpleName}")
        }

        return try {
            generate()
            keyStore().getEntry(ALIAS, null) as? KeyStore.PrivateKeyEntry
        } catch (e: Exception) {
            // The ordinary cause is a phone with no screen lock configured, which cannot hold an
            // authentication-bound key at all.
            Log.w(TAG, "The owner key could not be generated: ${e.javaClass.simpleName}")
            null
        }
    }

    private fun generate() {
        val builder = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec(CURVE))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setUserAuthenticationRequired(true)
            .setUserAuthenticationParameters(
                VALIDITY_SECONDS,
                KeyProperties.AUTH_BIOMETRIC_STRONG or KeyProperties.AUTH_DEVICE_CREDENTIAL,
            )

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            builder.setInvalidatedByBiometricEnrollment(true)
        }

        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, PROVIDER)
            .apply { initialize(builder.build()) }
            .generateKeyPair()
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance(PROVIDER).apply { load(null) }

    private companion object {
        /**
         * `java.util.Base64`, not `android.util.Base64`: the standard padded alphabet PC Core's
         * `Convert.FromBase64String` reads, and a class that exists in an ordinary JVM test run.
         */
        val BASE64: java.util.Base64.Encoder = java.util.Base64.getEncoder()

        const val TAG = "NexusOwnerKey"
        const val PROVIDER = "AndroidKeyStore"
        const val ALIAS = "nexus.owner-session.v1"
        const val CURVE = "secp256r1"
        const val SIGNATURE_ALGORITHM = "SHA256withECDSA"

        /**
         * Fifteen minutes, matching [SessionLockPolicy.BACKGROUND_GRACE_MS]. Two independent
         * mechanisms enforcing the same interval is not duplication: the app can be made to forget
         * its own timer by a bug, and the platform cannot.
         */
        const val VALIDITY_SECONDS = 15 * 60
    }
}
