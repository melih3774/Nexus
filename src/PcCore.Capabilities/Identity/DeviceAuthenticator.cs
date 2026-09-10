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
    /// The exact bytes that must be signed. Exposed so the development simulator and the Android
    /// client sign the same message the PC verifies, rather than reimplementing it.
    /// </summary>
    byte[] BuildSignedMessage(string deviceId, ReadOnlySpan<byte> challenge);

    /// <summary>
    /// Takes a challenge out of circulation and returns its bytes, without checking any device
    /// proof.
    /// <para>
    /// This exists for one caller: remote pairing enrollment, where the peer has no permanent
    /// credential yet and proves possession of the short-lived pairing secret instead. The nonce is
    /// still the PC's own, still single-use, and still expiring — enrollment reuses this machinery
    /// rather than inventing a second replay defence. It grants nothing on its own: a caller
    /// holding a challenge has proved nothing at all until a proof is checked against it.
    /// </para>
    /// </summary>
    bool TryConsumeChallenge(string challengeId, out byte[] challenge);
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

    /// <summary>How often an unchanging failure is allowed to say so again. See <see cref="Report"/>.</summary>
    private static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _reportGate = new();
    private string? _lastReason;
    private int _repeats;
    private DateTimeOffset _lastReportedUtc;

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
        var outcome = _registry.CheckProof(deviceId, message, presented);

        if (outcome != ProofOutcome.Verified)
        {
            // The caller is still told nothing (see Failed). The owner's own log, though, gets the
            // one sentence that separates "this phone is no longer the paired one" from "this
            // phone's secret is wrong" — a distinction that otherwise costs an afternoon, because
            // both used to print the same line and both are cured by re-pairing, which is the last
            // thing to try rather than the first.
            throw Failed(Explain(outcome, deviceId));
        }

        _logger.LogInformation("Device {DeviceId} authenticated.", deviceId);
        return new AuthenticatedDevice(deviceId, DateTimeOffset.UtcNow);
    }

    public bool TryConsumeChallenge(string challengeId, out byte[] challenge)
    {
        // Removed, not read: exactly like Verify, so a challenge spent on an enrollment attempt
        // cannot then be spent on an authentication attempt or replayed by a listener.
        if (challengeId is not null && _challenges.TryRemove(challengeId, out var pending))
        {
            if (DateTimeOffset.UtcNow < pending.ExpiresAtUtc)
            {
                challenge = pending.Challenge;
                return true;
            }

            _logger.LogWarning("Challenge consumed after expiry.");
        }

        challenge = [];
        return false;
    }

    public byte[] BuildSignedMessage(string deviceId, ReadOnlySpan<byte> challenge)
    {
        var prefix = Encoding.UTF8.GetBytes($"{Context}\0{_identity.PcInstanceId}\0{deviceId}\0");
        var message = new byte[prefix.Length + challenge.Length];

        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));

        return message;
    }

    /// <summary>
    /// Turns a verification outcome into something the owner can act on, using identifiers only.
    /// <para>
    /// No key material appears here, and none is needed: the two ids are enough to tell a phone
    /// carrying a superseded pairing from one carrying the wrong secret, and the installation id is
    /// enough to tell a phone paired to a different PC Core entirely.
    /// </para>
    /// </summary>
    private string Explain(ProofOutcome outcome, string deviceId) => outcome switch
    {
        ProofOutcome.NotPaired =>
            $"device {deviceId} presented a proof but no primary phone is paired on this "
            + "installation (or its stored credential could not be read)",

        // The actionable one. The phone is not lying about who it is; it is holding a credential
        // this PC replaced, which is what happens when pairing is run again after the phone had
        // already enrolled.
        ProofOutcome.DifferentDevice =>
            $"device {deviceId} is not the paired phone — this installation is paired with "
            + $"{_registry.Current?.DeviceId ?? "nothing"}, so the presented credential is from an "
            + "earlier pairing and cannot be verified",

        ProofOutcome.WrongProof =>
            $"device {deviceId} is the paired phone, but its proof did not match the stored secret "
            + $"for installation {_identity.PcInstanceId} — the phone's copy of the credential "
            + "differs from this PC's",

        _ => $"invalid proof for device {deviceId}",
    };

    private CapabilityException Failed(string internalReason)
    {
        // One opaque message for every failure: telling an unauthenticated caller *why* it failed
        // would let it probe which device ids and challenges exist.
        Report(internalReason);
        return new CapabilityException(ErrorCodes.AuthenticationFailed, "Authentication failed.");
    }

    /// <summary>
    /// Logs a failure once, then collapses identical repeats.
    /// <para>
    /// A phone that cannot authenticate reconnects on a backoff, so the same sentence would
    /// otherwise be written to the owner's log every few seconds until they noticed — which buries
    /// the first occurrence, and everything else that happened afterwards, under a wall of one
    /// repeated line. The information in the tenth copy is the count, not the sentence, so that is
    /// what gets written.
    /// </para>
    /// <para>
    /// A <em>different</em> reason is always reported immediately: the change is the news.
    /// </para>
    /// </summary>
    private void Report(string reason)
    {
        string? summary = null;
        var announce = false;

        lock (_reportGate)
        {
            var now = DateTimeOffset.UtcNow;

            if (!string.Equals(reason, _lastReason, StringComparison.Ordinal))
            {
                // Flush what the previous reason accumulated, so a run of failures is never lost
                // silently just because the last one happened to be different.
                if (_repeats > 0)
                {
                    summary = $"The previous authentication failure repeated {_repeats} more time(s).";
                }

                _lastReason = reason;
                _repeats = 0;
                _lastReportedUtc = now;
                announce = true;
            }
            else if (now - _lastReportedUtc >= RepeatSummaryInterval)
            {
                _repeats++;
                summary = $"Device authentication failed: {reason}. "
                    + $"(repeated {_repeats} time(s) since the first)";
                _lastReportedUtc = now;
            }
            else
            {
                _repeats++;
            }
        }

        if (summary is not null)
        {
            _logger.LogWarning("{Summary}", summary);
        }

        if (announce)
        {
            _logger.LogWarning("Device authentication failed: {Reason}.", reason);
        }
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
