using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Protocol;
using Xunit;

namespace PcCore.Tests.Protocol;

/// <summary>
/// The cross-language contract between PC Core and the Nexus Android client.
/// <para>
/// v0.5 is the first milestone where the protocol is implemented twice, in two languages, by two
/// different JSON stacks. The failures that produces are quiet ones: an HMAC over subtly different
/// bytes, a base64 variant with padding, a key order that only matters because someone compared
/// strings, a timestamp format one side cannot parse. None of them show up as compile errors, and
/// several of them look fine right up until a real phone tries to authenticate.
/// </para>
/// <para>
/// So the vectors are <b>generated here, from the real PC Core implementations</b> — not written by
/// hand and not derived from a description — committed at the repository root, and asserted against
/// by both sides. This test proves PC Core still produces them; the Kotlin
/// <c>ProtocolVectorsTest</c> proves the phone produces the same. Regenerate deliberately by
/// setting <c>PCCORE_WRITE_VECTORS=1</c>, and expect the Android tests to fail until they agree.
/// </para>
/// </summary>
public sealed class ProtocolVectorTests
{
    /// <summary>Fixed inputs. Nothing here is random, and nothing here is a real credential.</summary>
    private const string PcInstanceId = "8f14e45f-ea9c-4d2b-9a34-0f7c6b1d5e20";

    private const string DeviceId = "bYWObWzU6kQ9UMMZT3kCog";
    private const string SessionId = "Q0xxUkdQVGtnUXNCd2c1Zw";

    /// <summary>A throwaway 256-bit value that exists only in this file. Never a paired secret.</summary>
    private const string SharedSecret = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";

    private const string PairingSecret = "IB8eHRwbGhkYFxYVFBMSERAPDg0MCwoJCAcGBQQDAgE";
    private const string Challenge = "8J-Qq0hlbGxvLU5leHVzLUNoYWxsZW5nZS0wMDAxISE";

    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 29, 12, 34, 56, 789, TimeSpan.Zero);

    private static string VectorPath => Path.Combine(RepositoryRoot(), "shared", "protocol", "protocol-vectors.json");

    [Fact]
    public void TheCommittedVectorsMatchWhatPcCoreProduces()
    {
        var generated = Generate();

        if (Environment.GetEnvironmentVariable("PCCORE_WRITE_VECTORS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VectorPath)!);
            File.WriteAllText(VectorPath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.True(File.Exists(VectorPath), $"vector file missing at {VectorPath}");

        // Newline-insensitive: the file is committed on Windows and read by Gradle on any platform.
        Assert.Equal(
            generated.ReplaceLineEndings("\n"),
            File.ReadAllText(VectorPath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TheAuthProofVectorVerifiesAgainstTheRealAuthenticator()
    {
        // The vector is only worth anything if the value it pins is the one the server accepts.
        // This closes the loop on the PC side: same bytes, same secret, real HMAC.
        var message = SignedAuthMessage();
        var proof = HMACSHA256.HashData(Base64Url.DecodeFromChars(SharedSecret.AsSpan()), message);

        Assert.Equal(
            Vector("auth_proof")["proof_b64url"]!.GetValue<string>(),
            Base64Url.EncodeToString(proof));

        Assert.Equal(
            Vector("auth_proof")["signed_message_hex"]!.GetValue<string>(),
            Convert.ToHexStringLower(message));
    }

    /// <summary>
    /// The owner-session message the phone has to reproduce byte for byte, and a signature over it
    /// verified by the same code that will verify the phone's.
    /// </summary>
    [Fact]
    public void TheOwnerSessionVectorVerifiesAgainstTheRealVerifier()
    {
        var challenge = Base64Url.DecodeFromChars(Challenge.AsSpan());
        var message = OwnerSessionService.SigningMessage(PcInstanceId, DeviceId, challenge);

        Assert.Equal(
            Vector("owner_session")["signed_message_hex"]!.GetValue<string>(),
            Convert.ToHexStringLower(message));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(key.VerifyData(
            message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        // And a signature over the wrong bytes does not verify, which is the property the vector is
        // protecting.
        Assert.False(key.VerifyData(
            OwnerSessionService.SigningMessage(PcInstanceId, "another-device", challenge),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void ThePairingPayloadVectorDecodesToTheStatedFields()
    {
        var vector = Vector("pairing_payload_v2");

        Assert.True(PairingPayloadV2.TryDecode(vector["encoded"]!.GetValue<string>(), out var payload));

        Assert.Equal(PcInstanceId, payload!.PcInstanceId);
        Assert.Equal(SessionId, payload.SessionId);
        Assert.Equal(PairingSecret, payload.PairingSecret);
        Assert.Equal("100.101.102.103", payload.Host);
        Assert.Equal(47812, payload.Port);
        Assert.Equal(ProtocolConstants.CurrentVersion, payload.ProtocolVersion);
    }

    [Fact]
    public void PcCoreParsesTheUnescapedFormThatKotlinProduces()
    {
        // System.Text.Json escapes non-ASCII to \uXXXX; kotlinx.serialization emits it raw. Both
        // are valid JSON and neither side is wrong — but a phone whose requests the PC could not
        // read would be a very late discovery, so the raw form is a pinned vector the PC must
        // accept, and the escaped form is one the phone must accept.
        var vector = Vector("request_envelope_unicode");

        var decoded = ProtocolCodec.DecodeRequest(vector["encoded_raw"]!.GetValue<string>());

        Assert.True(decoded.IsSuccess);
        Assert.Equal("files.list", decoded.Request!.Action);
        Assert.Equal(
            vector["path"]!.GetValue<string>(),
            decoded.Request.Params!.Value.GetProperty("path").GetString());

        // And the two spellings are genuinely the same message.
        var escaped = ProtocolCodec.DecodeRequest(vector["encoded_escaped"]!.GetValue<string>());
        Assert.True(escaped.IsSuccess);
        Assert.Equal(
            decoded.Request.Params!.Value.GetProperty("path").GetString(),
            escaped.Request!.Params!.Value.GetProperty("path").GetString());
    }

    private static JsonNode Vector(string name) =>
        JsonNode.Parse(File.ReadAllText(VectorPath))![name]!;

    private static byte[] SignedAuthMessage()
    {
        var prefix = Encoding.UTF8.GetBytes($"nexus-auth-v1\0{PcInstanceId}\0{DeviceId}\0");
        var challenge = Base64Url.DecodeFromChars(Challenge.AsSpan());

        var message = new byte[prefix.Length + challenge.Length];
        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));
        return message;
    }

    private static byte[] SignedEnrollmentMessage()
    {
        var prefix = Encoding.UTF8.GetBytes($"nexus-pair-v1\0{PcInstanceId}\0{SessionId}\0");
        var challenge = Base64Url.DecodeFromChars(Challenge.AsSpan());

        var message = new byte[prefix.Length + challenge.Length];
        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));
        return message;
    }

    [Fact]
    public void TheStreamFrameHeaderVectorMatchesTheRealWriter()
    {
        // The binary half of the contract. A field written at the wrong offset produces a header
        // that parses cleanly on both sides and means something different on each.
        var vector = Vector("stream_frame_header");
        var expected = Convert.FromHexString(vector["header_hex"]!.GetValue<string>());

        var buffer = new byte[StreamFrameHeader.Size];
        SampleFrameHeader().WriteTo(buffer);

        Assert.Equal(expected, buffer);
        Assert.True(StreamFrameHeader.TryParse(expected, out var parsed));
        Assert.Equal(SampleFrameHeader(), parsed);
    }

    [Fact]
    public void TheInputEnvelopeVectorsMatchWhatTheDispatcherAccepts()
    {
        // Each one is decoded and bound to the real parameter type, so a vector cannot describe a
        // message PC Core would reject.
        foreach (var entry in Vector("input_envelopes").AsArray())
        {
            var action = entry!["action"]!.GetValue<string>();
            var raw = entry["encoded"]!.GetValue<string>();

            var decoded = ProtocolCodec.DecodeRequest(raw);

            Assert.True(decoded.IsSuccess, $"{action} vector did not decode");
            Assert.Equal(action, decoded.Request.Action);

            var parameterType = action switch
            {
                "input.pointer" => typeof(PointerParameters),
                "input.button" => typeof(ButtonParameters),
                "input.scroll" => typeof(ScrollParameters),
                "input.key" => typeof(KeyParameters),
                "input.text" => typeof(TextParameters),
                "input.touch" => typeof(TouchParameters),
                _ => typeof(NoParameters),
            };

            Assert.True(
                ProtocolCodec.TryBindParameters(decoded.Request.Params, parameterType, out var bound, out var error),
                $"{action} vector did not bind: {error?.Message}");

            Assert.NotNull(bound);
        }
    }

    /// <summary>The one header both sides are pinned to. Deliberately full of awkward values.</summary>
    private static StreamFrameHeader SampleFrameHeader() => new()
    {
        Version = StreamFrameHeader.CurrentVersion,
        Encoding = FrameEncoding.Jpeg,
        Flags = FrameFlags.Complete | FrameFlags.CursorBakedIn,
        DisplayIndex = 1,
        StreamId = 0x11223344,
        Sequence = 0x0102030405060708,
        TimestampUnixMs = 1_788_000_000_123,
        Width = 1280,
        Height = 720,

        // Negative, because a second monitor left of the primary is exactly where an off-by-one
        // sign error would go unnoticed until every click landed on the wrong screen.
        DesktopLeft = -1920,
        DesktopTop = -120,
        DesktopWidth = 1920,
        DesktopHeight = 1080,
        CursorX = 640,
        CursorY = 360,
        CursorVisible = true,
        PayloadLength = 4,
    };

    /// <summary>
    /// The wire name of a key, read from the attribute the serializer uses rather than restated.
    /// </summary>
    /// <summary>
    /// The spelling a member actually goes out as, read from the attribute that pins it.
    /// <para>
    /// <c>Single()</c> rather than a fallback to the naming policy, deliberately: a protocol enum
    /// whose member is not pinned fails here. That is the check v0.7.1 was missing — an unpinned
    /// <see cref="IncidentTrigger"/> silently inherited <c>snake_case</c> and no test noticed.
    /// </para>
    /// </summary>
    private static string WireName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        typeof(TEnum).GetField(value.ToString()!)!
            .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>()
            .Single()
            .Name;

    /// <summary>
    /// One incident, fixed in every field, built from the real records the capabilities return.
    /// <para>
    /// Deliberately carries a resolved-looking mix — an end time, several evidence kinds, a
    /// timeline entry — so the vector pins more than the happy path of an empty session.
    /// </para>
    /// </summary>
    private static SecuritySession SampleSession(
        IncidentTrigger trigger = IncidentTrigger.PointerActivity) => new()
    {
        SessionId = "0123456789abcdef",
        StartedAtUtc = Timestamp,
        EndedAtUtc = Timestamp.AddMinutes(3),
        Trigger = trigger,
        Resolution = SessionResolution.Active,
        HighAlert = false,
        Timeline =
        [
            new SecurityTimelineEntry
            {
                AtUtc = Timestamp,
                Code = "session.started",
                Message = "Security session started",
            },
        ],
        Evidence =
        [
            new SecurityEvidence
            {
                EvidenceId = "ev00000000000001",
                SessionId = "0123456789abcdef",
                Kind = EvidenceKind.CameraImage,
                CreatedAtUtc = Timestamp,
                FileName = "camera-20260829-123456-789.jpg",
                ContentType = "image/jpeg",
                SizeBytes = 55_512,
                DurationMs = 0,
                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                Note = "1280x720",
            },
        ],
    };

    private static JsonObject BuildFrameHeaderVector()
    {
        var header = SampleFrameHeader();
        var buffer = new byte[StreamFrameHeader.Size];
        header.WriteTo(buffer);

        return new JsonObject
        {
            ["note"] = "64 bytes, little-endian. The payload follows immediately after.",
            ["magic"] = "NXS1",
            ["header_size"] = StreamFrameHeader.Size,
            ["version"] = header.Version,
            ["encoding"] = (int)header.Encoding,
            ["flags"] = (int)header.Flags,
            ["display_index"] = header.DisplayIndex,
            ["stream_id"] = header.StreamId,
            ["sequence"] = header.Sequence,
            ["timestamp_unix_ms"] = header.TimestampUnixMs,
            ["width"] = header.Width,
            ["height"] = header.Height,
            ["desktop_left"] = header.DesktopLeft,
            ["desktop_top"] = header.DesktopTop,
            ["desktop_width"] = header.DesktopWidth,
            ["desktop_height"] = header.DesktopHeight,
            ["cursor_x"] = header.CursorX,
            ["cursor_y"] = header.CursorY,
            ["cursor_visible"] = header.CursorVisible,
            ["payload_length"] = header.PayloadLength,
            ["header_hex"] = Convert.ToHexStringLower(buffer),
            ["payload_hex"] = "deadbeef",
            ["message_hex"] = Convert.ToHexStringLower(header.ToMessage([0xde, 0xad, 0xbe, 0xef])),
        };
    }

    private static JsonObject InputVector(string action, Dictionary<string, object?>? parameters)
    {
        var request = new RequestEnvelope
        {
            Id = $"vec-{action}",
            Action = action,
            Params = parameters is null
                ? null
                : JsonSerializer.SerializeToElement(parameters, ProtocolCodec.Options),
        };

        return new JsonObject
        {
            ["action"] = action,
            ["encoded"] = ProtocolCodec.Encode(request),
        };
    }

    private static string Generate()
    {
        var authMessage = SignedAuthMessage();
        var enrollMessage = SignedEnrollmentMessage();

        var unicodePath = @"C:\Users\Örnek\Masaüstü\Ünlü Belgeler";

        var requestParams = new Dictionary<string, object?>
        {
            ["path"] = @"C:\Users\Example\Desktop",
            ["include_hidden"] = false,
        };

        var request = new RequestEnvelope
        {
            Id = "req-0001",
            Action = "files.list",
            Params = JsonSerializer.SerializeToElement(requestParams, ProtocolCodec.Options),
        };

        var unicodeRequest = new RequestEnvelope
        {
            Id = "req-0002",
            Action = "files.list",
            Params = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?> { ["path"] = unicodePath },
                ProtocolCodec.Options),
        };

        // The real result and payload types, not look-alikes: a vector that pins a shape PC Core
        // does not actually emit is worse than no vector, because it makes the phone agree with
        // something nothing sends.
        var response = ResponseEnvelope.Ok(
            "req-0003",
            "security.get_state",
            new SecurityStateResult
            {
                State = SecurityState.Armed,
                PreviousState = SecurityState.OwnerPresent,
                SinceUtc = Timestamp,
            },
            durationMs: 3);

        var @event = new EventEnvelope
        {
            Id = "evt-0001",
            EventName = SecurityEvents.StateChanged,
            Payload = new SecurityStateChangedPayload(SecurityState.Armed, SecurityState.SecuritySession),
            TimestampUtc = Timestamp,
        };

        var vectors = new JsonObject
        {
            ["note"] = "Generated by PcCore.Tests.Protocol.ProtocolVectorTests. Do not hand-edit; "
                + "regenerate with PCCORE_WRITE_VECTORS=1 and update the Kotlin side to match.",
            ["protocol_version"] = ProtocolConstants.CurrentVersion,

            // --- base64url ------------------------------------------------------------------
            // The unpadded RFC 4648 §5 alphabet. Every length residue is covered, because the
            // padding rules are exactly where a hand-written implementation goes wrong.
            ["base64url"] = new JsonArray(
                Base64Vector([]),
                Base64Vector([0x00]),
                Base64Vector([0xff]),
                Base64Vector([0xfb, 0xff]),
                Base64Vector([0xfb, 0xef, 0xff]),
                Base64Vector([0x01, 0x02, 0x03, 0x04]),
                Base64Vector(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())),

            // --- authentication proof ---------------------------------------------------------
            ["auth_proof"] = new JsonObject
            {
                ["context"] = "nexus-auth-v1",
                ["pc_instance_id"] = PcInstanceId,
                ["device_id"] = DeviceId,
                ["challenge_b64url"] = Challenge,
                ["shared_secret_b64url"] = SharedSecret,
                ["signed_message_hex"] = Convert.ToHexStringLower(authMessage),
                ["proof_b64url"] = Base64Url.EncodeToString(
                    HMACSHA256.HashData(Base64Url.DecodeFromChars(SharedSecret.AsSpan()), authMessage)),
            },

            // --- owner-session challenge --------------------------------------------------------
            // What the phone signs with its biometric-gated Keystore key. Newline-separated rather
            // than NUL-separated like the two proofs above, and that difference is exactly the kind
            // of thing a vector exists to pin: both sides build these bytes independently, and a
            // signature over subtly different bytes fails with no clue as to why.
            ["owner_session"] = new JsonObject
            {
                ["context"] = "nexus.owner-session.v1",
                ["pc_instance_id"] = PcInstanceId,
                ["device_id"] = DeviceId,
                ["challenge_b64url"] = Challenge,
                ["signed_message_hex"] = Convert.ToHexStringLower(
                    OwnerSessionService.SigningMessage(
                        PcInstanceId, DeviceId, Base64Url.DecodeFromChars(Challenge.AsSpan()))),
            },

            // --- enrollment proof -------------------------------------------------------------
            // Note the key: the UTF-8 bytes of the pairing secret *as it appears in the code*, not
            // its decoded form. The two proofs differ in key derivation as well as context, and
            // getting that backwards is a mistake that only surfaces at pairing time.
            ["enrollment_proof"] = new JsonObject
            {
                ["context"] = RemoteEnrollmentService.Context,
                ["pc_instance_id"] = PcInstanceId,
                ["session_id"] = SessionId,
                ["challenge_b64url"] = Challenge,
                ["pairing_secret"] = PairingSecret,
                ["signed_message_hex"] = Convert.ToHexStringLower(enrollMessage),
                ["proof_b64url"] = Base64Url.EncodeToString(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(PairingSecret), enrollMessage)),
            },

            // --- NEXUS2 pairing payload -------------------------------------------------------
            ["pairing_payload_v2"] = new JsonObject
            {
                ["pc_instance_id"] = PcInstanceId,
                ["session_id"] = SessionId,
                ["pairing_secret"] = PairingSecret,
                ["host"] = "100.101.102.103",
                ["port"] = 47812,
                ["encoded"] = new PairingPayloadV2
                {
                    PcInstanceId = PcInstanceId,
                    SessionId = SessionId,
                    PairingSecret = PairingSecret,
                    Host = "100.101.102.103",
                    Port = 47812,
                }.Encode(),
                ["encoded_without_endpoint"] = new PairingPayloadV2
                {
                    PcInstanceId = PcInstanceId,
                    SessionId = SessionId,
                    PairingSecret = PairingSecret,
                }.Encode(),
            },

            ["malformed_pairing_payloads"] = new JsonArray(
                "",
                "NEXUS2.",
                "NEXUS2.@@@not-base64@@@",
                "NEXUS1." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"v":1,"pc":"x","s":"y","k":"z"}""")),
                "NEXUS3." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"v":3}""")),
                Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"v":2}"""))),

            // --- envelopes --------------------------------------------------------------------
            // --- v0.6: live screen and remote input -----------------------------------------
            ["stream_frame_header"] = BuildFrameHeaderVector(),

            ["input_envelopes"] = new JsonArray(
                InputVector("input.begin", null),
                InputVector("input.end", null),
                InputVector(
                    "input.pointer",
                    new Dictionary<string, object?>
                    {
                        ["mode"] = "ABSOLUTE",
                        ["x"] = 0.25,
                        ["y"] = 0.75,
                        ["display_index"] = 1,
                    }),
                InputVector(
                    "input.pointer",
                    new Dictionary<string, object?> { ["mode"] = "RELATIVE", ["dx"] = -12.5, ["dy"] = 7.25 }),
                InputVector(
                    "input.button",
                    new Dictionary<string, object?> { ["button"] = "RIGHT", ["action"] = "DOUBLE_CLICK" }),
                InputVector(
                    "input.scroll",
                    new Dictionary<string, object?> { ["vertical"] = -3.0, ["horizontal"] = 1.0 }),
                InputVector(
                    "input.key",
                    new Dictionary<string, object?> { ["key"] = "PAGE_DOWN", ["action"] = "PRESS" }),
                InputVector(
                    "input.key",
                    new Dictionary<string, object?> { ["key"] = "CONTROL", ["action"] = "DOWN" }),
                InputVector(
                    "input.text",
                    new Dictionary<string, object?> { ["text"] = "merhaba dünya 🙂" }),
                InputVector("input.release_all", null),

                // v0.8. Direct mode sends these; the touch phases and the contact id are the parts
                // both sides have to agree on exactly, and a negative display origin is the case a
                // hard-coded origin would get wrong.
                InputVector(
                    "input.touch",
                    new Dictionary<string, object?>
                    {
                        ["action"] = "DOWN",
                        ["x"] = 0.125,
                        ["y"] = 0.875,
                        ["display_index"] = 1,
                        ["contact_id"] = 0,
                    }),
                InputVector(
                    "input.touch",
                    new Dictionary<string, object?>
                    {
                        ["action"] = "MOVE",
                        ["x"] = 0.5,
                        ["y"] = 0.5,
                        ["display_index"] = 1,
                        ["contact_id"] = 0,
                    }),
                InputVector(
                    "input.touch",
                    new Dictionary<string, object?>
                    {
                        ["action"] = "UP",
                        ["x"] = 0.5,
                        ["y"] = 0.5,
                        ["display_index"] = 1,
                        ["contact_id"] = 0,
                    }),
                InputVector(
                    "input.touch",
                    new Dictionary<string, object?>
                    {
                        ["action"] = "CANCEL",
                        ["x"] = 0.0,
                        ["y"] = 0.0,
                        ["display_index"] = 0,
                        ["contact_id"] = 2,
                    }),
                InputVector("input.status", null)),

            // The status shape the phone decodes. Pinned because a spelling change here is how a
            // status line silently becomes blank rather than failing loudly.
            ["input_status"] = JsonSerializer.SerializeToNode(
                new NativeInputStatus
                {
                    Path = InputPath.NativeBroker,
                    State = NativeInputState.Ready,
                    ReachesElevatedWindows = true,
                    TouchAvailable = true,
                    PhysicalDigitizer = false,
                    Desktop = "DEFAULT",
                    BrokerVersion = "0.8.0",
                    Diagnostic = null,
                },
                ProtocolCodec.Options),

            ["input_touch_actions"] = new JsonArray(
                [.. Enum.GetValues<TouchAction>().Select(action => (JsonNode)JsonValue.Create(WireName(action))!)]),

            ["input_key_names"] = new JsonArray(
                [.. Enum.GetValues<NexusKey>().Select(key => (JsonNode)JsonValue.Create(WireName(key))!)]),

            ["request_envelope"] = new JsonObject
            {
                ["id"] = request.Id,
                ["action"] = request.Action,
                ["encoded"] = ProtocolCodec.Encode(request),
            },

            ["request_envelope_unicode"] = new JsonObject
            {
                ["path"] = unicodePath,
                ["encoded_escaped"] = ProtocolCodec.Encode(unicodeRequest),
                ["encoded_raw"] = Unescape(ProtocolCodec.Encode(unicodeRequest)),
            },

            ["response_envelope"] = new JsonObject
            {
                ["encoded"] = ProtocolCodec.Encode(response),
                ["id"] = response.Id,
                ["action"] = response.Action,
                ["state"] = "ARMED",
                ["previous_state"] = "OWNER_PRESENT",
                ["since_utc"] = "2026-08-29T12:34:56.789+00:00",
                ["duration_ms"] = 3,
            },

            ["error_response_envelope"] = new JsonObject
            {
                ["encoded"] = ProtocolCodec.Encode(ResponseEnvelope.Fail(
                    "req-0004", "device.unpair_primary", ErrorCodes.NotAuthorized, "Action not permitted.")),
                ["code"] = ErrorCodes.NotAuthorized,
            },

            ["event_envelope"] = new JsonObject
            {
                ["encoded"] = ProtocolCodec.Encode(@event),
                ["event"] = @event.EventName,
                ["current_state"] = "SECURITY_SESSION",
                ["previous_state"] = "ARMED",
                ["timestamp_utc"] = "2026-08-29T12:34:56.789+00:00",
            },

            ["challenge_event"] = new JsonObject
            {
                ["encoded"] = ProtocolCodec.Encode(new EventEnvelope
                {
                    Id = "evt-0002",
                    EventName = "auth.challenge",
                    Payload = new ChallengeVector(SessionId, Challenge, Timestamp),
                    TimestampUtc = Timestamp,
                }),
            },

            // --- the security incident shape ---------------------------------------------------
            // Added in v0.7.1, after a physical phone showed why it was missing. Every enum in a
            // session summary except `trigger` had its wire spelling pinned by attribute; `trigger`
            // inherited the codec's snake_case policy and so went out as "pointer_activity" while
            // the phone expected "POINTER_ACTIVITY". Nothing failed on the PC — the responses were
            // well-formed and logged as successes — but the phone could not decode any of them, so
            // the Security screen errored, the incident history stayed empty, and the alert
            // notification was silently dropped. One mis-spelled enum, three broken features, and
            // no test on either side that looked at the actual bytes.
            //
            // These vectors are the fix for that class of bug rather than that one instance: they
            // pin every enum a session summary carries, generated from the real records.
            ["security_session_summary"] = new JsonObject
            {
                ["encoded"] = JsonSerializer.Serialize(
                    SecuritySessionSummary.From(SampleSession()), ProtocolCodec.Options),
                ["session_id"] = "0123456789abcdef",
                ["trigger"] = "POINTER_ACTIVITY",
                ["resolution"] = "ACTIVE",
                ["started_at_utc"] = "2026-08-29T12:34:56.789+00:00",
            },

            ["security_session_detail"] = new JsonObject
            {
                ["encoded"] = JsonSerializer.Serialize(
                    new GetSessionResult
                    {
                        Session = SessionDetail.From(SampleSession()),
                        HasActiveSession = true,
                        State = SecurityState.SecuritySession,
                    },
                    ProtocolCodec.Options),
                ["state"] = "SECURITY_SESSION",
                ["evidence_kind"] = "CAMERA_IMAGE",
            },

            ["security_session_started_event"] = new JsonObject
            {
                ["encoded"] = ProtocolCodec.Encode(new EventEnvelope
                {
                    Id = "evt-0003",
                    EventName = SecurityEvents.SessionStarted,

                    // A different trigger from the summary vector on purpose, so the pair covers
                    // two members rather than proving one member twice.
                    Payload = SecuritySessionSummary.From(SampleSession(IncidentTrigger.KeyboardActivity)),
                    TimestampUtc = Timestamp,
                }),
                ["event"] = SecurityEvents.SessionStarted,
                ["trigger"] = "KEYBOARD_ACTIVITY",
            },

            // Every member of every enum a session summary carries, so a member added on one side
            // and forgotten on the other fails here rather than on a phone.
            ["security_enum_names"] = new JsonObject
            {
                ["incident_trigger"] = new JsonArray(
                    [.. Enum.GetValues<IncidentTrigger>().Select(v => (JsonNode)JsonValue.Create(WireName(v))!)]),
                ["session_resolution"] = new JsonArray(
                    [.. Enum.GetValues<SessionResolution>().Select(v => (JsonNode)JsonValue.Create(WireName(v))!)]),
                ["evidence_kind"] = new JsonArray(
                    [.. Enum.GetValues<EvidenceKind>().Select(v => (JsonNode)JsonValue.Create(WireName(v))!)]),
                ["security_state"] = new JsonArray(
                    [.. Enum.GetValues<SecurityState>().Select(v => (JsonNode)JsonValue.Create(WireName(v))!)]),
            },
        };

        return JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static JsonObject Base64Vector(byte[] bytes) => new()
    {
        ["bytes_hex"] = Convert.ToHexStringLower(bytes),
        ["encoded"] = Base64Url.EncodeToString(bytes),
    };

    /// <summary>
    /// Rewrites <c>\uXXXX</c> escapes as literal characters, producing the spelling
    /// kotlinx.serialization emits for the same message.
    /// </summary>
    private static string Unescape(string json)
    {
        var builder = new StringBuilder(json.Length);

        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] == '\\' && i + 5 < json.Length && json[i + 1] == 'u')
            {
                builder.Append((char)Convert.ToUInt16(json.Substring(i + 2, 4), 16));
                i += 5;
                continue;
            }

            builder.Append(json[i]);
        }

        return builder.ToString();
    }

    private sealed record ChallengeVector(string ChallengeId, string Challenge, DateTimeOffset ExpiresAtUtc);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PcCore.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not locate the repository root from the test output directory");
    }
}
