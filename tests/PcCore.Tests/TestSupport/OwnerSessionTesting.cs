using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

using PcCore.Capabilities.Identity;
using PcCore.Protocol;
using PcCore.Transport.Remote;

using Xunit;

namespace PcCore.Tests.TestSupport;

/// <summary>
/// A stand-in for the Android Keystore half of owner authentication.
/// <para>
/// The phone generates a P-256 key the platform will not use until a biometric or device-credential
/// prompt has been satisfied, registers the public half once, and signs a challenge with it each
/// time the owner authenticates. None of that is testable here — there is no fingerprint sensor in a
/// test run — but everything the <em>PC</em> does with the result is, and this is what lets those
/// tests speak the real protocol rather than a mock of it.
/// </para>
/// </summary>
public sealed class OwnerPhoneKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Base64 X.509 SubjectPublicKeyInfo, exactly what Android's <c>PublicKey.getEncoded()</c> yields.</summary>
    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>DER, exactly what Java's <c>SHA256withECDSA</c> yields.</summary>
    public string Sign(string pcInstanceId, string deviceId, string challengeBase64Url) =>
        Convert.ToBase64String(_key.SignData(
            OwnerSessionService.SigningMessage(pcInstanceId, deviceId, Base64Url.DecodeFromChars(challengeBase64Url)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => _key.Dispose();
}

public static class OwnerSessionTesting
{
    /// <summary>
    /// Registers the key, takes a challenge, signs it, and authorises the session — the whole
    /// sequence a phone performs after a successful fingerprint.
    /// </summary>
    public static async Task<string> AuthorizeAsync(
        this RemotePhoneClient client,
        OwnerPhoneKey key,
        string pcInstanceId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var enrolled = await client.SendRequestAsync(
            "session.enroll_key", new { public_key = key.PublicKey }, cancellationToken);

        Assert.True(enrolled.Success, $"session.enroll_key failed: {enrolled.Error?.Code}");
        Assert.True(Read(enrolled.Result).GetProperty("registered").GetBoolean());

        var challenge = await client.SendRequestAsync("session.challenge", null, cancellationToken);
        Assert.True(challenge.Success, $"session.challenge failed: {challenge.Error?.Code}");

        var payload = Read(challenge.Result);

        var authorized = await client.SendRequestAsync(
            "session.authorize",
            new
            {
                challenge_id = payload.GetProperty("challenge_id").GetString(),
                signature = key.Sign(
                    pcInstanceId, deviceId, payload.GetProperty("challenge").GetString()!),
            },
            cancellationToken);

        Assert.True(authorized.Success, $"session.authorize failed: {authorized.Error?.Code}");

        var grant = Read(authorized.Result);
        Assert.True(grant.GetProperty("authorized").GetBoolean());

        return grant.GetProperty("ticket").GetString()!;
    }

    public static JsonElement Read(object? result) => JsonSerializer.SerializeToElement(result, ProtocolCodec.Options);
}
