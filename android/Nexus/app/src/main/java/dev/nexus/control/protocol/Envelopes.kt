package dev.nexus.control.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement

/**
 * The three message shapes, matching `PcCore.Protocol.Envelopes`.
 *
 * Property order is not cosmetic: kotlinx.serialization emits fields in declaration order, and
 * `protocol-vectors.json` pins the exact string PC Core produces for the same message. Reordering
 * these will fail `ProtocolVectorsTest` rather than fail silently on a phone.
 */
@Serializable
data class RequestEnvelope(
    @SerialName("protocol_version") val protocolVersion: Int = NexusProtocol.VERSION,
    val id: String,
    val type: String = NexusProtocol.MessageTypes.REQUEST,
    val action: String,
    val params: JsonElement? = null,
)

@Serializable
data class ResponseEnvelope(
    @SerialName("protocol_version") val protocolVersion: Int = NexusProtocol.VERSION,
    val id: String = "",
    val type: String = NexusProtocol.MessageTypes.RESPONSE,
    val action: String? = null,
    val success: Boolean = false,
    val result: JsonElement? = null,
    val error: ProtocolError? = null,
    @SerialName("duration_ms") val durationMs: Long = 0,
)

/** A structured failure. `message` is for people; switch on `code`. */
@Serializable
data class ProtocolError(
    val code: String,
    val message: String = "",
    val details: String? = null,
)

@Serializable
data class EventEnvelope(
    @SerialName("protocol_version") val protocolVersion: Int = NexusProtocol.VERSION,
    val id: String = "",
    val type: String = NexusProtocol.MessageTypes.EVENT,
    @SerialName("event") val eventName: String = "",
    val payload: JsonElement? = null,
    @SerialName("timestamp_utc") val timestampUtc: String = "",
)

/**
 * The single JSON configuration for everything on the wire.
 *
 * - `encodeDefaults`: `protocol_version` and `type` are constants with defaults, and PC Core
 *   requires both to be present.
 * - `explicitNulls = false`: an absent `params` is omitted rather than sent as `null`. PC Core
 *   treats the two identically, and omitting keeps requests small.
 * - `ignoreUnknownKeys`: a newer PC Core adding a field must not break an older phone. This is the
 *   same forward-compatibility stance `ProtocolCodec` takes in the other direction.
 */
val NexusJson: Json = Json {
    encodeDefaults = true
    explicitNulls = false
    ignoreUnknownKeys = true
    isLenient = false
}
