using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;
using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// The remote pairing-enrollment path: how a phone that holds no credential gets one.
/// <para>
/// This is the only route by which an unauthenticated peer can change PC Core's trust state, so it
/// gets the most adversarial coverage in the suite. The listener is bound to loopback for the same
/// reason as <see cref="RemoteTransportTests"/> — the machine has no Tailscale interface — and the
/// substitution is confined to the <see cref="RemoteEndpoint"/> constructor, which only the test
/// assembly can reach.
/// </para>
/// </summary>
public sealed class RemoteEnrollmentTests : IAsyncLifetime
{
    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private IPairingService _pairing = null!;
    private IPrimaryPhoneRegistry _phones = null!;
    private IPcIdentityProvider _identity = null!;
    private Uri _uri = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("remote-enroll");

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(_state.Directory);
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IDeviceEndpointProvider, NoDeviceEndpoint>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();

        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ICapabilityRegistry>(new CapabilityRegistry([]));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        var options = new RemoteTransportOptions { Port = FreePort() };
        services.AddSingleton(options);
        services.AddSingleton<RemoteSessionRegistry>();

        _services = services.BuildServiceProvider();
        _pairing = _services.GetRequiredService<IPairingService>();
        _phones = _services.GetRequiredService<IPrimaryPhoneRegistry>();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();

        _transport = new RemoteTransport(
            _services,
            options,
            _services.GetRequiredService<RemoteSessionRegistry>(),
            _services.GetRequiredService<ILoggerFactory>());

        await _transport.StartAsync(new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint"));
        _uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        _state.Dispose();
    }

    [Fact]
    public async Task APhoneWithTheLocalPairingCodeEnrollsAndReceivesACredential()
    {
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        var enrollment = await RemotePhoneClient.EnrollAsync(
            _uri, payload.PcInstanceId, payload.SessionId, payload.PairingSecret, "Owner Phone", token);

        Assert.Equal(_identity.PcInstanceId, enrollment.PcInstanceId);
        Assert.NotEmpty(enrollment.DeviceId);
        Assert.NotEmpty(enrollment.SharedSecret);

        // The PC now trusts exactly one phone, and it is this one.
        Assert.NotNull(_phones.Current);
        Assert.Equal(enrollment.DeviceId, _phones.Current!.DeviceId);
        Assert.Equal("Owner Phone", _phones.Current.DisplayName);

        // 256 bits, and the credential actually works: the enrolled phone can authenticate.
        Assert.Equal(32, Base64UrlBytes(enrollment.SharedSecret).Length);

        await using var phone = new RemotePhoneClient(enrollment.DeviceId, enrollment.SharedSecret);
        await phone.ConnectAsync(_uri, token);

        var authenticated = await phone.AuthenticateAsync(enrollment.PcInstanceId, token);
        Assert.True(authenticated.Success);
    }

    [Fact]
    public async Task EnrollmentDoesNotMakeTheConnectionADeviceSession()
    {
        // A phone leaves enrollment holding a credential and nothing else. If the socket became an
        // authenticated session, the boundary between "proved possession of a one-time pairing
        // code" and "is the trusted Primary Phone" would exist only in prose.
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
        await socket.ConnectAsync(_uri, token);

        var challenge = await ReadChallengeAsync(socket, token);

        await SendAsync(socket, Envelope("e1", RemoteProtocol.EnrollAction, new
        {
            challenge_id = challenge.Id,
            session_id = payload.SessionId,
            proof = RemotePhoneClient.SignEnrollment(
                payload.PcInstanceId, payload.SessionId, payload.PairingSecret, challenge.Value),
            display_name = "Owner Phone",
        }), token);

        var reply = await ReadAsync(socket, token);
        Assert.Contains("\"success\":true", reply, StringComparison.Ordinal);

        // The server closes as soon as it has answered; a follow-up request reaches nothing.
        await SendAsync(socket, Envelope("e2", "system.get_info", null), token);
        var after = await ReadAsync(socket, token);

        Assert.True(
            after is null || !after.Contains("\"success\":true", StringComparison.Ordinal),
            "an enrolled connection answered a capability request");

        Assert.Equal(0, _services.GetRequiredService<RemoteSessionRegistry>().Count);
    }

    [Fact]
    public async Task EnrollmentWithoutAnActivePairingSessionIsRefused()
    {
        // The decisive property: the owner starts pairing on the PC. With no live session there is
        // nothing for a phone on the tailnet to enroll against, however well-formed its request.
        var token = TestContext.Current.CancellationToken;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemotePhoneClient.EnrollAsync(
                _uri,
                _identity.PcInstanceId,
                "made-up-session",
                "made-up-secret",
                "Attacker",
                token));

        Assert.Contains(ErrorCodes.PairingFailed, refusal.Message, StringComparison.Ordinal);
        Assert.Null(_phones.Current);
    }

    [Fact]
    public async Task AWrongProofIsRefusedAndBurnsTheSession()
    {
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemotePhoneClient.EnrollAsync(
                _uri, payload.PcInstanceId, payload.SessionId, "the-wrong-secret", "Attacker", token));

        Assert.Contains(ErrorCodes.PairingFailed, wrong.Message, StringComparison.Ordinal);
        Assert.Null(_phones.Current);

        // And the code is spent, so the real phone cannot use it either: a scanned code survives
        // exactly one attempt, which is what stops it being guessed at by repetition.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemotePhoneClient.EnrollAsync(
                _uri, payload.PcInstanceId, payload.SessionId, payload.PairingSecret, "Owner Phone", token));

        Assert.Null(_phones.Current);
    }

    [Fact]
    public async Task AProofBoundToADifferentInstallationIsRefused()
    {
        // Domain separation in the signed message: a proof captured against one PC Core cannot be
        // replayed at another, even with a valid session id.
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnrollWithRawProofAsync(
                payload.SessionId,
                (challenge, _) => RemotePhoneClient.SignEnrollment(
                    "a-different-pc-instance", payload.SessionId, payload.PairingSecret, challenge),
                token));

        Assert.Contains(ErrorCodes.PairingFailed, refusal.Message, StringComparison.Ordinal);
        Assert.Null(_phones.Current);
    }

    [Fact]
    public async Task AnAuthenticationProofCannotBeReusedAsAnEnrollmentProof()
    {
        // The two proofs are keyed differently and carry different context strings. This is the
        // test that would fail if someone "simplified" them into one.
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnrollWithRawProofAsync(
                payload.SessionId,
                (challenge, _) =>
                {
                    var prefix = Encoding.UTF8.GetBytes(
                        $"nexus-auth-v1\0{payload.PcInstanceId}\0{payload.SessionId}\0");
                    var bytes = Base64UrlBytes(challenge);

                    var message = new byte[prefix.Length + bytes.Length];
                    prefix.CopyTo(message, 0);
                    bytes.CopyTo(message.AsSpan(prefix.Length));

                    return Base64UrlString(HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(payload.PairingSecret), message));
                },
                token));

        Assert.Contains(ErrorCodes.PairingFailed, refusal.Message, StringComparison.Ordinal);
        Assert.Null(_phones.Current);
    }

    [Fact]
    public async Task AReplayedEnrollmentProofIsRefused()
    {
        // The challenge is the PC's own nonce and single-use, so a captured enrollment exchange is
        // worthless to anyone who records it.
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
        await socket.ConnectAsync(_uri, token);

        var challenge = await ReadChallengeAsync(socket, token);
        var proof = RemotePhoneClient.SignEnrollment(
            payload.PcInstanceId, payload.SessionId, payload.PairingSecret, challenge.Value);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "captured", token);

        // A listener now has a valid proof, and replays it on a fresh connection.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnrollWithRawProofAsync(payload.SessionId, (_, _) => proof, token));

        Assert.Contains(ErrorCodes.PairingFailed, refusal.Message, StringComparison.Ordinal);
        Assert.Null(_phones.Current);
    }

    [Fact]
    public async Task APairedPhoneCannotBeReplacedFromTheTailnet()
    {
        // Replacement is the owner's decision, stated locally with replace_existing. A pairing
        // session started without it cannot hand the tailnet a new credential, so a phone that
        // still holds a valid code cannot re-enroll itself over a working setup.
        var token = TestContext.Current.CancellationToken;

        var first = BeginPairing();
        var original = await RemotePhoneClient.EnrollAsync(
            _uri, first.PcInstanceId, first.SessionId, first.PairingSecret, "Owner Phone", token);

        // The owner did not ask to replace anything, so Begin refuses outright.
        Assert.Throws<CapabilityException>(() => _pairing.Begin(replaceExisting: false));

        Assert.Equal(original.DeviceId, _phones.Current!.DeviceId);
    }

    [Fact]
    public async Task EnrollmentIsNotACapabilityAndIsUnreachableFromADeviceSession()
    {
        var token = TestContext.Current.CancellationToken;

        var payload = BeginPairing();
        var enrollment = await RemotePhoneClient.EnrollAsync(
            _uri, payload.PcInstanceId, payload.SessionId, payload.PairingSecret, "Owner Phone", token);

        // It is not registered, so it is not dispatchable by anyone, local or remote.
        Assert.DoesNotContain(
            _services.GetRequiredService<ICapabilityRegistry>().Actions,
            a => a.Name == RemoteProtocol.EnrollAction);

        await using var phone = new RemotePhoneClient(enrollment.DeviceId, enrollment.SharedSecret);
        await phone.ConnectAsync(_uri, token);
        Assert.True((await phone.AuthenticateAsync(enrollment.PcInstanceId, token)).Success);

        var response = await phone.SendRequestAsync(
            RemoteProtocol.EnrollAction,
            new { challenge_id = "x", session_id = "y", proof = "z" },
            token);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, response.Error!.Code);

        // The session survives the refusal — the connection is still answering — and the PC still
        // trusts the same phone. (No capabilities are registered in this fixture, so the follow-up
        // comes back as unknown_action; what matters is that it comes back at all.)
        var after = await phone.SendRequestAsync("system.get_info", null, token);
        Assert.Equal(ErrorCodes.UnknownAction, after.Error!.Code);
        Assert.Equal(1, _services.GetRequiredService<RemoteSessionRegistry>().Count);
        Assert.Equal(enrollment.DeviceId, _phones.Current!.DeviceId);
    }

    [Fact]
    public async Task TheEnrollmentResponseCarriesTheCredentialAndNothingElseAboutTheMachine()
    {
        var token = TestContext.Current.CancellationToken;
        var payload = BeginPairing();

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
        await socket.ConnectAsync(_uri, token);

        var challenge = await ReadChallengeAsync(socket, token);

        await SendAsync(socket, Envelope("e1", RemoteProtocol.EnrollAction, new
        {
            challenge_id = challenge.Id,
            session_id = payload.SessionId,
            proof = RemotePhoneClient.SignEnrollment(
                payload.PcInstanceId, payload.SessionId, payload.PairingSecret, challenge.Value),
            display_name = "Owner Phone",
        }), token);

        var reply = await ReadAsync(socket, token);
        Assert.NotNull(reply);

        // Exactly the four fields a phone needs to reconnect, and nothing describing the machine.
        Assert.Contains("\"pc_instance_id\"", reply, StringComparison.Ordinal);
        Assert.Contains("\"device_id\"", reply, StringComparison.Ordinal);
        Assert.Contains("\"shared_secret\"", reply, StringComparison.Ordinal);
        Assert.Contains("\"paired_at_utc\"", reply, StringComparison.Ordinal);

        Assert.DoesNotContain(Environment.MachineName, reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(payload.PairingSecret, reply, StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private PairingPayloadV2 BeginPairing()
    {
        var session = _pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));
        return payload!;
    }

    private sealed record Challenge(string Id, string Value);

    private async Task<InvalidOperationException> EnrollWithRawProofAsync(
        string sessionId,
        Func<string, string, string> buildProof,
        CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.Subprotocol);
        await socket.ConnectAsync(_uri, token);

        var challenge = await ReadChallengeAsync(socket, token);

        await SendAsync(socket, Envelope("e1", RemoteProtocol.EnrollAction, new
        {
            challenge_id = challenge.Id,
            session_id = sessionId,
            proof = buildProof(challenge.Value, challenge.Id),
            display_name = "Test",
        }), token);

        var reply = await ReadAsync(socket, token)
            ?? throw new InvalidOperationException("connection closed with no reply");

        using var document = global::System.Text.Json.JsonDocument.Parse(reply);

        if (document.RootElement.GetProperty("success").GetBoolean())
        {
            throw new InvalidOperationException("enrollment unexpectedly succeeded");
        }

        throw new InvalidOperationException(
            $"enrollment refused: {document.RootElement.GetProperty("error").GetProperty("code").GetString()}");
    }

    private static async Task<Challenge> ReadChallengeAsync(WebSocket socket, CancellationToken token)
    {
        var message = await ReadAsync(socket, token);
        Assert.NotNull(message);

        using var document = global::System.Text.Json.JsonDocument.Parse(message!);
        var payload = document.RootElement.GetProperty("payload");

        return new Challenge(
            payload.GetProperty("challenge_id").GetString()!,
            payload.GetProperty("challenge").GetString()!);
    }

    private static string Envelope(string id, string action, object? parameters) =>
        global::System.Text.Json.JsonSerializer.Serialize(
            new
            {
                protocol_version = ProtocolConstants.CurrentVersion,
                id,
                type = MessageTypes.Request,
                action,
                @params = parameters,
            },
            ProtocolCodec.Options);

    private static Task SendAsync(WebSocket socket, string payload, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, token);

    private static async Task<string?> ReadAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            var result = await socket.ReceiveAsync(buffer, token);
            return result.MessageType == WebSocketMessageType.Close
                ? null
                : Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlBytes(string value) =>
        global::System.Buffers.Text.Base64Url.DecodeFromChars(value.AsSpan());

    private static string Base64UrlString(byte[] value) =>
        global::System.Buffers.Text.Base64Url.EncodeToString(value);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
