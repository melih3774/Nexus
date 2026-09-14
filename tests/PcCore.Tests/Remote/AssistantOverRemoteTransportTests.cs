using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Assistant;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.System;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;

using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// PC Assistant over the real remote transport, the real policy and the real owner-session service.
/// What is proven here is who may instruct PC Assistant and who may watch it: a paired phone whose owner
/// has not authenticated reaches none of it, and a transcript is pushed only to the connection that asked
/// for it while that connection holds the owner's session.
/// </summary>
public sealed class AssistantOverRemoteTransportTests : IAsyncLifetime
{
    private static readonly string[] AssistantActions =
    [
        "assistant.status",
        "assistant.subscribe",
        "assistant.unsubscribe",
        "assistant.tasks.list",
        "assistant.tasks.get",
        "assistant.tasks.create",
        "assistant.tasks.send",
        "assistant.tasks.stop",
        "assistant.tasks.set_mode",
        "assistant.approvals.resolve",
    ];

    private readonly OwnerPhoneKey _ownerKey = new();
    private readonly FakePcAssistant _pcAssistant = new();

    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private PairingCompletion _phone = null!;
    private IPcIdentityProvider _identity = null!;
    private Uri _uri = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("remote-assistant-state");

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(_state.Directory);
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton(new ScreenOptions());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IDeviceEndpointProvider, NoDeviceEndpoint>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ISecurityStateService, SecurityStateService>();
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();

        var options = new RemoteTransportOptions { Port = FreePort() };
        services.AddSingleton(options);
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddRemoteControlDoubles();
        services.AddSingleton<RemoteStreamCoordinator>();

        // PC Assistant, composed the way the host composes it, with the pipe replaced by the scripted one.
        services.AddSingleton(new AssistantBridgeOptions { Enabled = true, ServerExecutable = @"C:\PcAssistant.Desktop.exe", MessageIntervalMilliseconds = 0 });
        services.AddSingleton<IAssistantPipeConnector>(_pcAssistant);
        services.AddSingleton<AssistantBridge>();
        services.AddSingleton<IEventAudience, AssistantEventAudience>();
        services.AddSingleton<ICapabilityAction, GetAssistantStatusAction>();
        services.AddSingleton<ICapabilityAction, SubscribeAssistantAction>();
        services.AddSingleton<ICapabilityAction, UnsubscribeAssistantAction>();
        services.AddSingleton<ICapabilityAction, ListAssistantTasksAction>();
        services.AddSingleton<ICapabilityAction, GetAssistantTaskAction>();
        services.AddSingleton<ICapabilityAction, CreateAssistantTaskAction>();
        services.AddSingleton<ICapabilityAction, SendAssistantMessageAction>();
        services.AddSingleton<ICapabilityAction, StopAssistantTaskAction>();
        services.AddSingleton<ICapabilityAction, SetAssistantModeAction>();
        services.AddSingleton<ICapabilityAction, ResolveAssistantApprovalAction>();
        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();
        services.AddSingleton<ICapabilityAction, GetSecurityStateAction>();
        services.AddSingleton<ICapabilityAction, ArmSecurityAction>();

        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        _services = services.BuildServiceProvider();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();

        var pairing = _services.GetRequiredService<IPairingService>();
        var session = pairing.Begin(replaceExisting: true);
        Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));
        _phone = pairing.Complete(payload!.SessionId, payload.PairingSecret, "Test Phone");

        _transport = new RemoteTransport(_services, options, _services.GetRequiredService<RemoteSessionRegistry>(), _services.GetRequiredService<ILoggerFactory>());
        await _transport.StartAsync(new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint"));
        _uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        await _pcAssistant.DisposeAsync();
        _ownerKey.Dispose();
        _state.Dispose();
    }

    [Fact]
    public void EveryAssistantActionNeedsTheOwnerNotJustThePhone()
    {
        foreach (var action in AssistantActions)
        {
            Assert.Contains(action, DefaultCapabilityPolicy.RemoteAllowedActions);
            Assert.DoesNotContain(action, DefaultCapabilityPolicy.ActionsWithoutOwnerAuthorization);
        }
    }

    [Fact]
    public void AnAiCallerStillReachesNothing()
    {
        var policy = new DefaultCapabilityPolicy(_services.GetRequiredService<IRemoteAuthorityProvider>());
        var ai = new CallerDescriptor("in-process", "ai", ActorType.Ai);

        foreach (var action in AssistantActions.Append("system.get_info"))
        {
            Assert.False(policy.Evaluate(action, ai).Allowed);
        }
    }

    [Fact]
    public async Task APairedPhoneWhoseOwnerHasNotAuthenticatedReachesNoneOfIt()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectUnauthorizedAsync(token);

        foreach (var action in AssistantActions)
        {
            var response = await client.SendRequestAsync(action, new
            {
                task_id = Guid.NewGuid().ToString(),
                approval_id = Guid.NewGuid().ToString(),
                mode = "auto",
                prompt = "open my email",
                text = "and reply",
                decision = "allow_once",
            }, token);

            Assert.False(response.Success, $"{action} was allowed without owner authorisation");
            Assert.Equal(ErrorCodes.NotAuthorized, response.Error!.Code);
        }

        // Refused by policy, before any part of the action ran: PC Assistant was never contacted.
        Assert.Equal(0, _pcAssistant.Connections);
    }

    [Fact]
    public async Task AnOwnerSessionStartsATaskAndWatchesItStream()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        var subscribed = Read(await Ok(client, "assistant.subscribe", null, token));
        Assert.Equal("READY", subscribed.GetProperty("status").GetProperty("state").GetString());

        var created = Read(await Ok(client, "assistant.tasks.create", new { mode = "ask", prompt = "Say hello" }, token));
        var taskId = created.GetProperty("task").GetProperty("id").GetGuid();

        await _pcAssistant.PushAsync("message.appended", new
        {
            task_id = taskId,
            message = new { id = Guid.NewGuid(), role = "assistant", text = "Hello from Claude", created_at = DateTimeOffset.UtcNow },
        });

        var message = await client.WaitForEventAsync(AssistantEvents.Message, TimeSpan.FromSeconds(10), token);
        Assert.NotNull(message);
        Assert.Equal("Hello from Claude", Read(message!.Payload).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public async Task OnlyTheSubscribedOwnerSessionReceivesTheTranscript()
    {
        var token = TestContext.Current.CancellationToken;

        await using var owner = await ConnectAsync(token);
        await Ok(owner, "assistant.subscribe", null, token);

        // A second connection from the same paired phone, whose owner session was never opened.
        await using var locked = await ConnectUnauthorizedAsync(token);

        await _pcAssistant.PushAsync("message.appended", new
        {
            task_id = Guid.NewGuid(),
            message = new { id = Guid.NewGuid(), role = "assistant", text = "private", created_at = DateTimeOffset.UtcNow },
        });

        Assert.NotNull(await owner.WaitForEventAsync(AssistantEvents.Message, TimeSpan.FromSeconds(10), token));

        // Security state still reaches a locked phone, so it can say the PC is armed; the transcript does not.
        await Ok(owner, "security.arm", null, token);
        Assert.NotNull(await locked.WaitForEventAsync("security.state_changed", TimeSpan.FromSeconds(10), token));
        Assert.DoesNotContain(locked.ReceivedEvents, e => e.EventName.StartsWith("assistant.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LockingThePhoneStopsTheTranscriptAndUnlockingMustAskAgain()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);
        await Ok(client, "assistant.subscribe", null, token);

        await Ok(client, "session.lock", null, token);

        await _pcAssistant.PushAsync("task.progress", new
        {
            task_id = Guid.NewGuid(),
            @event = new { id = Guid.NewGuid(), kind = "tool_started", title = "after the lock", created_at = DateTimeOffset.UtcNow },
        });

        await AssertNothingArrivesAsync(client, token);

        // Unlocked again: the subscription went with the session, so nothing flows until it is renewed.
        await client.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);

        await _pcAssistant.PushAsync("task.progress", new
        {
            task_id = Guid.NewGuid(),
            @event = new { id = Guid.NewGuid(), kind = "tool_started", title = "unlocked, not resubscribed", created_at = DateTimeOffset.UtcNow },
        });

        await AssertNothingArrivesAsync(client, token);

        await Ok(client, "assistant.subscribe", null, token);
        await _pcAssistant.PushAsync("task.progress", new
        {
            task_id = Guid.NewGuid(),
            @event = new { id = Guid.NewGuid(), kind = "tool_started", title = "resubscribed", created_at = DateTimeOffset.UtcNow },
        });

        Assert.NotNull(await client.WaitForEventAsync(AssistantEvents.Progress, TimeSpan.FromSeconds(10), token));
    }

    [Fact]
    public async Task AReconnectResumesTheSessionAndReopensATask()
    {
        var token = TestContext.Current.CancellationToken;
        string ticket;
        var taskId = Guid.NewGuid();

        await using (var first = await ConnectUnauthorizedAsync(token))
        {
            ticket = await first.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
            await Ok(first, "assistant.subscribe", null, token);
        }

        await using var second = await ConnectUnauthorizedAsync(token);

        // A dropped socket is not a lost session, but it is a lost subscription and a new connection.
        Assert.Equal(ErrorCodes.NotAuthorized, (await second.SendRequestAsync("assistant.tasks.get", new { task_id = taskId }, token)).Error!.Code);

        await Ok(second, "session.resume", new { ticket }, token);
        var detail = Read(await Ok(second, "assistant.tasks.get", new { task_id = taskId }, token));
        Assert.Equal(taskId, detail.GetProperty("task").GetProperty("id").GetGuid());
        Assert.Equal("hello", detail.GetProperty("messages")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task PcAssistantNotRunningIsAStateNotAFailure()
    {
        var token = TestContext.Current.CancellationToken;
        _pcAssistant.Running = false;

        await using var client = await ConnectAsync(token);

        var status = Read(await Ok(client, "assistant.status", null, token));
        Assert.Equal("NOT_RUNNING", status.GetProperty("state").GetString());
        Assert.False(status.GetProperty("can_work").GetBoolean());

        var refused = await client.SendRequestAsync("assistant.tasks.list", null, token);
        Assert.Equal(ErrorCodes.AssistantUnavailable, refused.Error!.Code);
        Assert.Equal("NOT_RUNNING", refused.Error.Details);
    }

    /// <summary>
    /// Gives a pushed event ample time to arrive, then reads everything that did. A timed-out receive
    /// would abort the test client's socket, so absence is checked by draining, not by waiting for a miss.
    /// </summary>
    private static async Task AssertNothingArrivesAsync(RemotePhoneClient client, CancellationToken token)
    {
        await Task.Delay(500, token);
        await client.SendRequestAsync("session.status", null, token);
        Assert.DoesNotContain(client.ReceivedEvents, e => e.EventName == AssistantEvents.Progress);
    }

    private async Task<RemotePhoneClient> ConnectUnauthorizedAsync(CancellationToken token)
    {
        var client = new RemotePhoneClient(_phone.DeviceId, _phone.SharedSecret);
        await client.ConnectAsync(_uri, token);
        Assert.True((await client.AuthenticateAsync(_identity.PcInstanceId, token)).Success);
        return client;
    }

    private async Task<RemotePhoneClient> ConnectAsync(CancellationToken token)
    {
        var client = await ConnectUnauthorizedAsync(token);
        await client.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
        return client;
    }

    private static async Task<object?> Ok(RemotePhoneClient client, string action, object? parameters, CancellationToken token)
    {
        var response = await client.SendRequestAsync(action, parameters, token);
        Assert.True(response.Success, $"{action} failed: {response.Error?.Code} {response.Error?.Message}");
        return response.Result;
    }

    private static JsonElement Read(object? result) => JsonSerializer.SerializeToElement(result, ProtocolCodec.Options);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
