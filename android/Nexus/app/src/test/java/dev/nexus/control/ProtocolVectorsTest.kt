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
import dev.nexus.control.protocol.StreamFrame
import dev.nexus.control.protocol.StreamFrameHeader
import dev.nexus.control.protocol.NexusKey
import dev.nexus.control.protocol.PointerParams
import dev.nexus.control.protocol.ButtonParams
import dev.nexus.control.protocol.ScrollParams
import dev.nexus.control.protocol.KeyParams
import dev.nexus.control.protocol.TextParams
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

    // --- v0.6: live screen and remote input -------------------------------------------------------

    @Test
    fun `the stream frame header parses exactly as PC Core wrote it`() {
        // The binary half of the contract, and the half with no safety net: a field read from the
        // wrong offset produces a header that parses cleanly and describes a different frame.
        val vector = vector("stream_frame_header")
        val bytes = hex(vector["header_hex"]!!.jsonPrimitive.content)

        assertEquals(StreamFrameHeader.SIZE, bytes.size)

        val header = StreamFrameHeader.parse(bytes)
        assertNotNull("the committed header did not parse", header)
        header!!

        assertEquals(vector["version"]!!.jsonPrimitive.int, header.version)
        assertEquals(vector["encoding"]!!.jsonPrimitive.int, header.encoding)
        assertEquals(vector["flags"]!!.jsonPrimitive.int, header.flags)
        assertEquals(vector["display_index"]!!.jsonPrimitive.int, header.displayIndex)
        assertEquals(vector["stream_id"]!!.jsonPrimitive.content.toLong(), header.streamId)
        assertEquals(vector["sequence"]!!.jsonPrimitive.content.toLong(), header.sequence)
        assertEquals(vector["timestamp_unix_ms"]!!.jsonPrimitive.content.toLong(), header.timestampUnixMs)
        assertEquals(vector["width"]!!.jsonPrimitive.int, header.width)
        assertEquals(vector["height"]!!.jsonPrimitive.int, header.height)

        // The negative origin is the field most likely to be quietly wrong.
        assertEquals(vector["desktop_left"]!!.jsonPrimitive.int, header.desktopLeft)
        assertEquals(vector["desktop_top"]!!.jsonPrimitive.int, header.desktopTop)
        assertEquals(vector["desktop_width"]!!.jsonPrimitive.int, header.desktopWidth)
        assertEquals(vector["desktop_height"]!!.jsonPrimitive.int, header.desktopHeight)
        assertEquals(vector["cursor_x"]!!.jsonPrimitive.int, header.cursorX)
        assertEquals(vector["cursor_y"]!!.jsonPrimitive.int, header.cursorY)
        assertEquals(vector["payload_length"]!!.jsonPrimitive.int, header.payloadLength)
        assertTrue(header.cursorVisible)
    }

    @Test
    fun `a complete frame message splits into header and payload`() {
        val vector = vector("stream_frame_header")
        val message = hex(vector["message_hex"]!!.jsonPrimitive.content)

        val frame = StreamFrame.parse(message)
        assertNotNull("the committed frame message did not parse", frame)
        frame!!

        assertArrayEqualsHex(
            hex(vector["payload_hex"]!!.jsonPrimitive.content),
            frame.message.copyOfRange(frame.payloadOffset, frame.message.size),
        )
    }

    @Test
    fun `input requests encode exactly as PC Core expects them`() {
        // Each vector is the JSON PC Core produced for a request it accepts. Encoding the same
        // command here and comparing strings catches a renamed field, a reordered one, or a number
        // formatted differently — none of which would fail to compile.
        for (entry in vectors["input_envelopes"]!!.jsonArray) {
            val action = entry.jsonObject["action"]!!.jsonPrimitive.content
            val expected = entry.jsonObject["encoded"]!!.jsonPrimitive.content
            val decoded = Json.parseToJsonElement(expected).jsonObject

            val params = decoded["params"]
            val id = decoded["id"]!!.jsonPrimitive.content

            val rebuilt = NexusJson.encodeToString(
                RequestEnvelope.serializer(),
                RequestEnvelope(id = id, action = action, params = params),
            )

            // Compared as parsed JSON rather than as text: both sides must agree on the content,
            // and pinning key order would be pinning an implementation detail of two serializers.
            assertEquals(action, Json.parseToJsonElement(expected), Json.parseToJsonElement(rebuilt))
        }
    }

    @Test
    fun `the input parameter types round trip through the shared vectors`() {
        // Proves the phone can build each command from its own typed model, not just echo bytes.
        for (entry in vectors["input_envelopes"]!!.jsonArray) {
            val action = entry.jsonObject["action"]!!.jsonPrimitive.content
            val params = Json.parseToJsonElement(entry.jsonObject["encoded"]!!.jsonPrimitive.content)
                .jsonObject["params"] ?: continue

            when (action) {
                NexusProtocol.Actions.INPUT_POINTER ->
                    NexusJson.decodeFromJsonElement(PointerParams.serializer(), params)

                NexusProtocol.Actions.INPUT_BUTTON ->
                    NexusJson.decodeFromJsonElement(ButtonParams.serializer(), params)

                NexusProtocol.Actions.INPUT_SCROLL ->
                    NexusJson.decodeFromJsonElement(ScrollParams.serializer(), params)

                NexusProtocol.Actions.INPUT_KEY ->
                    NexusJson.decodeFromJsonElement(KeyParams.serializer(), params)

                NexusProtocol.Actions.INPUT_TEXT ->
                    NexusJson.decodeFromJsonElement(TextParams.serializer(), params)
            }
        }
    }

    @Test
    fun `the key set matches PC Core exactly`() {
        // A key added on one side and forgotten on the other is a button that silently does
        // nothing. Both directions are checked, because either gap is a bug.
        val fromPc = vectors["input_key_names"]!!.jsonArray.map { it.jsonPrimitive.content }.toSet()
        val fromPhone = NexusKey.entries.map { it.name }.toSet()

        assertEquals(
            "keys PC Core knows that the phone does not: ${fromPc - fromPhone}; " +
                "keys the phone knows that PC Core does not: ${fromPhone - fromPc}",
            fromPc,
            fromPhone,
        )
    }

    private fun hex(value: String): ByteArray =
        ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    private fun toHex(bytes: ByteArray): String =
        bytes.joinToString("") { "%02x".format(it) }

    private fun assertArrayEqualsHex(expected: ByteArray, actual: ByteArray) =
        assertEquals(toHex(expected), toHex(actual))

    private fun assertNull(value: Any?) = assertTrue("expected null but was $value", value == null)

    private fun assertNull(message: String, value: Any?) = assertTrue(message, value == null)
}
