using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;
using PcCore.Windows;
using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// Pairing survives PC Core being closed and started again.
/// <para>
/// Every other remote test builds one service provider, pairs into it, and authenticates against
/// the same live objects — so all of them pass while holding the shared secret in memory. That is
/// exactly the case that cannot fail. The interesting one is the second start: a fresh process
/// reads the record back from disk, unprotects the secret, and has to arrive at the same bytes the
/// phone was given, or the owner is asked to scan a QR code because their PC restarted.
/// </para>
/// <para>
/// These tests therefore <b>dispose the whole instance</b> — transport, service provider, registry,
/// authenticator — and build a second one over the same state directory, which is the only way the
/// persistence path is actually exercised.
/// </para>
/// </summary>
public sealed class PairingSurvivesRestartTests : IDisposable
{
    private readonly TempStateDirectory _state = new("restart-pairing");

    public void Dispose() => _state.Dispose();

    /// <summary>
    /// The reported regression, end to end: pair, authenticate, shut the PC down, start it again,
    /// and reconnect with the credential the phone already holds.
    /// </summary>
    [Fact]
    public async Task APairedPhoneReconnectsAfterPcCoreIsRestarted()
    {
        var token = TestContext.Current.CancellationToken;

        string pcInstanceId;
        RemotePhoneClient.Enrollment enrollment;

        await using (var first = await Instance.StartAsync(_state.Directory))
        {
            enrollment = await first.EnrollAsync("samsung SM-S948B", token);
            pcInstanceId = first.Identity.PcInstanceId;

            await using var phone = new RemotePhoneClient(enrollment.DeviceId, enrollment.SharedSecret);
            await phone.ConnectAsync(first.Uri, token);

            var authenticated = await phone.AuthenticateAsync(pcInstanceId, token);
            Assert.True(authenticated.Success);
        }

        // --- PC Core is closed. Everything in memory is gone; only the files remain. ---

        await using var second = await Instance.StartAsync(_state.Directory);

        // The installation is the same one the phone paired with. If this changed, every stored
        // credential in the world would be worthless, because the id is inside the signed message.
        Assert.Equal(pcInstanceId, second.Identity.PcInstanceId);

        // The same phone, still the primary, with the same id it was issued.
        Assert.NotNull(second.Phones.Current);
        Assert.Equal(enrollment.DeviceId, second.Phones.Current!.DeviceId);
        Assert.Equal("samsung SM-S948B", second.Phones.Current.DisplayName);

        // And the credential still authenticates — no re-pairing, no new QR code.
        await using var returning = new RemotePhoneClient(enrollment.DeviceId, enrollment.SharedSecret);
        await returning.ConnectAsync(second.Uri, token);

        var reconnected = await returning.AuthenticateAsync(pcInstanceId, token);

        Assert.True(
            reconnected.Success,
            $"a paired phone was refused after a restart: {reconnected.Error?.Code} {reconnected.Error?.Message}");
        Assert.True(returning.IsAuthenticated);
    }

    /// <summary>
    /// The same phone, across many restarts. Once would pass if a secret happened to survive one
    /// round trip through the protector; this fails if any restart mutates or re-derives it.
    /// </summary>
    [Fact]
    public async Task TheCredentialStillWorksAfterSeveralRestarts()
    {
        var token = TestContext.Current.CancellationToken;

        RemotePhoneClient.Enrollment enrollment;
        string pcInstanceId;

        await using (var first = await Instance.StartAsync(_state.Directory))
        {
            enrollment = await first.EnrollAsync("Owner Phone", token);
            pcInstanceId = first.Identity.PcInstanceId;
        }

        for (var restart = 1; restart <= 3; restart++)
        {
            await using var instance = await Instance.StartAsync(_state.Directory);

            Assert.Equal(pcInstanceId, instance.Identity.PcInstanceId);

            await using var phone = new RemotePhoneClient(enrollment.DeviceId, enrollment.SharedSecret);
            await phone.ConnectAsync(instance.Uri, token);

            var authenticated = await phone.AuthenticateAsync(pcInstanceId, token);
            Assert.True(authenticated.Success, $"restart {restart} refused the paired phone");
        }
    }

    /// <summary>
    /// Reconnecting repeatedly against one running instance, which is what the phone does when a
    /// link drops. Each connection must get its own challenge, and none may be reusable.
    /// </summary>
    [Fact]
    public async Task EveryReconnectGetsAFreshChallengeAndNoneCanBeReplayed()
    {
        var token = TestContext.Current.CancellationToken;

        await using var instance = await Instance.StartAsync(_state.Directory);
        var enrollment = await instance.EnrollAsync("Owner Phone", token);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var phone = new RemotePhoneClient(enrollment.DeviceId, enrollment.SharedSecret);
            await phone.ConnectAsync(instance.Uri, token);

            var authenticated = await phone.AuthenticateAsync(instance.Identity.PcInstanceId, token);
            Assert.True(authenticated.Success, $"reconnect {attempt} was refused");
        }

        // Freshness, checked at the source rather than inferred: the authenticator is the thing
        // that must never hand out the same nonce twice.
        for (var i = 0; i < 64; i++)
        {
            var challenge = instance.Authenticator.IssueChallenge();

            Assert.True(seen.Add(challenge.ChallengeId), "a challenge id was issued twice");
            Assert.True(seen.Add(challenge.Challenge), "a challenge value was issued twice");

            // Spend it, so the outstanding-challenge cap is not what this test measures.
            instance.Authenticator.TryConsumeChallenge(challenge.ChallengeId, out _);
        }
    }

    /// <summary>
    /// A proof made for one challenge is refused against the next one.
    /// <para>
    /// This is the failure the reported symptom would look like if the phone ever cached a proof
    /// across reconnects, so it is worth pinning that the server would not accept it either way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AProofFromAnEarlierChallengeIsRefused()
    {
        var token = TestContext.Current.CancellationToken;

        await using var instance = await Instance.StartAsync(_state.Directory);
        var enrollment = await instance.EnrollAsync("Owner Phone", token);

        var stale = instance.Authenticator.IssueChallenge();
        var staleProof = Sign(
            instance.Authenticator,
            enrollment.DeviceId,
            enrollment.SharedSecret,
            stale.Challenge);

        var fresh = instance.Authenticator.IssueChallenge();

        // The right secret, the right device, the wrong nonce.
        var refused = Assert.Throws<CapabilityException>(
            () => instance.Authenticator.Verify(fresh.ChallengeId, enrollment.DeviceId, staleProof));

        Assert.Equal(ErrorCodes.AuthenticationFailed, refused.Code);

        // That proof is genuinely valid for its own challenge — this is what makes the check above
        // mean something, rather than passing because the proof was malformed all along.
        var accepted = instance.Authenticator.Verify(stale.ChallengeId, enrollment.DeviceId, staleProof);
        Assert.Equal(enrollment.DeviceId, accepted.DeviceId);

        // And now that it has been spent, presenting the identical pair again is refused. A
        // listener who captured the exchange gains nothing by repeating it.
        var replayed = Assert.Throws<CapabilityException>(
            () => instance.Authenticator.Verify(stale.ChallengeId, enrollment.DeviceId, staleProof));

        Assert.Equal(ErrorCodes.AuthenticationFailed, replayed.Code);

        _ = token;
    }

    /// <summary>
    /// The protector that actually runs in production, round-tripped.
    /// <para>
    /// Every other test in the suite substitutes <c>PassThroughSecretProtector</c>, so DPAPI itself
    /// is never exercised — and DPAPI is the one step between the secret the phone was handed and
    /// the secret a restarted PC Core compares against.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRealProtectorReturnsExactlyTheBytesItWasGiven()
    {
        var protector = new WindowsSecretProtector(
            LoggerFactory.Create(b => b.ClearProviders()).CreateLogger<WindowsSecretProtector>());

        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var original = (byte[])secret.Clone();

        var protectedBytes = protector.Protect(secret);

        // Protect must not touch its input: the caller keeps using that array as the live secret.
        Assert.Equal(original, secret);

        var recovered = protector.Unprotect(protectedBytes);

        Assert.NotNull(recovered);
        Assert.Equal(original, recovered);
    }

    private static string Sign(
        IDeviceAuthenticator authenticator,
        string deviceId,
        string sharedSecretBase64Url,
        string challengeBase64Url)
    {
        var secret = global::System.Buffers.Text.Base64Url.DecodeFromChars(sharedSecretBase64Url.AsSpan());
        var challenge = global::System.Buffers.Text.Base64Url.DecodeFromChars(challengeBase64Url.AsSpan());
        var message = authenticator.BuildSignedMessage(deviceId, challenge);

        return global::System.Buffers.Text.Base64Url.EncodeToString(
            System.Security.Cryptography.HMACSHA256.HashData(secret, message));
    }

    /// <summary>
    /// One run of PC Core's identity and remote transport over a given state directory.
    /// <para>
    /// Disposing it is the "user closed Nexus" half of these tests, so nothing here may outlive the
    /// service provider — which is why the transport is owned by this type rather than a fixture.
    /// </para>
    /// </summary>
    private sealed class Instance : IAsyncDisposable
    {
        private ServiceProvider _services = null!;
        private RemoteTransport _transport = null!;

        public IPcIdentityProvider Identity { get; private set; } = null!;

        public IPrimaryPhoneRegistry Phones { get; private set; } = null!;

        public IDeviceAuthenticator Authenticator { get; private set; } = null!;

        public IPairingService Pairing { get; private set; } = null!;

        public Uri Uri { get; private set; } = null!;

        public static async Task<Instance> StartAsync(StateDirectory directory)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.ClearProviders());
            services.AddSingleton(directory);

            // The one substitution left in place: DPAPI blobs are bound to this Windows account,
            // which is fine here but would make the fixture unrunnable anywhere else. The real
            // protector gets its own round-trip test above.
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
            services.AddRemoteControlDoubles();
            services.AddSingleton<RemoteStreamCoordinator>();

            var instance = new Instance { _services = services.BuildServiceProvider() };

            instance.Identity = instance._services.GetRequiredService<IPcIdentityProvider>();
            instance.Phones = instance._services.GetRequiredService<IPrimaryPhoneRegistry>();
            instance.Authenticator = instance._services.GetRequiredService<IDeviceAuthenticator>();
            instance.Pairing = instance._services.GetRequiredService<IPairingService>();

            instance._transport = new RemoteTransport(
                instance._services,
                options,
                instance._services.GetRequiredService<RemoteSessionRegistry>(),
                instance._services.GetRequiredService<ILoggerFactory>());

            await instance._transport.StartAsync(
                new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint"));

            instance.Uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
            return instance;
        }

        /// <summary>Owner starts pairing locally; the phone enrolls over the remote transport.</summary>
        public async Task<RemotePhoneClient.Enrollment> EnrollAsync(string displayName, CancellationToken token)
        {
            var session = Pairing.Begin(replaceExisting: Phones.Current is not null);
            Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));

            return await RemotePhoneClient.EnrollAsync(
                Uri, payload!.PcInstanceId, payload.SessionId, payload.PairingSecret, displayName, token);
        }

        public async ValueTask DisposeAsync()
        {
            await _transport.DisposeAsync();
            await _services.DisposeAsync();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
