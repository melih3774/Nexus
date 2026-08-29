package dev.nexus.control.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * A decoded `NEXUS2.…` pairing code.
 *
 * This is the one place untrusted text becomes structured data: the bytes come from a camera, or
 * from the clipboard, and nothing about them is trustworthy until every field has been checked.
 * Decoding therefore never throws — a bad scan is an ordinary outcome, not an exception — and it
 * never yields a partially valid object.
 */
data class PairingCode(
    val pcInstanceId: String,
    val sessionId: String,
    val pairingSecret: String,
    val host: String?,
    val port: Int,
    val protocolVersion: Int,
) {
    /**
     * The endpoint this code points at, validated. Absent for a code created while PC Core had no
     * Tailscale address bound — a real and expected case, and one the UI explains rather than
     * reporting as a failure to scan.
     */
    fun endpoint(): Result<NexusEndpoint> = NexusEndpoint.parse(host, port)

    companion object {
        const val PREFIX_V2 = "NEXUS2."
        const val PREFIX_V1 = "NEXUS1."
        const val VERSION = 2

        private val json = Json { ignoreUnknownKeys = true; isLenient = false }

        /** Decodes a code, or explains why it could not be used. */
        fun parse(raw: String?): Result<PairingCode> {
            val value = raw?.trim().orEmpty()

            if (value.isEmpty()) {
                return Result.failure(Rejection.Empty)
            }

            if (value.startsWith(PREFIX_V1)) {
                // A real code, for a flow this app does not implement: v1 carries no endpoint, so a
                // phone has no way to reach the PC. Saying so beats "invalid code".
                return Result.failure(Rejection.OldFormat)
            }

            if (!value.startsWith(PREFIX_V2)) {
                return Result.failure(Rejection.NotAPairingCode)
            }

            val decoded = try {
                Base64Url.decode(value.substring(PREFIX_V2.length))
            } catch (_: IllegalArgumentException) {
                return Result.failure(Rejection.Corrupt)
            }

            val wire = try {
                json.decodeFromString(Wire.serializer(), decoded.toString(Charsets.UTF_8))
            } catch (_: Exception) {
                return Result.failure(Rejection.Corrupt)
            }

            if (wire.version != VERSION) {
                return Result.failure(Rejection.UnsupportedVersion(wire.version))
            }

            if (wire.pcInstanceId.isNullOrBlank() ||
                wire.sessionId.isNullOrBlank() ||
                wire.pairingSecret.isNullOrBlank()
            ) {
                return Result.failure(Rejection.Corrupt)
            }

            if (wire.protocolVersion != NexusProtocol.VERSION) {
                return Result.failure(Rejection.ProtocolMismatch(wire.protocolVersion))
            }

            return Result.success(
                PairingCode(
                    pcInstanceId = wire.pcInstanceId,
                    sessionId = wire.sessionId,
                    pairingSecret = wire.pairingSecret,
                    host = wire.host?.takeIf { it.isNotBlank() },
                    port = wire.port,
                    protocolVersion = wire.protocolVersion,
                ),
            )
        }
    }

    /** Compact field names, fixed to match `PairingPayloadV2` on the PC side. */
    @Serializable
    private data class Wire(
        @SerialName("v") val version: Int = 0,
        @SerialName("pc") val pcInstanceId: String? = null,
        @SerialName("s") val sessionId: String? = null,
        @SerialName("k") val pairingSecret: String? = null,
        @SerialName("h") val host: String? = null,
        @SerialName("p") val port: Int = 0,
        @SerialName("pv") val protocolVersion: Int = 0,
    )

    sealed class Rejection(message: String) : Exception(message) {
        data object Empty : Rejection("Nothing to read.")

        data object NotAPairingCode :
            Rejection("That is not a Nexus pairing code. Start pairing on the PC and scan the code it shows.")

        data object OldFormat :
            Rejection("That is an older pairing code with no PC address in it. Create a new one on the PC.")

        data object Corrupt :
            Rejection("The pairing code is damaged. Try scanning it again.")

        data class UnsupportedVersion(val version: Int) :
            Rejection("This pairing code is version $version, which this app does not understand.")

        data class ProtocolMismatch(val version: Int) :
            Rejection("The PC speaks Nexus protocol $version and this app speaks ${NexusProtocol.VERSION}. Update whichever is older.")
    }
}
