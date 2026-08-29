using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Identity;

/// <summary>A one-time random challenge the phone must sign.</summary>
public sealed record AuthChallenge(string ChallengeId, string Challenge, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Proof that the caller holds the Primary Phone's shared secret.
/// <para>
/// This type is the <b>only</b> way to obtain a <see cref="ActorType.Device"/> caller identity.
/// It cannot be constructed from request data — a transport must present one to
/// <see cref="ToCaller"/>, and the only way to get one is a successful
/// <see cref="IDeviceAuthenticator.Verify"/>.
/// </para>
/// </summary>
public sealed record AuthenticatedDevice
{
    internal AuthenticatedDevice(string deviceId, DateTimeOffset authenticatedAtUtc)
    {
        DeviceId = deviceId;
        AuthenticatedAtUtc = authenticatedAtUtc;
    }

    public string DeviceId { get; }

    public DateTimeOffset AuthenticatedAtUtc { get; }

    /// <summary>
    /// Builds the caller identity a transport attaches to requests from this device.
    /// <para>
    /// A future remote transport calls this <em>after</em> verification and never before. Nothing
    /// in a request body can reach this method, which is what stops a caller self-asserting
    /// <c>ActorType.DEVICE</c>.
    /// </para>
    /// </summary>
    public CallerDescriptor ToCaller(string transport, string connectionId) =>
        new(transport, connectionId, ActorType.Device, DeviceId);
}

/// <summary>
/// Challenge/response authentication for the Primary Phone, using HMAC-SHA-256 over the shared
/// secret established at pairing.
/// <para>
/// The permanent secret is never transmitted. The PC sends a random challenge; the phone returns
/// an HMAC over a context-bound message; the PC recomputes and compares in constant time.
/// </para>
/// </summary>
public interface IDeviceAuthenticator
{
    AuthChallenge IssueChallenge();

    /// <summary>
    /// Verifies a proof. Throws <c>authentication_failed</c> for every failure reason.
    /// The challenge is consumed whether or not verification succeeds.
    /// </summary>
    AuthenticatedDevice Verify(string challengeId, string deviceId, string proof);

    /// <summary>
    /// The exact bytes that must be signed. Exposed so the development simulator and the future
    /// Android client sign the same message the PC verifies, rather than reimplementing it.
    /// </summary>
    byte[] BuildSignedMessage(string deviceId, ReadOnlySpan<byte> challenge);
}

public sealed class DeviceAuthenticator : IDeviceAuthenticator
{
    /// <summary>
    /// Domain separation. Binding the purpose, the installation, and the device into the signed
    /// message means a proof captured here cannot be replayed against a different PC Core
    /// installation, a different device id, or some future protocol that reuses the same secret.
    /// </summary>
    private const string Context = "nexus-auth-v1";

    private const int ChallengeBytes = 32;
    private const int ChallengeIdBytes = 16;

    /// <summary>Bounds memory if challenges are issued and never used.</summary>
    private const int MaxOutstandingChallenges = 64;

    private readonly IPcIdentityProvider _identity;
    private readonly IPrimaryPhoneRegistry _registry;
    private readonly PairingOptions _options;
    private readonly ILogger<DeviceAuthenticator> _logger;

    private readonly ConcurrentDictionary<string, Pending> _challenges = new(StringComparer.Ordinal);

    public DeviceAuthenticator(
        IPcIdentityProvider identity,
        IPrimaryPhoneRegistry registry,
        PairingOptions options,
        ILogger<DeviceAuthenticator> logger)
    {
        _identity = identity;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public AuthChallenge IssueChallenge()
    {
        PruneExpired();

        if (_challenges.Count >= MaxOutstandingChallenges)
        {
            throw new CapabilityException(
                ErrorCodes.AuthenticationFailed,
                "Too many outstanding authentication challenges.");
        }

        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ChallengeIdBytes));
        var challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        var expires = DateTimeOffset.UtcNow.AddSeconds(_options.ChallengeTimeoutSeconds);

        _challenges[id] = new Pending(challenge, expires);

        return new AuthChallenge(id, Base64Url.EncodeToString(challenge), expires);
    }

    public AuthenticatedDevice Verify(string challengeId, string deviceId, string proof)
    {
        // Removed up front: a challenge is single-use regardless of outcome, so a captured proof
        // cannot be replayed and a wrong guess cannot be retried against the same challenge.
        if (challengeId is null || !_challenges.TryRemove(challengeId, out var pending))
        {
            throw Failed("unknown or already-used challenge");
        }

        if (DateTimeOffset.UtcNow >= pending.ExpiresAtUtc)
        {
            throw Failed("challenge expired");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw Failed("no device id presented");
        }

        byte[] presented;
        try
        {
            presented = Base64Url.DecodeFromChars((proof ?? string.Empty).AsSpan());
        }
        catch (FormatException)
        {
            throw Failed("proof was not valid base64url");
        }

        var message = BuildSignedMessage(deviceId, pending.Challenge);

        // The registry holds the secret and does the constant-time comparison. A device id that
        // does not match the paired phone simply has no secret to check against — presenting a
        // guessed device id establishes nothing.
        if (!_registry.VerifyProof(deviceId, message, presented))
        {
            throw Failed($"invalid proof for device {deviceId}");
        }

        _logger.LogInformation("Device {DeviceId} authenticated.", deviceId);
        return new AuthenticatedDevice(deviceId, DateTimeOffset.UtcNow);
    }

    public byte[] BuildSignedMessage(string deviceId, ReadOnlySpan<byte> challenge)
    {
        var prefix = Encoding.UTF8.GetBytes($"{Context}\0{_identity.PcInstanceId}\0{deviceId}\0");
        var message = new byte[prefix.Length + challenge.Length];

        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));

        return message;
    }

    private CapabilityException Failed(string internalReason)
    {
        // One opaque message for every failure: telling an unauthenticated caller *why* it failed
        // would let it probe which device ids and challenges exist.
        _logger.LogWarning("Device authentication failed: {Reason}.", internalReason);
        return new CapabilityException(ErrorCodes.AuthenticationFailed, "Authentication failed.");
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, pending) in _challenges)
        {
            if (now >= pending.ExpiresAtUtc)
            {
                _challenges.TryRemove(id, out _);
            }
        }
    }

    private sealed record Pending(byte[] Challenge, DateTimeOffset ExpiresAtUtc);
}
