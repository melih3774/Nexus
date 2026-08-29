using System.Buffers.Text;
using System.Text;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Identity;

/// <summary>
/// The one narrow path by which a phone that holds no permanent credential can obtain one.
/// <para>
/// <b>The bootstrap problem.</b> Every remote request needs an authenticated <c>DEVICE</c>
/// identity, which needs the Primary Phone secret, which a phone can only get by pairing. Pairing
/// therefore cannot itself require that identity. This service is that exception, and it is
/// deliberately the <em>only</em> one.
/// </para>
/// <para>
/// <b>Why it is not a capability.</b> The caller is unauthenticated, so it must not reach
/// <c>CapabilityDispatcher</c> — that dispatcher exists to serve identified callers under a policy,
/// and an unidentified peer has no place in it. Enrollment also grants nothing: it returns a
/// credential and ends. The connection does not become a <c>DEVICE</c> session; the phone
/// reconnects and authenticates normally, exactly like every later connection.
/// </para>
/// <para>
/// <b>What authorises it.</b> Only the short-lived, single-use, 256-bit pairing secret the owner
/// created locally with <c>pairing.begin</c>. The phone proves possession of it rather than
/// transmitting it, over a nonce this PC issued. Authority to decide which phone is trusted stays
/// on the PC: without an active local pairing session there is nothing to enroll against, and
/// replacing an existing Primary Phone still requires the owner to have said so locally.
/// </para>
/// </summary>
public interface IRemoteEnrollmentService
{
    /// <summary>
    /// Consumes the challenge and the pairing session, verifies the proof, and issues the permanent
    /// credential. Throws <c>pairing_failed</c> for every failure, without distinguishing them.
    /// </summary>
    PairingCompletion Enroll(string challengeId, string sessionId, string proof, string? displayName);

    /// <summary>
    /// The exact bytes a phone must sign with the pairing secret. Shared so the Android client and
    /// the development simulator build the same message this service verifies.
    /// </summary>
    byte[] BuildEnrollmentMessage(string sessionId, ReadOnlySpan<byte> challenge);
}

public sealed class RemoteEnrollmentService : IRemoteEnrollmentService
{
    /// <summary>
    /// Domain separation, distinct from <c>nexus-auth-v1</c>. The two proofs are keyed by different
    /// secrets and mean different things; separating the messages means neither can ever be
    /// replayed as the other, even if some future change made the keys collide.
    /// </summary>
    public const string Context = "nexus-pair-v1";

    private readonly IPcIdentityProvider _identity;
    private readonly IPairingService _pairing;
    private readonly IDeviceAuthenticator _authenticator;
    private readonly ILogger<RemoteEnrollmentService> _logger;

    public RemoteEnrollmentService(
        IPcIdentityProvider identity,
        IPairingService pairing,
        IDeviceAuthenticator authenticator,
        ILogger<RemoteEnrollmentService> logger)
    {
        _identity = identity;
        _pairing = pairing;
        _authenticator = authenticator;
        _logger = logger;
    }

    public PairingCompletion Enroll(string challengeId, string sessionId, string proof, string? displayName)
    {
        if (!_authenticator.TryConsumeChallenge(challengeId ?? string.Empty, out var challenge))
        {
            throw Failed("unknown, expired, or already-used challenge");
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

        var message = BuildEnrollmentMessage(sessionId ?? string.Empty, challenge);

        // PairingService owns the secret and the single-use rule; this service only decides which
        // bytes get signed. A wrong proof still consumes the session there, so a scanned code
        // cannot be brute-forced by repetition.
        var completion = _pairing.CompleteWithProof(sessionId ?? string.Empty, message, presented, displayName);

        // Device id only. The credential in the return value is never logged.
        _logger.LogInformation(
            "Remote enrollment completed: device {DeviceId} is now the primary phone.", completion.DeviceId);

        return completion;
    }

    public byte[] BuildEnrollmentMessage(string sessionId, ReadOnlySpan<byte> challenge)
    {
        var prefix = Encoding.UTF8.GetBytes($"{Context}\0{_identity.PcInstanceId}\0{sessionId}\0");
        var message = new byte[prefix.Length + challenge.Length];

        prefix.CopyTo(message, 0);
        challenge.CopyTo(message.AsSpan(prefix.Length));

        return message;
    }

    private CapabilityException Failed(string internalReason)
    {
        // Same opaque code the local pairing surface returns, for the same reason: an unauthorised
        // caller must not be able to tell "no session" from "wrong proof" from "expired".
        _logger.LogWarning("Remote enrollment refused: {Reason}.", internalReason);
        return new CapabilityException(ErrorCodes.PairingFailed, "Pairing failed.");
    }
}
