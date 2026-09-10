package dev.nexus.control.security

import android.util.Log
import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.protocol.Base64Url
import dev.nexus.control.protocol.NexusCrypto
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.OwnerKeyRegistration
import dev.nexus.control.protocol.OwnerSessionGrant
import dev.nexus.control.protocol.OwnerSessionStatus
import kotlinx.serialization.DeserializationStrategy
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

/** What happened when the phone tried to open or resume a Full Remote Session. */
sealed interface OwnerSessionResult {
    data object Authorized : OwnerSessionResult

    /** The Keystore key needs a fresh prompt before it will sign. */
    data object NeedsAuthentication : OwnerSessionResult

    /**
     * The key was invalidated by a change to the phone's screen lock, and the PC still holds the old
     * one. The owner has to clear it at the PC — which is the point.
     */
    data object KeyRejected : OwnerSessionResult

    data class Failed(val message: String) : OwnerSessionResult
}

/**
 * The phone's half of the owner-session handshake.
 *
 * **What it is separate from.** Device authentication happens in [dev.nexus.control.net.PcConnectionManager]
 * during the socket handshake, because nothing can be sent before it. This happens afterwards, over
 * the ordinary request path, and is deliberately built out of ordinary requests: it is a
 * conversation between an authenticated phone and its PC, not part of the transport.
 *
 * **The ticket is the reason this feels usable.** Signing needs the biometric-gated key; redeeming a
 * ticket does not. So a reconnect — which on a phone happens constantly — costs one request, and
 * only a cold start, a locked phone, or a long absence costs a fingerprint. The ticket is held in
 * memory by [OwnerSessionGate] and written nowhere, so process death loses it, which is exactly the
 * behaviour that makes "cold launch asks for the owner" true rather than merely intended.
 */
class OwnerSessionClient(
    private val channel: NexusCommandChannel,
    private val keys: OwnerKeyStore,
) {

    /** Redeems a ticket. Cheap, and asks the owner for nothing. */
    suspend fun resume(ticket: String): Pair<OwnerSessionResult, String?> = try {
        val grant = call(
            NexusProtocol.Actions.SESSION_RESUME,
            buildJsonObject { put("ticket", JsonPrimitive(ticket)) },
            OwnerSessionGrant.serializer(),
        )

        if (grant.authorized) OwnerSessionResult.Authorized to grant.ticket else refused() to null
    } catch (e: NexusRequestException) {
        // An expired or withdrawn ticket is not an error worth showing; it means authenticate again.
        OwnerSessionResult.NeedsAuthentication to null
    } catch (e: Exception) {
        OwnerSessionResult.Failed(unreachable(e)) to null
    }

    /**
     * Registers the key if the PC has not seen it, then signs a challenge and opens the session.
     *
     * Called only after the owner has satisfied the system prompt, so the Keystore key is inside its
     * validity window and will sign without prompting again.
     */
    suspend fun authorize(pcInstanceId: String, deviceId: String): Pair<OwnerSessionResult, String?> {
        return try {
            ensureKeyRegistered()?.let { return it to null }
            sign(pcInstanceId, deviceId)
        } catch (e: NexusRequestException) {
            OwnerSessionResult.Failed(described(e)) to null
        } catch (e: Exception) {
            OwnerSessionResult.Failed(unreachable(e)) to null
        }
    }

    private suspend fun sign(pcInstanceId: String, deviceId: String): Pair<OwnerSessionResult, String?> {
        val challenge = call(
            NexusProtocol.Actions.SESSION_CHALLENGE,
            null,
            dev.nexus.control.protocol.AuthChallenge.serializer(),
        )

        val message = NexusCrypto.buildOwnerSessionMessage(
            pcInstanceId, deviceId, Base64Url.decode(challenge.challenge),
        )

        return when (val signature = keys.sign(message)) {
            is OwnerSignature.Signed -> {
                val grant = call(
                    NexusProtocol.Actions.SESSION_AUTHORIZE,
                    buildJsonObject {
                        put("challenge_id", JsonPrimitive(challenge.challengeId))
                        put("signature", JsonPrimitive(signature.value))
                    },
                    OwnerSessionGrant.serializer(),
                )

                if (grant.authorized) OwnerSessionResult.Authorized to grant.ticket else refused() to null
            }

            OwnerSignature.NeedsAuthentication -> OwnerSessionResult.NeedsAuthentication to null
            OwnerSignature.Invalidated -> OwnerSessionResult.KeyRejected to null
            is OwnerSignature.Unavailable -> OwnerSessionResult.Failed(signature.reason) to null
        }
    }

    /** Tells the PC to withdraw this phone's authority now rather than when the window elapses. */
    suspend fun lock() {
        try {
            channel.request(NexusProtocol.Actions.SESSION_LOCK, null)
        } catch (e: Exception) {
            // Best effort by design. The PC expires the session on its own, and a phone that could
            // not say "I locked" must still lock.
            Log.d(TAG, "The PC could not be told about the lock: ${e.javaClass.simpleName}")
        }
    }

    suspend fun status(): OwnerSessionStatus? = try {
        call(NexusProtocol.Actions.SESSION_STATUS, null, OwnerSessionStatus.serializer())
    } catch (e: Exception) {
        null
    }

    /** Returns null when the key is registered, or the failure that stopped it. */
    private suspend fun ensureKeyRegistered(): OwnerSessionResult? {
        val publicKey = keys.publicKey() ?: return OwnerSessionResult.Failed(
            "This phone has no screen lock, so Nexus cannot prove who is holding it. " +
                "Set a PIN, pattern or biometric in Android settings.",
        )

        val registration = call(
            NexusProtocol.Actions.SESSION_ENROLL_KEY,
            buildJsonObject { put("public_key", JsonPrimitive(publicKey)) },
            OwnerKeyRegistration.serializer(),
        )

        return if (registration.registered) null else OwnerSessionResult.KeyRejected
    }

    private fun refused() = OwnerSessionResult.Failed("The PC refused this phone's owner authentication.")

    private fun described(e: NexusRequestException): String = when (e.error.code) {
        NexusProtocol.Errors.AUTHENTICATION_FAILED ->
            "The PC did not accept this phone's owner authentication."

        NexusProtocol.Errors.NOT_AUTHORIZED ->
            "The PC has not authorised this session."

        else -> "The PC refused the request (${e.error.code})."
    }

    private fun unreachable(e: Exception): String {
        Log.d(TAG, "Owner session step failed: ${e.javaClass.simpleName}")
        return "The PC could not be reached."
    }

    private suspend fun <T> call(
        action: String,
        params: JsonObject?,
        serializer: DeserializationStrategy<T>,
    ): T {
        val result: JsonElement = channel.request(action, params)
            ?: throw IllegalStateException("$action returned no result")

        return NexusJson.decodeFromJsonElement(serializer, result)
    }

    private companion object {
        const val TAG = "NexusOwnerSession"
    }
}
