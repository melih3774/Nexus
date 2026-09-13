using System.Text.Json;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Security;

/// <summary>
/// The three <c>security.*</c> actions, and their behaviour through the real dispatcher so the
/// error mapping and result serialization are exercised the way a caller sees them.
/// </summary>
public sealed class SecurityCapabilityTests
{
    private sealed record Harness(
        ICapabilityDispatcher Dispatcher,
        ISecurityStateService State,
        RecordingEventPublisher Events);

    private static Harness Build()
    {
        var events = new RecordingEventPublisher();
        var state = new SecurityStateService(
            events,
            new TempStateDirectory().Directory,
            TestLog.For<SecurityStateService>());

        var registry = new CapabilityRegistry(
        [
            new GetSecurityStateAction(state),
            new ArmSecurityAction(state),
            new DisarmSecurityAction(state),
        ]);

        return new Harness(
            new CapabilityDispatcher(registry, new DefaultCapabilityPolicy(), TestLog.For<CapabilityDispatcher>()),
            state,
            events);
    }

    private static async Task<ResponseEnvelope> Call(Harness harness, string action) =>
        await harness.Dispatcher.DispatchAsync(
            new RequestEnvelope { Id = "sec-1", Action = action },
            CallerDescriptor.Internal,
            TestContext.Current.CancellationToken);

    // --- security.get_state ---------------------------------------------------------------------

    [Fact]
    public async Task GetState_ReportsTheInitialState()
    {
        var harness = Build();

        var response = await Call(harness, "security.get_state");

        Assert.True(response.Success);
        var result = Assert.IsType<SecurityStateResult>(response.Result);
        Assert.Equal(SecurityState.OwnerPresent, result.State);
        Assert.Null(result.PreviousState);
        Assert.NotEqual(default, result.SinceUtc);
    }

    [Fact]
    public async Task GetState_ReportsPreviousStateAfterAChange()
    {
        var harness = Build();
        await Call(harness, "security.arm");

        var result = Assert.IsType<SecurityStateResult>((await Call(harness, "security.get_state")).Result);

        Assert.Equal(SecurityState.Armed, result.State);
        Assert.Equal(SecurityState.OwnerPresent, result.PreviousState);
    }

    [Fact]
    public async Task GetState_ExposesOnlyStateMetadata()
    {
        // Guards against someone later attaching internals — reasons, caller ids, config — to a
        // response any local caller can read.
        var harness = Build();

        var result = Assert.IsType<SecurityStateResult>((await Call(harness, "security.get_state")).Result);

        Assert.Equal(
            ["State", "PreviousState", "SinceUtc"],
            result.GetType().GetProperties().Select(p => p.Name));
    }

    // --- security.arm ---------------------------------------------------------------------------

    [Fact]
    public async Task Arm_MovesOwnerPresentToArmed()
    {
        var harness = Build();

        var response = await Call(harness, "security.arm");

        Assert.True(response.Success);
        var result = Assert.IsType<SecurityCommandResult>(response.Result);
        Assert.True(result.Changed);
        Assert.Equal(SecurityState.Armed, result.State);
        Assert.Equal(SecurityState.OwnerPresent, result.PreviousState);
        Assert.Equal(SecurityState.Armed, harness.State.Current.State);
    }

    [Fact]
    public async Task Arm_IsIdempotentWhenAlreadyArmed()
    {
        var harness = Build();
        await Call(harness, "security.arm");

        var response = await Call(harness, "security.arm");

        Assert.True(response.Success);
        var result = Assert.IsType<SecurityCommandResult>(response.Result);
        Assert.False(result.Changed);
        Assert.Equal(SecurityState.Armed, result.State);

        // One real change, so exactly one event.
        Assert.Single(harness.Events.Published);
    }

    [Fact]
    public async Task Arm_IsRefusedDuringASecuritySession()
    {
        var harness = Build();
        harness.State.RequestTransition(SecurityState.Armed, "test");
        harness.State.RequestTransition(SecurityState.SecuritySession, "test");

        var response = await Call(harness, "security.arm");

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidStateTransition, response.Error!.Code);

        // Refused, not redirected: the session is still running.
        Assert.Equal(SecurityState.SecuritySession, harness.State.Current.State);
    }

    // --- security.disarm ------------------------------------------------------------------------

    [Fact]
    public async Task Disarm_ReturnsToOwnerPresentFromArmed()
    {
        var harness = Build();
        await Call(harness, "security.arm");

        var result = Assert.IsType<SecurityCommandResult>((await Call(harness, "security.disarm")).Result);

        Assert.True(result.Changed);
        Assert.Equal(SecurityState.OwnerPresent, result.State);
        Assert.Equal(SecurityState.Armed, result.PreviousState);
    }

    [Fact]
    public async Task Disarm_ReturnsToOwnerPresentFromASecuritySession()
    {
        var harness = Build();
        harness.State.RequestTransition(SecurityState.Armed, "test");
        harness.State.RequestTransition(SecurityState.SecuritySession, "test");

        var response = await Call(harness, "security.disarm");

        Assert.True(response.Success);
        var result = Assert.IsType<SecurityCommandResult>(response.Result);
        Assert.True(result.Changed);
        Assert.Equal(SecurityState.OwnerPresent, result.State);
        Assert.Equal(SecurityState.SecuritySession, result.PreviousState);
    }

    [Fact]
    public async Task Disarm_IsIdempotentWhenAlreadyOwnerPresent()
    {
        var harness = Build();

        var response = await Call(harness, "security.disarm");

        Assert.True(response.Success);
        var result = Assert.IsType<SecurityCommandResult>(response.Result);
        Assert.False(result.Changed);
        Assert.Equal(SecurityState.OwnerPresent, result.State);
        Assert.Empty(harness.Events.Published);
    }

    // --- Registration ---------------------------------------------------------------------------

    [Fact]
    public void NoPublicCapabilityStartsASecuritySession()
    {
        // Escalation into SECURITY_SESSION is internal-only. If this ever fails, someone exposed a
        // way for any local caller to declare an incident.
        var harness = Build();

        Assert.All(
            new[] { "security.start_session", "security.set_state", "security.escalate" },
            action => Assert.False(
                ((CapabilityRegistry)new CapabilityRegistry(
                    [
                        new GetSecurityStateAction(harness.State),
                        new ArmSecurityAction(harness.State),
                        new DisarmSecurityAction(harness.State),
                    ])).TryGet(action, out _)));
    }
}

/// <summary>Wire-format tests: these strings are a contract that clients switch on.</summary>
public sealed class SecuritySerializationTests
{
    private static string Encode(object payload) =>
        ProtocolCodec.Encode(ResponseEnvelope.Ok("x", "security.get_state", payload));

    [Theory]
    [InlineData(SecurityState.OwnerPresent, "OWNER_PRESENT")]
    [InlineData(SecurityState.Armed, "ARMED")]
    [InlineData(SecurityState.SecuritySession, "SECURITY_SESSION")]
    public void StatesSerializeToTheirExplicitWireNames(SecurityState state, string expected)
    {
        // Pinned by attribute, not by the codec's snake_case policy: renaming the C# member must
        // never silently change the protocol.
        var json = Encode(new SecurityStateResult { State = state, SinceUtc = DateTimeOffset.UnixEpoch });

        Assert.Contains($"\"state\":\"{expected}\"", json);
    }

    [Theory]
    [InlineData(ActorType.Local, "LOCAL")]
    [InlineData(ActorType.Device, "DEVICE")]
    [InlineData(ActorType.Ai, "AI")]
    public void ActorTypesSerializeToTheirExplicitWireNames(ActorType actorType, string expected)
    {
        var json = JsonSerializer.Serialize(new { actor_type = actorType }, ProtocolCodec.Options);

        Assert.Contains($"\"actor_type\":\"{expected}\"", json);
    }

    [Fact]
    public void StateResultUsesSnakeCaseFields()
    {
        var json = Encode(new SecurityStateResult
        {
            State = SecurityState.Armed,
            PreviousState = SecurityState.OwnerPresent,
            SinceUtc = DateTimeOffset.UnixEpoch,
        });

        Assert.Contains("\"previous_state\":\"OWNER_PRESENT\"", json);
        Assert.Contains("\"since_utc\":", json);
    }

    [Fact]
    public void StateChangedEventMatchesTheDocumentedShape()
    {
        var envelope = new EventEnvelope
        {
            Id = "e1",
            EventName = SecurityEvents.StateChanged,
            Payload = new SecurityStateChangedPayload(SecurityState.OwnerPresent, SecurityState.Armed),
            TimestampUtc = DateTimeOffset.UnixEpoch,
        };

        var json = ProtocolCodec.Encode(envelope);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("event", root.GetProperty("type").GetString());
        Assert.Equal("security.state_changed", root.GetProperty("event").GetString());
        Assert.True(root.TryGetProperty("timestamp_utc", out _));

        var payload = root.GetProperty("payload");
        Assert.Equal("OWNER_PRESENT", payload.GetProperty("previous_state").GetString());
        Assert.Equal("ARMED", payload.GetProperty("current_state").GetString());
    }

    [Fact]
    public void CommandResultCarriesTheChangedFlag()
    {
        var json = ProtocolCodec.Encode(ResponseEnvelope.Ok("x", "security.arm", new SecurityCommandResult
        {
            Changed = false,
            State = SecurityState.Armed,
            PreviousState = SecurityState.OwnerPresent,
            SinceUtc = DateTimeOffset.UnixEpoch,
        }));

        Assert.Contains("\"changed\":false", json);
        Assert.Contains("\"state\":\"ARMED\"", json);
    }
}

/// <summary>Caller identity is typed metadata in v0.2; these pin the model, not any policy.</summary>
public sealed class CallerIdentityTests
{
    [Fact]
    public void InternalCallerIsALocalActorWithNoDevice()
    {
        Assert.Equal(ActorType.Local, CallerDescriptor.Internal.ActorType);
        Assert.Null(CallerDescriptor.Internal.DeviceId);
    }

    [Fact]
    public void ADeviceIdMayOnlyBeAttachedToADeviceActor()
    {
        // Stops a fabricated device id from being stamped onto an unauthenticated caller, where
        // later code could read it and conclude a pairing had happened.
        Assert.Throws<ArgumentException>(() => new CallerDescriptor("t", "c", ActorType.Local, "device-1"));
        Assert.Throws<ArgumentException>(() => new CallerDescriptor("t", "c", ActorType.Ai, "device-1"));
        Assert.Throws<ArgumentException>(() => CallerDescriptor.Internal with { DeviceId = "device-1" });

        var device = new CallerDescriptor("remote", "c", ActorType.Device, "device-1");
        Assert.Equal("device-1", device.DeviceId);
    }

    [Fact]
    public void CallerDescriptorCarriesNoTransportInternals()
    {
        // The v0.1 invariant: a capability must not be able to branch on how the caller arrived.
        // The app version and the update gate describe the client, not the transport.
        Assert.Equal(
            ["ActorType", "ClientVersionCode", "ConnectionId", "DeviceId", "RequiresUpdate", "Transport"],
            typeof(CallerDescriptor).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public void ToStringIsUsefulForLogsWithoutLeakingAnything()
    {
        Assert.Equal("loopback-http/abc (Local)", new CallerDescriptor("loopback-http", "abc", ActorType.Local).ToString());
    }
}
