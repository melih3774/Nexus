package dev.nexus.control

import dev.nexus.control.protocol.AuthChallenge
import dev.nexus.control.protocol.Base64Url
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.NexusCrypto
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.PairingCode
import dev.nexus.control.protocol.RequestEnvelope
import dev.nexus.control.protocol.ResponseEnvelope
import dev.nexus.control.protocol.SecurityState
import dev.nexus.control.protocol.SecurityStateChanged
import dev.nexus.control.protocol.SecurityStateResult
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.OffsetDateTime

/**
 * The other half of the cross-language contract.
 *
 * `PcCore.Tests.Protocol.ProtocolVectorTests` generates `shared/protocol/protocol-vectors.json`
 * from the real PC Core implementations and proves the PC still produces it. This proves the phone
 * produces the same bytes and reads the same messages. Between them, a change to either side that
 * breaks the other fails a test on the side that made it, rather than on a phone at pairing time.
 *
 * The file is read straight from the repository — the Gradle module adds `shared/protocol` as a
 * test resource directory — so there is no copy here that could go stale.
 */
class ProtocolVectorsTest {

    private val vectors: JsonObject = run {
        val stream = javaClass.getResourceAsStream("/protocol-vectors.json")
        assertNotNull(
            "protocol-vectors.json is not on the test classpath; check the resources srcDir in app/build.gradle.kts",
            stream,
        )

        Json.parseToJsonElement(stream!!.readBytes().toString(Charsets.UTF_8)).jsonObject
    }

    private fun vector(name: String) = vectors[name]!!.jsonObject

    private fun text(name: String, field: String) = vector(name)[field]!!.jsonPrimitive.content

    @Test
    fun `the vectors are for the protocol version this app speaks`() {
        assertEquals(NexusProtocol.VERSION, vectors["protocol_version"]!!.jsonPrimitive.int)
    }

    // --- base64url ------------------------------------------------------------------------------

    @Test
    fun `base64url round trips every padding case`() {
        val cases = vectors["base64url"]!!.jsonArray
        assertTrue("expected several base64 vectors", cases.size >= 5)

        for (case in cases) {
            val bytes = hex(case.jsonObject["bytes_hex"]!!.jsonPrimitive.content)
            val expected = case.jsonObject["encoded"]!!.jsonPrimitive.content

            assertEquals("encoding ${expected.ifEmpty { "<empty>" }}", expected, Base64Url.encode(bytes))
            assertArrayEqualsHex(bytes, Base64Url.decode(expected))
        }
    }

    @Test
    fun `base64url rejects the padded and standard alphabets it must not accept`() {
        // '+' and '/' belong to standard base64. Accepting them would mean a value that decodes
        // differently on the two sides, which is worse than failing.
        assertNull(Base64Url.decodeOrNull("++//"))
        assertNull(Base64Url.decodeOrNull("not base64"))
    }

    // --- proofs ---------------------------------------------------------------------------------

    @Test
    fun `the authentication proof matches PC Core byte for byte`() {
        val v = vector("auth_proof")

        val message = NexusCrypto.buildAuthMessage(
            pcInstanceId = v["pc_instance_id"]!!.jsonPrimitive.content,
            deviceId = v["device_id"]!!.jsonPrimitive.content,
            challenge = Base64Url.decode(v["challenge_b64url"]!!.jsonPrimitive.content),
        )

        assertEquals(
            "the signed message differs — the proof would be rejected",
            v["signed_message_hex"]!!.jsonPrimitive.content,
            toHex(message),
        )

        assertEquals(
            v["proof_b64url"]!!.jsonPrimitive.content,
            NexusCrypto.signAuth(
                pcInstanceId = v["pc_instance_id"]!!.jsonPrimitive.content,
                deviceId = v["device_id"]!!.jsonPrimitive.content,
                sharedSecretB64Url = v["shared_secret_b64url"]!!.jsonPrimitive.content,
                challengeB64Url = v["challenge_b64url"]!!.jsonPrimitive.content,
            ),
        )
    }

    @Test
    fun `the enrollment proof matches PC Core byte for byte`() {
        val v = vector("enrollment_proof")

        val message = NexusCrypto.buildEnrollmentMessage(
            pcInstanceId = v["pc_instance_id"]!!.jsonPrimitive.content,
            sessionId = v["session_id"]!!.jsonPrimitive.content,
            challenge = Base64Url.decode(v["challenge_b64url"]!!.jsonPrimitive.content),
        )

        assertEquals(v["signed_message_hex"]!!.jsonPrimitive.content, toHex(message))

        assertEquals(
            v["proof_b64url"]!!.jsonPrimitive.content,
            NexusCrypto.signEnrollment(
                pcInstanceId = v["pc_instance_id"]!!.jsonPrimitive.content,
                sessionId = v["session_id"]!!.jsonPrimitive.content,
                pairingSecret = v["pairing_secret"]!!.jsonPrimitive.content,
                challengeB64Url = v["challenge_b64url"]!!.jsonPrimitive.content,
            ),
        )
    }

    @Test
    fun `the two proofs are different for the same inputs`() {
        // Domain separation, checked rather than assumed. If these ever matched, a proof made to
        // authenticate could be replayed to enroll.
        val auth = vector("auth_proof")
        val enroll = vector("enrollment_proof")

        assertFalse(
            auth["proof_b64url"]!!.jsonPrimitive.content == enroll["proof_b64url"]!!.jsonPrimitive.content,
        )
        assertFalse(
            auth["signed_message_hex"]!!.jsonPrimitive.content ==
                enroll["signed_message_hex"]!!.jsonPrimitive.content,
        )
    }

    @Test
    fun `the enrollment key is the pairing secret as text, not decoded`() {
        // The single most plausible way to get enrollment wrong. Pinning the difference means a
        // "tidy-up" that decodes the secret first fails here instead of at pairing time.
        val v = vector("enrollment_proof")
        val secret = v["pairing_secret"]!!.jsonPrimitive.content

        val decodedKeyProof = Base64Url.encode(
            NexusCrypto.hmacSha256(
                key = Base64Url.decode(secret),
                message = NexusCrypto.buildEnrollmentMessage(
                    v["pc_instance_id"]!!.jsonPrimitive.content,
                    v["session_id"]!!.jsonPrimitive.content,
                    Base64Url.decode(v["challenge_b64url"]!!.jsonPrimitive.content),
                ),
            ),
        )

        assertFalse(decodedKeyProof == v["proof_b64url"]!!.jsonPrimitive.content)
    }

    // --- pairing payload -------------------------------------------------------------------------

    @Test
    fun `the NEXUS2 payload decodes to the fields PC Core encoded`() {
        val v = vector("pairing_payload_v2")
        val code = PairingCode.parse(v["encoded"]!!.jsonPrimitive.content).getOrThrow()

        assertEquals(v["pc_instance_id"]!!.jsonPrimitive.content, code.pcInstanceId)
        assertEquals(v["session_id"]!!.jsonPrimitive.content, code.sessionId)
        assertEquals(v["pairing_secret"]!!.jsonPrimitive.content, code.pairingSecret)
        assertEquals(v["host"]!!.jsonPrimitive.content, code.host)
        assertEquals(v["port"]!!.jsonPrimitive.int, code.port)
        assertEquals(NexusProtocol.VERSION, code.protocolVersion)

        val endpoint = code.endpoint().getOrThrow()
        assertEquals("ws://100.101.102.103:47812/nexus", endpoint.webSocketUrl)
    }

    @Test
    fun `a payload made without a bound endpoint parses but names no address`() {
        val code = PairingCode.parse(text("pairing_payload_v2", "encoded_without_endpoint")).getOrThrow()

        assertNull(code.host)
        assertTrue(code.endpoint().isFailure)
    }

    @Test
    fun `every malformed payload PC Core listed is rejected`() {
        for (bad in vectors["malformed_pairing_payloads"]!!.jsonArray) {
            val raw = bad.jsonPrimitive.content
            assertTrue("should have rejected: $raw", PairingCode.parse(raw).isFailure)
        }
    }

    // --- envelopes -------------------------------------------------------------------------------

    @Test
    fun `the request envelope this app encodes is byte-identical to PC Core's`() {
        val encoded = NexusJson.encodeToString(
            RequestEnvelope.serializer(),
            RequestEnvelope(
                id = text("request_envelope", "id"),
                action = text("request_envelope", "action"),
                params = buildJsonObject {
                    put("path", JsonPrimitive("""C:\Users\Example\Desktop"""))
                    put("include_hidden", JsonPrimitive(false))
                },
            ),
        )

        assertEquals(text("request_envelope", "encoded"), encoded)
    }

    @Test
    fun `each side reads the other's spelling of non-ASCII paths`() {
        // kotlinx.serialization emits non-ASCII raw; System.Text.Json escapes it. Both are valid
        // JSON, so the contract is that each side encodes its own way and parses the other's.
        val v = vector("request_envelope_unicode")
        val path = v["path"]!!.jsonPrimitive.content

        val encoded = NexusJson.encodeToString(
            RequestEnvelope.serializer(),
            RequestEnvelope(
                id = "req-0002",
                action = "files.list",
                params = buildJsonObject { put("path", JsonPrimitive(path)) },
            ),
        )

        assertEquals(v["encoded_raw"]!!.jsonPrimitive.content, encoded)

        val fromPc = NexusJson.decodeFromString(
            RequestEnvelope.serializer(),
            v["encoded_escaped"]!!.jsonPrimitive.content,
        )

        assertEquals(path, fromPc.params!!.jsonObject["path"]!!.jsonPrimitive.content)
    }

    @Test
    fun `the response envelope decodes, including its timestamp format`() {
        val response = NexusJson.decodeFromString(
            ResponseEnvelope.serializer(),
            text("response_envelope", "encoded"),
        )

        assertTrue(response.success)
        assertEquals(text("response_envelope", "id"), response.id)
        assertEquals(text("response_envelope", "action"), response.action)
        assertEquals(3L, response.durationMs)

        val result = NexusJson.decodeFromJsonElement(SecurityStateResult.serializer(), response.result!!)
        assertEquals(SecurityState.ARMED, result.securityState)
        assertEquals("OWNER_PRESENT", result.previousState)

        // PC Core writes offsets as `+00:00`, which Instant.parse rejects and OffsetDateTime accepts.
        // Pinned because the difference is invisible until a timestamp is actually parsed.
        assertEquals(2026, OffsetDateTime.parse(result.sinceUtc).year)
    }

    @Test
    fun `an error response decodes to its structured code`() {
        val response = NexusJson.decodeFromString(
            ResponseEnvelope.serializer(),
            text("error_response_envelope", "encoded"),
        )

        assertFalse(response.success)
        assertNull(response.result)
        assertEquals(NexusProtocol.Errors.NOT_AUTHORIZED, response.error!!.code)
        assertEquals(text("error_response_envelope", "code"), response.error!!.code)
    }

    @Test
    fun `the security state event decodes to both states`() {
        val event = NexusJson.decodeFromString(
            EventEnvelope.serializer(),
            text("event_envelope", "encoded"),
        )

        assertEquals(NexusProtocol.Events.SECURITY_STATE_CHANGED, event.eventName)

        val payload = NexusJson.decodeFromJsonElement(SecurityStateChanged.serializer(), event.payload!!)
        assertEquals(text("event_envelope", "current_state"), payload.currentState)
        assertEquals(text("event_envelope", "previous_state"), payload.previousState)
        assertEquals(SecurityState.SECURITY_SESSION, payload.securityState)
        assertEquals(text("event_envelope", "timestamp_utc"), event.timestampUtc)
    }

    @Test
    fun `the challenge event decodes to usable challenge material`() {
        val event = NexusJson.decodeFromString(
            EventEnvelope.serializer(),
            text("challenge_event", "encoded"),
        )

        assertEquals(NexusProtocol.Events.CHALLENGE, event.eventName)

        val challenge = NexusJson.decodeFromJsonElement(AuthChallenge.serializer(), event.payload!!)
        assertTrue(challenge.challengeId.isNotBlank())

        // The value has to survive base64url decoding, since it is HMAC input.
        assertTrue(Base64Url.decode(challenge.challenge).isNotEmpty())
    }

    @Test
    fun `an unrecognised security state maps to null rather than throwing`() {
        // Forward compatibility: a later PC Core adding a fourth state must not crash this app.
        assertNull(SecurityState.fromWire("SOMETHING_NEW"))
        assertNull(SecurityState.fromWire(null))
        assertEquals(SecurityState.ARMED, SecurityState.fromWire("ARMED"))
        assertEquals(SecurityState.OWNER_PRESENT, SecurityState.fromWire("OWNER_PRESENT"))
        assertEquals(SecurityState.SECURITY_SESSION, SecurityState.fromWire("SECURITY_SESSION"))

        // And the wire spellings are the protocol's, not Kotlin's naming.
        assertEquals("OWNER_PRESENT", SecurityState.OWNER_PRESENT.wire)
        assertEquals("SECURITY_SESSION", SecurityState.SECURITY_SESSION.wire)
    }

    // --- helpers ---------------------------------------------------------------------------------

    private fun hex(value: String): ByteArray =
        ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    private fun toHex(bytes: ByteArray): String =
        bytes.joinToString("") { "%02x".format(it) }

    private fun assertArrayEqualsHex(expected: ByteArray, actual: ByteArray) =
        assertEquals(toHex(expected), toHex(actual))

    private fun assertNull(value: Any?) = assertTrue("expected null but was $value", value == null)

    private fun assertNull(message: String, value: Any?) = assertTrue(message, value == null)
}
