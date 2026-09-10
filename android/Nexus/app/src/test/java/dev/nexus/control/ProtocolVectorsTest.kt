package dev.nexus.control

import dev.nexus.control.protocol.AuthChallenge
import dev.nexus.control.protocol.Base64Url
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.EvidenceKind
import dev.nexus.control.protocol.GetSessionResult
import dev.nexus.control.protocol.IncidentTrigger
import dev.nexus.control.protocol.SecuritySessionSummary
import dev.nexus.control.protocol.SessionResolution
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
import dev.nexus.control.protocol.TouchAction
import dev.nexus.control.protocol.TouchParams
import dev.nexus.control.protocol.NativeInputStatus
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
import org.junit.Assert.assertNotEquals
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

    /**
     * The label the two ends compare when a phone is refused.
     *
     * It is not used to authenticate anything, which is exactly why it needs a vector: nothing else
     * would ever notice the two implementations drifting apart, and the moment anyone looks at it is
     * the moment they are trying to work out why authentication failed. A fingerprint that disagreed
     * would send them chasing a mismatch that did not exist.
     */
    @Test
    fun `the credential fingerprint matches PC Core`() {
        val v = vector("credential_fingerprint")

        val fingerprint = NexusCrypto.fingerprint(v["shared_secret_b64url"]!!.jsonPrimitive.content)

        assertEquals(v["fingerprint"]!!.jsonPrimitive.content, fingerprint)

        // Never the secret, and never long enough to be mistaken for it.
        assertEquals(8, fingerprint.length)
        assertTrue(fingerprint.all { it in "0123456789abcdef" })
        assertNotEquals(v["shared_secret_b64url"]!!.jsonPrimitive.content, fingerprint)
    }

    /** A credential that cannot even be decoded says so rather than throwing into a failure screen. */
    @Test
    fun `an unusable credential fingerprints as unreadable`() {
        assertEquals("unreadable", NexusCrypto.fingerprint("not base64url!!"))
    }

    /**
     * The bytes the phone signs with its biometric-gated Keystore key.
     *
     * Newline-separated, not NUL-separated like the two HMAC proofs, and that difference is the
     * whole reason this vector exists: an ECDSA signature over subtly different bytes is refused
     * with no clue as to why, on a phone, at the moment the owner is trying to unlock their PC.
     */
    @Test
    fun `the owner-session message matches PC Core byte for byte`() {
        val v = vector("owner_session")

        val message = NexusCrypto.buildOwnerSessionMessage(
            pcInstanceId = v["pc_instance_id"]!!.jsonPrimitive.content,
            deviceId = v["device_id"]!!.jsonPrimitive.content,
            challenge = Base64Url.decode(v["challenge_b64url"]!!.jsonPrimitive.content),
        )

        assertEquals(
            "the signed message differs - the PC would refuse this phone's owner authentication",
            v["signed_message_hex"]!!.jsonPrimitive.content,
            toHex(message),
        )
    }

    /** A different device id is a different message, which is what stops one signature standing in. */
    @Test
    fun `the owner-session message is bound to the PC and the device`() {
        val v = vector("owner_session")
        val challenge = Base64Url.decode(v["challenge_b64url"]!!.jsonPrimitive.content)
        val pc = v["pc_instance_id"]!!.jsonPrimitive.content
        val device = v["device_id"]!!.jsonPrimitive.content

        assertFalse(
            toHex(NexusCrypto.buildOwnerSessionMessage(pc, "another-device", challenge)) ==
                toHex(NexusCrypto.buildOwnerSessionMessage(pc, device, challenge)),
        )

        assertFalse(
            toHex(NexusCrypto.buildOwnerSessionMessage("another-pc", device, challenge)) ==
                toHex(NexusCrypto.buildOwnerSessionMessage(pc, device, challenge)),
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

                NexusProtocol.Actions.INPUT_TOUCH ->
                    NexusJson.decodeFromJsonElement(TouchParams.serializer(), params)
            }
        }
    }

    @Test
    fun `the touch actions match PC Core exactly`() {
        val fromPc = vectors["input_touch_actions"]!!.jsonArray.map { it.jsonPrimitive.content }
        val fromPhone = TouchAction.entries.map { it.name }

        assertEquals(fromPc, fromPhone)
    }

    @Test
    fun `the input status decodes with every field the PC sends`() {
        // The status line is the one place the phone tells the owner why a click on an
        // Administrator window did nothing. A renamed field here would not fail to compile; it
        // would quietly leave the line blank, which is the worst possible way to report a boundary.
        val status = NexusJson.decodeFromJsonElement(
            NativeInputStatus.serializer(),
            vectors["input_status"]!!,
        )

        assertEquals("NATIVE_BROKER", status.path)
        assertEquals("READY", status.state)
        assertTrue("the status should reach elevated windows", status.reachesElevatedWindows)
        assertTrue("touch should be available", status.touchAvailable)
        assertFalse("this machine has no digitizer of its own", status.physicalDigitizer)
        assertFalse("the secure desktop is not up", status.secureDesktopActive)
        assertEquals("0.8.0", status.brokerVersion)
        assertTrue("a ready broker is fully native", status.isFullyNative)
        assertEquals("Native input: ready", status.label)
    }

    @Test
    fun `a status the phone cannot fully understand still reads as not native`() {
        // A PC newer than the phone must never read as "ready" by accident: the phone falls back to
        // the safe answer, which is that elevated windows may not be reachable.
        val status = NexusJson.decodeFromJsonElement(
            NativeInputStatus.serializer(),
            Json.parseToJsonElement("""{"path":"SOMETHING_NEW","state":"SOMETHING_NEW"}"""),
        )

        assertFalse("an unknown state is not fully native", status.isFullyNative)
        assertEquals("Standard input", status.label)
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

    // --- the security incident shape ------------------------------------------------------------

    /*
     * These four tests exist because of a bug a real phone found and no test did.
     *
     * PC Core answered `security.sessions.get` and `security.sessions.list` successfully, held
     * three incidents on disk with their screenshots and camera images, and published
     * `security.session_started` — and the phone showed "Something went wrong talking to the PC",
     * an empty history, and no notification. The whole cause was that `trigger` arrived as
     * "pointer_activity" while this app declared @SerialName("POINTER_ACTIVITY"), like every other
     * security enum. kotlinx.serialization throws on an unknown enum value, the Security screen
     * caught it as a generic failure, and SecurityAlertService swallowed it with
     * `runCatching{}.getOrNull()` and posted nothing.
     *
     * The existing tests all built a SecuritySessionSummary in Kotlin and read it back in Kotlin,
     * so they agreed with themselves perfectly. These decode the bytes PC Core actually emits.
     */

    @Test
    fun `a session summary from PC Core decodes`() {
        val encoded = text("security_session_summary", "encoded")
        val summary = NexusJson.decodeFromString(SecuritySessionSummary.serializer(), encoded)

        assertEquals(vector("security_session_summary")["session_id"]!!.jsonPrimitive.content, summary.sessionId)
        assertEquals(IncidentTrigger.POINTER_ACTIVITY, summary.trigger)
        assertEquals(SessionResolution.ACTIVE, summary.resolution)

        // The screen renders this directly, so a summary that decodes but has no readable
        // timestamp is still a broken history row.
        assertNotNull(OffsetDateTime.parse(summary.startedAtUtc))
    }

    @Test
    fun `a full session detail from PC Core decodes`() {
        val result = NexusJson.decodeFromString(
            GetSessionResult.serializer(),
            text("security_session_detail", "encoded"),
        )

        assertTrue("expected an active session", result.hasActiveSession)
        assertEquals(SecurityState.SECURITY_SESSION, result.securityState)

        val session = result.session
        assertNotNull("the detail carried no session", session)

        val evidence = session!!.evidence.single()
        assertEquals(EvidenceKind.CAMERA_IMAGE, evidence.kind)
        assertTrue("camera images must be viewable in this version", evidence.kind.isViewableImage)
        assertNotNull(OffsetDateTime.parse(evidence.createdAtUtc))
        assertEquals(1, session.timeline.size)
    }

    /**
     * The payload behind the alert notification.
     *
     * `SecurityAlertService` decodes exactly this and gives up quietly when it cannot, so a decode
     * failure here is a notification that never arrives rather than an error anyone sees.
     */
    @Test
    fun `the session started event payload decodes`() {
        val envelope = NexusJson.decodeFromString(
            EventEnvelope.serializer(),
            text("security_session_started_event", "encoded"),
        )

        assertEquals(NexusProtocol.Events.SECURITY_SESSION_STARTED, envelope.eventName)

        val summary = NexusJson.decodeFromJsonElement(
            SecuritySessionSummary.serializer(),
            envelope.payload!!,
        )

        assertEquals(IncidentTrigger.KEYBOARD_ACTIVITY, summary.trigger)
        assertTrue("the notification text needs a label", summary.trigger.label.isNotBlank())
    }

    @Test
    fun `the security enum members match PC Core exactly`() {
        // Both directions for each enum: a member on one side only is either a value the phone
        // cannot decode or a value it will never see, and the first of those breaks a screen.
        assertEnumMatches("incident_trigger", IncidentTrigger.entries.map { it.name })
        assertEnumMatches("session_resolution", SessionResolution.entries.map { it.name })
        assertEnumMatches("evidence_kind", EvidenceKind.entries.map { it.name })
        assertEnumMatches("security_state", SecurityState.entries.map { it.wire })
    }

    private fun assertEnumMatches(name: String, fromPhone: List<String>) {
        val fromPc = vector("security_enum_names")[name]!!.jsonArray.map { it.jsonPrimitive.content }.toSet()
        val phone = fromPhone.toSet()

        assertEquals(
            "$name: PC Core has ${fromPc - phone} that the phone does not; " +
                "the phone has ${phone - fromPc} that PC Core does not",
            fromPc,
            phone,
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
