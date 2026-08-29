package dev.nexus.control.protocol

import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Unpadded base64url (RFC 4648 §5) — the encoding PC Core uses for every binary value on the wire.
 *
 * `java.util.Base64` rather than `android.util.Base64`, so the same code runs in ordinary JVM unit
 * tests where the Android framework classes are stubs. Its URL decoder accepts input with or
 * without padding, which is what PC Core's `Base64Url` produces.
 */
object Base64Url {
    private val encoder: Base64.Encoder = Base64.getUrlEncoder().withoutPadding()
    private val decoder: Base64.Decoder = Base64.getUrlDecoder()

    fun encode(bytes: ByteArray): String = encoder.encodeToString(bytes)

    /** @throws IllegalArgumentException if [value] is not valid base64url. */
    fun decode(value: String): ByteArray = decoder.decode(value)

    fun decodeOrNull(value: String?): ByteArray? = try {
        if (value == null) null else decoder.decode(value)
    } catch (_: IllegalArgumentException) {
        null
    }
}

/**
 * The two HMAC-SHA-256 proofs, byte for byte as PC Core builds and verifies them.
 *
 * Both messages are `context`, `pcInstanceId` and a scope value joined by NUL bytes, followed by
 * the raw challenge. The differences between the two are deliberate and load-bearing:
 *
 * - **Context string.** `nexus-auth-v1` versus `nexus-pair-v1`, so a proof made for one purpose can
 *   never be replayed as the other.
 * - **Scope.** The device id for authentication; the pairing session id for enrollment.
 * - **Key.** Authentication is keyed by the *decoded* 256-bit shared secret. Enrollment is keyed by
 *   the *UTF-8 text* of the pairing secret exactly as it appears in the pairing code, because that
 *   is the form PC Core stored when it created the session. Decoding it first is the single most
 *   plausible way to get this wrong, which is why both are pinned by shared test vectors.
 */
object NexusCrypto {

    /**
     * The separator PC Core uses. Written as a code point rather than an escape in a string
     * literal: a raw NUL in a source file is the kind of character that survives one editor and not
     * the next, and this value is the difference between a valid proof and a rejected one.
     */
    private val NUL: String = Char(0).toString()

    fun buildAuthMessage(pcInstanceId: String, deviceId: String, challenge: ByteArray): ByteArray =
        message(NexusProtocol.Context.AUTH, pcInstanceId, deviceId, challenge)

    fun buildEnrollmentMessage(pcInstanceId: String, sessionId: String, challenge: ByteArray): ByteArray =
        message(NexusProtocol.Context.PAIRING, pcInstanceId, sessionId, challenge)

    /** @param sharedSecretB64Url the permanent credential, base64url as PC Core issued it. */
    fun signAuth(
        pcInstanceId: String,
        deviceId: String,
        sharedSecretB64Url: String,
        challengeB64Url: String,
    ): String = Base64Url.encode(
        hmacSha256(
            key = Base64Url.decode(sharedSecretB64Url),
            message = buildAuthMessage(pcInstanceId, deviceId, Base64Url.decode(challengeB64Url)),
        ),
    )

    /** @param pairingSecret the short-lived secret, as text, exactly as it appears in the code. */
    fun signEnrollment(
        pcInstanceId: String,
        sessionId: String,
        pairingSecret: String,
        challengeB64Url: String,
    ): String = Base64Url.encode(
        hmacSha256(
            key = pairingSecret.toByteArray(Charsets.UTF_8),
            message = buildEnrollmentMessage(pcInstanceId, sessionId, Base64Url.decode(challengeB64Url)),
        ),
    )

    fun hmacSha256(key: ByteArray, message: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(message)
        }

    private fun message(
        context: String,
        pcInstanceId: String,
        scope: String,
        challenge: ByteArray,
    ): ByteArray =
        (context + NUL + pcInstanceId + NUL + scope + NUL).toByteArray(Charsets.UTF_8) + challenge
}
