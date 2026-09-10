package dev.nexus.control.security

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.GetEvidenceParams
import dev.nexus.control.protocol.GetEvidenceResult
import dev.nexus.control.protocol.GetSessionResult
import dev.nexus.control.protocol.ListSessionsResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ResolveSessionResult
import dev.nexus.control.protocol.SessionIdParams

/**
 * The phone's view of the PC's security incidents.
 *
 * Deliberately thin: five calls, each mapping to one capability. The interesting decisions —
 * whether an incident is open, what the owner is allowed to do about it — live on the PC, and
 * duplicating that judgement here would be two sources of truth about the same thing.
 */
class SecurityIncidentClient(private val connection: NexusCommandChannel) {

    suspend fun listSessions(): ListSessionsResult {
        val result = connection.request(NexusProtocol.Actions.SECURITY_SESSIONS_LIST)
            ?: return ListSessionsResult()

        return NexusJson.decodeFromJsonElement(ListSessionsResult.serializer(), result)
    }

    /** With no id, returns the open incident — which is what the Security screen asks for first. */
    suspend fun getSession(sessionId: String? = null): GetSessionResult {
        val params = sessionId?.let {
            NexusJson.encodeToJsonElement(SessionIdParams.serializer(), SessionIdParams(it))
        }

        val result = connection.request(NexusProtocol.Actions.SECURITY_SESSIONS_GET, params)
            ?: return GetSessionResult()

        return NexusJson.decodeFromJsonElement(GetSessionResult.serializer(), result)
    }

    /**
     * One chunk of one piece of evidence.
     *
     * [offset] and [maxBytes] make this a ranged read. A still image is one call; a camera clip is
     * a handful, appended to a file on the way past rather than assembled in memory.
     */
    suspend fun getEvidence(
        sessionId: String,
        evidenceId: String,
        offset: Long = 0,
        maxBytes: Int? = null,
    ): GetEvidenceResult {
        val result = connection.request(
            NexusProtocol.Actions.SECURITY_EVIDENCE_GET,
            NexusJson.encodeToJsonElement(
                GetEvidenceParams.serializer(),
                GetEvidenceParams(sessionId, evidenceId, offset, maxBytes),
            ),
        ) ?: throw IllegalStateException("The PC returned no evidence.")

        return NexusJson.decodeFromJsonElement(GetEvidenceResult.serializer(), result)
    }

    /**
     * Acknowledges the incident.
     *
     * The biometric gate that guards this lives in the view model, not here: this class is the
     * protocol, and burying an authorisation step inside a network call would make it easy to add a
     * second caller that skips it.
     */
    suspend fun thisWasMe(): ResolveSessionResult = resolve(NexusProtocol.Actions.SECURITY_THIS_WAS_ME)

    suspend fun notMe(): ResolveSessionResult = resolve(NexusProtocol.Actions.SECURITY_NOT_ME)

    private suspend fun resolve(action: String): ResolveSessionResult {
        val result = connection.request(action) ?: return ResolveSessionResult()
        return NexusJson.decodeFromJsonElement(ResolveSessionResult.serializer(), result)
    }
}
