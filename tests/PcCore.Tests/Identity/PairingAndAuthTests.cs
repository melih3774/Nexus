using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Windows;
using Xunit;

namespace PcCore.Tests.Identity;

/// <summary>
/// A whole PC Core identity stack over a throwaway state directory. "Restarting" means calling
/// <see cref="Restart"/>, which rebuilds every component over the same directory — exactly what a
/// process restart does.
/// </summary>
internal sealed class IdentityHarness : IDisposable
{
    private readonly TempStateDirectory _state;

    public IdentityHarness(string label)
    {
        _state = new TempStateDirectory(label);
        Restart();
    }

    public IPcIdentityProvider Identity { get; private set; } = null!;
    public IPrimaryPhoneRegistry Registry { get; private set; } = null!;
    public IPairingService Pairing { get; private set; } = null!;
    public IDeviceAuthenticator Authenticator { get; private set; } = null!;
    public PairingOptions Options { get; private set; } = null!;

    public TempStateDirectory State => _state;

    public void Restart(PairingOptions? options = null)
    {
        Options = options ?? new PairingOptions();
        Identity = new PcIdentityProvider(_state.Directory, TestLog.For<PcIdentityProvider>());
        Registry = new PrimaryPhoneRegistry(
            _state.Directory, new PassThroughSecretProtector(), TestLog.For<PrimaryPhoneRegistry>());
        Pairing = new PairingService(Identity, Registry, Options, TestLog.For<PairingService>());
        Authenticator = new DeviceAuthenticator(Identity, Registry, Options, TestLog.For<DeviceAuthenticator>());
    }

    /// <summary>Runs the full pairing handshake the way the phone simulator does.</summary>
    public PairingCompletion PairPhone(string name = "Test Phone", bool replaceExisting = false)
    {
        var session = Pairing.Begin(replaceExisting);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out var sid, out var secret));
        return Pairing.Complete(sid, secret, name);
    }

    /// <summary>Computes the proof a correctly-implemented phone would send.</summary>
    public string Sign(string deviceId, string sharedSecret, string challenge)
    {
        var message = Authenticator.BuildSignedMessage(deviceId, Base64Url.DecodeFromChars(challenge.AsSpan()));
        var proof = HMACSHA256.HashData(Base64Url.DecodeFromChars(sharedSecret.AsSpan()), message);
        return Base64Url.EncodeToString(proof);
    }

    public void Dispose() => _state.Dispose();
}

public sealed class PcIdentityTests
{
    [Fact]
    public void IsGeneratedOnceAndSurvivesRestart()
    {
        using var harness = new IdentityHarness("pc-identity");

        var first = harness.Identity.PcInstanceId;
        harness.Restart();

        Assert.Equal(first, harness.Identity.PcInstanceId);
        Assert.True(Guid.TryParse(first, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    [Fact]
    public void SeparateInstallationsGetDifferentIdentities()
    {
        using var a = new IdentityHarness("pc-identity-a");
        using var b = new IdentityHarness("pc-identity-b");

        Assert.NotEqual(a.Identity.PcInstanceId, b.Identity.PcInstanceId);
    }

    [Fact]
    public void IsNotDerivedFromAnyMachineIdentifier()
    {
        // A hardware- or account-derived id would be a fingerprint that follows the owner across
        // reinstalls and leaks machine details into every pairing QR code.
        using var harness = new IdentityHarness("pc-identity-derivation");
        var id = harness.Identity.PcInstanceId;

        Assert.DoesNotContain(Environment.MachineName, id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, id, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PrimaryPhoneTests
{
    [Fact]
    public void NoPhoneIsPairedInitially()
    {
        using var harness = new IdentityHarness("phone-initial");

        Assert.Null(harness.Registry.Current);
        Assert.False(PrimaryPhoneResult.From(harness.Registry.Current).Paired);
    }

    [Fact]
    public void PairingRecordsThePhoneAndSurvivesRestart()
    {
        using var harness = new IdentityHarness("phone-persist");

        var completion = harness.PairPhone("Melih's Pixel");
        harness.Restart();

        var phone = harness.Registry.Current;
        Assert.NotNull(phone);
        Assert.Equal(completion.DeviceId, phone!.DeviceId);
        Assert.Equal("Melih's Pixel", phone.DisplayName);
    }

    [Fact]
    public void TheCredentialStillAuthenticatesAfterRestart()
    {
        using var harness = new IdentityHarness("phone-credential-persist");
        var paired = harness.PairPhone();

        harness.Restart();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);

        Assert.Equal(paired.DeviceId, harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof).DeviceId);
    }

    [Fact]
    public void ASecondPhoneCannotSilentlyReplaceTheFirst()
    {
        // One owner, one phone. Replacing it is an explicit decision, never a side effect of
        // someone starting another pairing.
        using var harness = new IdentityHarness("phone-second");
        var first = harness.PairPhone("First");

        var exception = Assert.Throws<CapabilityException>(() => harness.Pairing.Begin(replaceExisting: false));
        Assert.Equal(ErrorCodes.AlreadyPaired, exception.Code);

        Assert.Equal(first.DeviceId, harness.Registry.Current!.DeviceId);
    }

    [Fact]
    public void AnExplicitReplacementSucceedsAndRetiresTheOldCredential()
    {
        using var harness = new IdentityHarness("phone-replace");
        var first = harness.PairPhone("First");

        var second = harness.PairPhone("Second", replaceExisting: true);

        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.Equal(second.DeviceId, harness.Registry.Current!.DeviceId);

        // The replaced phone's credential is gone, not merely shadowed.
        var challenge = harness.Authenticator.IssueChallenge();
        var oldProof = harness.Sign(first.DeviceId, first.SharedSecret, challenge.Challenge);

        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, first.DeviceId, oldProof));
    }

    [Fact]
    public void UnpairingRemovesTheStoredCredential()
    {
        using var harness = new IdentityHarness("phone-unpair");
        harness.PairPhone();

        Assert.True(harness.Registry.Unpair());

        Assert.Null(harness.Registry.Current);
        Assert.False(harness.State.FileExists(PrimaryPhoneRegistry.FileName));

        // Idempotent: revoking access must not fail because it was already revoked.
        Assert.False(harness.Registry.Unpair());
    }

    [Fact]
    public void TheOldCredentialFailsAfterUnpairing()
    {
        using var harness = new IdentityHarness("phone-unpair-auth");
        var paired = harness.PairPhone();

        harness.Registry.Unpair();
        harness.Restart();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);

        var exception = Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof));
        Assert.Equal(ErrorCodes.AuthenticationFailed, exception.Code);
    }

    [Fact]
    public void TheStoredRecordNeverContainsThePlaintextSecretUnderRealProtection()
    {
        // Deliberately uses the real DPAPI protector rather than the test double: "the credential
        // is not readable on disk" is a claim about the production path, and a pass-through
        // protector would make the assertion pass while proving nothing.
        using var state = new TempStateDirectory("phone-secret-at-rest");
        var registry = new PrimaryPhoneRegistry(
            state.Directory,
            new WindowsSecretProtector(TestLog.For<WindowsSecretProtector>()),
            TestLog.For<PrimaryPhoneRegistry>());

        var credential = registry.Pair("Protected Phone");
        var raw = state.ReadRaw(PrimaryPhoneRegistry.FileName);

        Assert.DoesNotContain(credential.SharedSecret, raw, StringComparison.Ordinal);

        // The blob still round-trips for this Windows account, so the phone keeps working.
        var reopened = new PrimaryPhoneRegistry(
            state.Directory,
            new WindowsSecretProtector(TestLog.For<WindowsSecretProtector>()),
            TestLog.For<PrimaryPhoneRegistry>());

        Assert.Equal(credential.Phone.DeviceId, reopened.Current!.DeviceId);
    }

    [Fact]
    public void DpapiProtectionRoundTripsAndProducesDistinctCiphertext()
    {
        var protector = new WindowsSecretProtector(TestLog.For<WindowsSecretProtector>());
        var secret = RandomNumberGenerator.GetBytes(32);

        var protectedBytes = protector.Protect(secret);

        Assert.NotEqual(secret, protectedBytes);
        Assert.Equal(secret, protector.Unprotect(protectedBytes));
    }

    [Fact]
    public void ATamperedProtectedBlobIsRejectedRatherThanReturningGarbage()
    {
        var protector = new WindowsSecretProtector(TestLog.For<WindowsSecretProtector>());
        var protectedBytes = protector.Protect(RandomNumberGenerator.GetBytes(32));

        protectedBytes[^1] ^= 0xFF;

        Assert.Null(protector.Unprotect(protectedBytes));
    }

    [Fact]
    public void ACredentialThatCannotBeUnprotectedLeavesTheSystemUnpaired()
    {
        // Fail closed: a restored profile or a different Windows account means the blob will not
        // decrypt, and half-loading a phone we cannot authenticate would be worse than no phone.
        using var harness = new IdentityHarness("phone-unprotect-fail");
        harness.PairPhone();

        var registry = new PrimaryPhoneRegistry(
            harness.State.Directory, new FailingSecretProtector(), TestLog.For<PrimaryPhoneRegistry>());

        Assert.Null(registry.Current);
    }

    [Fact]
    public void ACorruptRecordLeavesTheSystemUnpaired()
    {
        using var harness = new IdentityHarness("phone-corrupt");
        harness.PairPhone();

        harness.State.Corrupt(PrimaryPhoneRegistry.FileName);
        harness.Restart();

        Assert.Null(harness.Registry.Current);
    }

    private sealed class FailingSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[]? Unprotect(byte[] ciphertext) => null;
    }
}

public sealed class PairingSessionTests
{
    [Fact]
    public void TheSharedSecretHasFullEntropyAndIsNotHumanGenerated()
    {
        using var harness = new IdentityHarness("pairing-entropy");
        var paired = harness.PairPhone();

        var secret = Base64Url.DecodeFromChars(paired.SharedSecret.AsSpan());

        Assert.Equal(32, secret.Length);                       // 256 bits
        Assert.True(secret.Distinct().Count() > 12, "secret looks structured rather than random");
    }

    [Fact]
    public void EverySessionIsUnique()
    {
        using var harness = new IdentityHarness("pairing-unique");

        var sessions = Enumerable.Range(0, 25).Select(_ => harness.Pairing.Begin(replaceExisting: false)).ToArray();

        Assert.Equal(25, sessions.Select(s => s.SessionId).Distinct().Count());
        Assert.Equal(25, sessions.Select(s => s.Payload).Distinct().Count());
    }

    [Fact]
    public void ThePayloadRoundTripsAndCarriesOnlyPairingFields()
    {
        using var harness = new IdentityHarness("pairing-payload");
        var session = harness.Pairing.Begin(replaceExisting: false);

        Assert.StartsWith(PairingPayload.Prefix, session.Payload, StringComparison.Ordinal);
        Assert.True(PairingPayload.TryDecode(session.Payload, out var version, out var pc, out var sid, out var psk));

        Assert.Equal(1, version);
        Assert.Equal(harness.Identity.PcInstanceId, pc);
        Assert.Equal(session.SessionId, sid);
        Assert.NotEmpty(psk);

        // Nothing about the machine or the owner rides along in something that gets photographed.
        var decoded = Encoding.UTF8.GetString(
            Base64Url.DecodeFromChars(session.Payload.AsSpan(PairingPayload.Prefix.Length)));
        Assert.DoesNotContain(Environment.MachineName, decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, decoded, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-payload")]
    [InlineData("NEXUS1.@@@not-base64@@@")]
    [InlineData("NEXUS1.")]
    public void MalformedPayloadsAreRejected(string? payload) =>
        Assert.False(PairingPayload.TryDecode(payload, out _, out _, out _, out _));

    [Fact]
    public void AnExpiredSessionCannotBeCompleted()
    {
        using var harness = new IdentityHarness("pairing-expiry");
        harness.Restart(new PairingOptions { SessionTimeoutSeconds = 0 });

        var session = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out var sid, out var psk));

        var exception = Assert.Throws<CapabilityException>(() => harness.Pairing.Complete(sid, psk, "Phone"));

        Assert.Equal(ErrorCodes.PairingFailed, exception.Code);
        Assert.Null(harness.Registry.Current);
        Assert.Null(harness.Pairing.Active);
    }

    [Fact]
    public void ACancelledSessionCannotBeCompleted()
    {
        using var harness = new IdentityHarness("pairing-cancel");
        var session = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out var sid, out var psk));

        Assert.True(harness.Pairing.Cancel());
        Assert.False(harness.Pairing.Cancel());
        Assert.Null(harness.Pairing.Active);

        Assert.Throws<CapabilityException>(() => harness.Pairing.Complete(sid, psk, "Phone"));
        Assert.Null(harness.Registry.Current);
    }

    [Fact]
    public void ASessionIsSingleUse()
    {
        using var harness = new IdentityHarness("pairing-single-use");
        var session = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out var sid, out var psk));

        harness.Pairing.Complete(sid, psk, "Phone");

        // Replaying the same code must not pair a second phone.
        Assert.Throws<CapabilityException>(() => harness.Pairing.Complete(sid, psk, "Impostor"));
    }

    [Fact]
    public void AWrongSecretIsRejectedAndBurnsTheSession()
    {
        using var harness = new IdentityHarness("pairing-wrong-secret");
        var session = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out var sid, out var psk));

        Assert.Throws<CapabilityException>(() => harness.Pairing.Complete(sid, "wrong-secret", "Phone"));

        // Consumed even on failure, so a code cannot be guessed at by repetition.
        Assert.Throws<CapabilityException>(() => harness.Pairing.Complete(sid, psk, "Phone"));
        Assert.Null(harness.Registry.Current);
    }

    [Fact]
    public void AWrongSessionIdIsRejected()
    {
        using var harness = new IdentityHarness("pairing-wrong-session");
        var session = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out _, out var psk));

        Assert.Throws<CapabilityException>(() => harness.Pairing.Complete("some-other-session", psk, "Phone"));
    }

    [Fact]
    public void StartingANewSessionInvalidatesThePreviousOne()
    {
        using var harness = new IdentityHarness("pairing-replace-session");
        var first = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(first.Payload, out _, out _, out var firstSid, out var firstPsk));

        harness.Pairing.Begin(replaceExisting: false);

        Assert.Throws<CapabilityException>(() => harness.Pairing.Complete(firstSid, firstPsk, "Phone"));
    }

    [Fact]
    public async Task ConcurrentCompletionsPairExactlyOnePhone()
    {
        using var harness = new IdentityHarness("pairing-concurrent");
        var session = harness.Pairing.Begin(replaceExisting: false);
        Assert.True(PairingPayload.TryDecode(session.Payload, out _, out _, out var sid, out var psk));

        using var start = new Barrier(16);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();

            try
            {
                return harness.Pairing.Complete(sid, psk, "Phone");
            }
            catch (CapabilityException)
            {
                return null;
            }
        })));

        var successes = outcomes.Where(o => o is not null).ToArray();

        Assert.Single(successes);
        Assert.Equal(successes[0]!.DeviceId, harness.Registry.Current!.DeviceId);
    }
}

public sealed class DeviceAuthenticationTests
{
    [Fact]
    public void ThePairedPhoneAuthenticates()
    {
        using var harness = new IdentityHarness("auth-success");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);

        var device = harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof);

        Assert.Equal(paired.DeviceId, device.DeviceId);
    }

    [Fact]
    public void AWrongSecretFails()
    {
        using var harness = new IdentityHarness("auth-wrong-secret");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var wrong = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var proof = harness.Sign(paired.DeviceId, wrong, challenge.Challenge);

        var exception = Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof));
        Assert.Equal(ErrorCodes.AuthenticationFailed, exception.Code);
    }

    [Fact]
    public void SigningADifferentChallengeFails()
    {
        using var harness = new IdentityHarness("auth-altered-challenge");
        var paired = harness.PairPhone();

        var issued = harness.Authenticator.IssueChallenge();
        var somethingElse = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, somethingElse);

        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(issued.ChallengeId, paired.DeviceId, proof));
    }

    [Fact]
    public void AnAlteredProofFails()
    {
        using var harness = new IdentityHarness("auth-altered-proof");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = Base64Url.DecodeFromChars(
            harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge).AsSpan());
        proof[0] ^= 0xFF;

        Assert.Throws<CapabilityException>(() => harness.Authenticator.Verify(
            challenge.ChallengeId, paired.DeviceId, Base64Url.EncodeToString(proof)));
    }

    [Fact]
    public void ReplayingAProofFails()
    {
        // Challenges are single-use, so a captured proof is worthless the moment it is spent.
        using var harness = new IdentityHarness("auth-replay");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);

        harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof);

        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof));
    }

    [Fact]
    public void AFailedAttemptAlsoConsumesTheChallenge()
    {
        using var harness = new IdentityHarness("auth-consume-on-failure");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, "AAAA"));

        // A wrong guess cannot be retried against the same challenge.
        var goodProof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);
        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, goodProof));
    }

    [Fact]
    public void AnExpiredChallengeFails()
    {
        using var harness = new IdentityHarness("auth-expiry");
        harness.Restart(new PairingOptions { ChallengeTimeoutSeconds = 0 });
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);

        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof));
    }

    [Fact]
    public void AGuessedDeviceIdEstablishesNothing()
    {
        // The device id is an identifier, not a credential. Knowing it — or inventing one — gets a
        // caller nowhere without the shared secret.
        using var harness = new IdentityHarness("auth-device-id");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign("some-other-device", paired.SharedSecret, challenge.Challenge);

        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, "some-other-device", proof));
    }

    [Fact]
    public void AuthenticatingAgainstAnUnpairedSystemFails()
    {
        using var harness = new IdentityHarness("auth-unpaired");

        var challenge = harness.Authenticator.IssueChallenge();

        Assert.Throws<CapabilityException>(
            () => harness.Authenticator.Verify(challenge.ChallengeId, "anything", "AAAA"));
    }

    [Fact]
    public void ProofsAreBoundToThisInstallation()
    {
        // Domain separation: the signed message includes the PC instance id, so a proof produced
        // for one installation cannot be replayed at another even if both somehow shared a secret.
        using var a = new IdentityHarness("auth-binding-a");
        using var b = new IdentityHarness("auth-binding-b");

        var messageA = a.Authenticator.BuildSignedMessage("device", [1, 2, 3]);
        var messageB = b.Authenticator.BuildSignedMessage("device", [1, 2, 3]);

        Assert.NotEqual(messageA, messageB);
        Assert.Contains("nexus-auth-v1", Encoding.UTF8.GetString(messageA), StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulAuthenticationIsTheOnlyRouteToADeviceCaller()
    {
        using var harness = new IdentityHarness("auth-caller");
        var paired = harness.PairPhone();

        var challenge = harness.Authenticator.IssueChallenge();
        var proof = harness.Sign(paired.DeviceId, paired.SharedSecret, challenge.Challenge);
        var device = harness.Authenticator.Verify(challenge.ChallengeId, paired.DeviceId, proof);

        var caller = device.ToCaller("remote-test", "conn-1");

        Assert.Equal(ActorType.Device, caller.ActorType);
        Assert.Equal(paired.DeviceId, caller.DeviceId);

        // AuthenticatedDevice cannot be constructed outside the capability assembly, so this
        // identity is unreachable without a verified proof.
        Assert.Empty(typeof(AuthenticatedDevice)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }
}
