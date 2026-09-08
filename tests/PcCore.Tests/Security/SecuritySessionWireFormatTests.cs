using System.Text.Json;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// The spelling of everything in a security incident that crosses to the phone.
/// <para>
/// <b>Why this file exists.</b> On a real paired phone the Security screen showed "Something went
/// wrong talking to the PC", the incident history was empty, and no alert notification arrived —
/// while PC Core logged <c>security.sessions.get</c> and <c>security.sessions.list</c> as
/// successes, held three sessions on disk, and had visibly captured screenshots, camera images and
/// audio for all of them. Nothing was wrong with the sessions, the storage, the policy, or the
/// transport. The responses were simply undecodable by the client.
/// </para>
/// <para>
/// The cause was one unpinned enum. <see cref="SecurityState"/>, <see cref="SessionResolution"/>
/// and <see cref="EvidenceKind"/> all pin their wire names by attribute; <see cref="IncidentTrigger"/>
/// did not, so it inherited <c>ProtocolCodec</c>'s <c>SnakeCaseLower</c> policy and went out as
/// <c>"pointer_activity"</c> where the phone — consistently with every other security enum —
/// expected <c>"POINTER_ACTIVITY"</c>. Because <c>trigger</c> is a required part of every session
/// summary, one wrong string broke the incident list, the incident detail, and the
/// <c>security.session_started</c> event payload that raises the notification.
/// </para>
/// <para>
/// The tests below assert the bytes rather than the C# values. Every pre-existing test compared
/// <c>IncidentTrigger.PointerActivity</c> to itself, which is exactly why none of them noticed.
/// </para>
/// </summary>
public sealed class SecuritySessionWireFormatTests
{
    private static SecuritySession Session(
        IncidentTrigger trigger = IncidentTrigger.PointerActivity,
        SessionResolution resolution = SessionResolution.Active) => new()
    {
        SessionId = "0123456789abcdef",
        StartedAtUtc = new DateTimeOffset(2026, 8, 30, 17, 45, 38, TimeSpan.Zero),
        Trigger = trigger,
        Resolution = resolution,
        HighAlert = false,
    };

    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, ProtocolCodec.Options);

    /// <summary>
    /// The exact failure the phone hit, at the layer it happened.
    /// <para>
    /// <c>security.sessions.get</c> is the first call the Security screen makes, and the first that
    /// carries a trigger. This is the assertion that would have failed before the fix.
    /// </para>
    /// </summary>
    [Fact]
    public void ASessionSummaryNamesItsTriggerInScreamingSnakeCase()
    {
        var json = Encode(SecuritySessionSummary.From(Session()));

        Assert.Contains("\"trigger\":\"POINTER_ACTIVITY\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pointer_activity", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(IncidentTrigger.PointerActivity, "POINTER_ACTIVITY")]
    [InlineData(IncidentTrigger.KeyboardActivity, "KEYBOARD_ACTIVITY")]
    [InlineData(IncidentTrigger.SessionUnlocked, "SESSION_UNLOCKED")]
    [InlineData(IncidentTrigger.InteractiveLogon, "INTERACTIVE_LOGON")]
    public void EveryTriggerHasThePinnedWireName(IncidentTrigger trigger, string expected)
    {
        Assert.Equal($"\"{expected}\"", Encode(trigger));
    }

    /// <summary>
    /// The whole summary, field by field, as the phone's model declares it.
    /// <para>
    /// Property names as well as enum values: the client binds by name, so a renamed C# property is
    /// the same class of breakage as a renamed enum member.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSummaryCarriesEveryFieldThePhoneBinds()
    {
        var json = Encode(SecuritySessionSummary.From(Session()));

        foreach (var field in new[]
        {
            "session_id", "started_at_utc", "ended_at_utc", "trigger", "resolution",
            "high_alert", "duration_seconds", "timeline_count", "screenshot_count",
            "camera_count", "audio_count",
        })
        {
            Assert.Contains($"\"{field}\":", json, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The payload that raises the phone's alert notification.
    /// <para>
    /// It is the same record as a history row, which is why one mis-spelled enum silenced the
    /// notification as well as emptying the history. The service decodes this payload to read
    /// <c>trigger</c> for the notification text, and a decode failure there is invisible: the alert
    /// simply never appears.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSessionStartedEventPayloadIsDecodableByThePhone()
    {
        var encoded = ProtocolCodec.Encode(new EventEnvelope
        {
            Id = "evt-0001",
            EventName = SecurityEvents.SessionStarted,
            Payload = SecuritySessionSummary.From(Session(IncidentTrigger.KeyboardActivity)),
            TimestampUtc = DateTimeOffset.UnixEpoch,
        });

        Assert.Contains("\"event\":\"security.session_started\"", encoded, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":\"KEYBOARD_ACTIVITY\"", encoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SessionResolution.Active, "ACTIVE")]
    [InlineData(SessionResolution.AcknowledgedOwner, "ACKNOWLEDGED_OWNER")]
    [InlineData(SessionResolution.ConfirmedNotOwner, "CONFIRMED_NOT_OWNER")]
    public void EveryResolutionKeepsItsPinnedWireName(SessionResolution resolution, string expected)
    {
        Assert.Equal($"\"{expected}\"", Encode(resolution));
    }

    [Theory]
    [InlineData(EvidenceKind.Screenshot, "SCREENSHOT")]
    [InlineData(EvidenceKind.ScreenRecording, "SCREEN_RECORDING")]
    [InlineData(EvidenceKind.CameraImage, "CAMERA_IMAGE")]
    [InlineData(EvidenceKind.CameraVideo, "CAMERA_VIDEO")]
    [InlineData(EvidenceKind.Audio, "AUDIO")]
    public void EveryEvidenceKindKeepsItsPinnedWireName(EvidenceKind kind, string expected)
    {
        Assert.Equal($"\"{expected}\"", Encode(kind));
    }

    /// <summary>
    /// Sessions recorded before the spelling was pinned still load.
    /// <para>
    /// This is the part of the fix that matters most on the owner's real machine. Their history file
    /// already contained <c>"trigger": "pointer_activity"</c> for every incident, and each of those
    /// sessions owns a folder of camera images, screenshots and audio. Had pinning the enum made the
    /// old file unreadable, the store would have quarantined it and started empty — losing the
    /// index for evidence that is still sitting on disk, which is precisely the outcome the bug fix
    /// is supposed to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public void HistoryWrittenBeforeTheSpellingWasPinnedStillLoads()
    {
        using var state = new TempStateDirectory("session-legacy-format");

        var securityRoot = Path.Combine(state.Path, "Security");
        Directory.CreateDirectory(securityRoot);

        // Byte-for-byte the shape the owner's own sessions.json used, lowercase trigger and all.
        File.WriteAllText(
            Path.Combine(securityRoot, "sessions.json"),
            """
            {
              "schema_version": 1,
              "sessions": [
                {
                  "session_id": "883eaf90d3094d7a",
                  "started_at_utc": "2026-08-30T17:45:38.4592167+00:00",
                  "ended_at_utc": null,
                  "trigger": "pointer_activity",
                  "resolution": "ACTIVE",
                  "high_alert": false,
                  "resolved_at_utc": null,
                  "resolved_by": null,
                  "timeline": [],
                  "evidence": []
                }
              ]
            }
            """);

        var store = new SecuritySessionStore(
            state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        var session = Assert.Single(store.All());

        Assert.Equal("883eaf90d3094d7a", session.SessionId);
        Assert.Equal(IncidentTrigger.PointerActivity, session.Trigger);
        Assert.Equal(SessionResolution.Active, session.Resolution);
    }

    /// <summary>
    /// And what it writes back is the pinned spelling, so the file converges on one format.
    /// </summary>
    [Fact]
    public void TheStoreWritesThePinnedSpelling()
    {
        using var state = new TempStateDirectory("session-write-format");

        var store = new SecuritySessionStore(
            state.Directory, new SecurityStorageOptions(), TestLog.For<SecuritySessionStore>());

        store.Save(Session(IncidentTrigger.SessionUnlocked));

        var raw = File.ReadAllText(Path.Combine(state.Path, "Security", "sessions.json"));

        Assert.Contains("SESSION_UNLOCKED", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("session_unlocked", raw, StringComparison.Ordinal);
    }
}
