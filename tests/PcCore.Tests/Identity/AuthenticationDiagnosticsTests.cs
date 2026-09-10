using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Identity;

/// <summary>
/// What the owner's own log says when a phone cannot authenticate.
/// <para>
/// Every rejection returns the same opaque <c>authentication_failed</c> to the caller, deliberately,
/// so that an unauthenticated peer cannot use the reply to map which device ids exist. The
/// consequence is that the log was the only place the reason could live — and it printed the same
/// sentence for a phone holding a superseded credential, a phone with a wrong secret, and a PC with
/// no pairing at all. All three look like "invalid proof", and the only remedy anyone can infer
/// from that is to re-pair, which destroys the evidence of what actually happened.
/// </para>
/// </summary>
public sealed class AuthenticationDiagnosticsTests
{
    /// <summary>The reported case: the phone is real, but the PC has since paired something else.</summary>
    [Fact]
    public void APhoneHoldingASupersededCredentialIsNamedAsSuch()
    {
        using var fixture = new Fixture();

        var first = fixture.PairPhone("Old Phone");
        var second = fixture.PairPhone("New Phone", replaceExisting: true);

        var refused = fixture.AttemptWith(first.Phone.DeviceId, first.SharedSecret);

        Assert.Equal(ErrorCodes.AuthenticationFailed, refused.Code);

        // The caller still learns nothing.
        Assert.Equal("Authentication failed.", refused.Message);

        // The owner learns everything they need: both ids, and which one this PC trusts.
        var warning = Assert.Single(fixture.Log.Warnings);

        Assert.Contains(first.Phone.DeviceId, warning, StringComparison.Ordinal);
        Assert.Contains(second.Phone.DeviceId, warning, StringComparison.Ordinal);
        Assert.Contains("not the paired phone", warning, StringComparison.Ordinal);
        Assert.Contains("earlier pairing", warning, StringComparison.Ordinal);
    }

    /// <summary>The right phone, the wrong secret — a different problem with a different remedy.</summary>
    [Fact]
    public void TheRightDeviceWithAWrongSecretIsDistinguishedFromAnUnknownDevice()
    {
        using var fixture = new Fixture();
        var paired = fixture.PairPhone();

        var wrongSecret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var refused = fixture.AttemptWith(paired.Phone.DeviceId, wrongSecret);

        Assert.Equal(ErrorCodes.AuthenticationFailed, refused.Code);

        var warning = Assert.Single(fixture.Log.Warnings);

        Assert.Contains("is the paired phone", warning, StringComparison.Ordinal);
        Assert.Contains("did not match the stored secret", warning, StringComparison.Ordinal);

        // And it says which installation, because a phone paired to a different PC Core produces
        // exactly this outcome and the instance id is what tells them apart.
        Assert.Contains(fixture.Identity.PcInstanceId, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnpairedPcSaysThatRatherThanBlamingTheProof()
    {
        using var fixture = new Fixture();

        var refused = fixture.AttemptWith(
            "SomeDeviceId",
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

        Assert.Equal(ErrorCodes.AuthenticationFailed, refused.Code);

        var warning = Assert.Single(fixture.Log.Warnings);
        Assert.Contains("no primary phone is paired", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A phone that cannot authenticate reconnects on a backoff. The log must not become one
    /// sentence repeated until the owner happens to look.
    /// </summary>
    [Fact]
    public void AnUnchangingFailureIsReportedOnceRatherThanOnEveryAttempt()
    {
        using var fixture = new Fixture();
        var paired = fixture.PairPhone();

        var wrongSecret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        for (var attempt = 0; attempt < 25; attempt++)
        {
            fixture.AttemptWith(paired.Phone.DeviceId, wrongSecret);
        }

        // Twenty-five refusals, one line. The count is carried in a periodic summary rather than in
        // twenty-five copies; nothing here waits a minute to prove that.
        Assert.Single(fixture.Log.Warnings);
    }

    /// <summary>
    /// A different failure is news and is always printed — otherwise collapsing repeats would hide
    /// the moment the problem changed, which is the one moment worth seeing.
    /// </summary>
    [Fact]
    public void AChangeOfReasonIsAlwaysReported()
    {
        using var fixture = new Fixture();
        var paired = fixture.PairPhone();

        var wrongSecret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        fixture.AttemptWith(paired.Phone.DeviceId, wrongSecret);
        fixture.AttemptWith(paired.Phone.DeviceId, wrongSecret);

        // Now a different device id: a different reason entirely.
        fixture.AttemptWith("AnotherDeviceId", wrongSecret);

        var warnings = fixture.Log.Warnings.ToList();

        Assert.Contains(warnings, w => w.Contains("did not match the stored secret", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("not the paired phone", StringComparison.Ordinal));

        // And the repeats that were swallowed are accounted for rather than lost.
        Assert.Contains(warnings, w => w.Contains("repeated 1 more time(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The distinction stays local. Whatever the log says, the wire answer is one code and one
    /// sentence — this is the property that stops the diagnostic becoming an oracle.
    /// </summary>
    [Fact]
    public void EveryOutcomeReturnsTheSameErrorToTheCaller()
    {
        using var fixture = new Fixture();

        var unpaired = fixture.AttemptWith("A", Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

        var paired = fixture.PairPhone();
        var wrongDevice = fixture.AttemptWith("B", paired.SharedSecret);
        var wrongSecret = fixture.AttemptWith(
            paired.Phone.DeviceId, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

        foreach (var refusal in new[] { unpaired, wrongDevice, wrongSecret })
        {
            Assert.Equal(ErrorCodes.AuthenticationFailed, refusal.Code);
            Assert.Equal("Authentication failed.", refusal.Message);
        }
    }

    /// <summary>The registry's own view of the three outcomes, without the logging in between.</summary>
    [Fact]
    public void TheRegistryReportsWhichOfTheThreeWaysAProofFailed()
    {
        using var state = new TempStateDirectory("proof-outcome");

        var registry = new PrimaryPhoneRegistry(
            state.Directory, new PassThroughSecretProtector(), TestLog.For<PrimaryPhoneRegistry>());

        var message = "some signed message"u8.ToArray();

        Assert.Equal(ProofOutcome.NotPaired, registry.CheckProof("anything", message, [1, 2, 3]));

        var credential = registry.Pair("Phone");
        var secret = Base64Url.DecodeFromChars(credential.SharedSecret.AsSpan());
        var proof = HMACSHA256.HashData(secret, message);

        Assert.Equal(ProofOutcome.DifferentDevice, registry.CheckProof("someone-else", message, proof));
        Assert.Equal(ProofOutcome.WrongProof, registry.CheckProof(credential.Phone.DeviceId, message, [9, 9, 9]));
        Assert.Equal(ProofOutcome.Verified, registry.CheckProof(credential.Phone.DeviceId, message, proof));

        // And the boolean it used to be still agrees with all four.
        Assert.False(registry.VerifyProof("someone-else", message, proof));
        Assert.True(registry.VerifyProof(credential.Phone.DeviceId, message, proof));
    }

    /// <summary>The identity stack over a throwaway directory, with a log this test can read.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempStateDirectory _state = new("auth-diagnostics");

        public Fixture()
        {
            Identity = new PcIdentityProvider(_state.Directory, TestLog.For<PcIdentityProvider>());
            Registry = new PrimaryPhoneRegistry(
                _state.Directory, new PassThroughSecretProtector(), TestLog.For<PrimaryPhoneRegistry>());
            Authenticator = new DeviceAuthenticator(Identity, Registry, new PairingOptions(), Log);
        }

        public CapturingLogger<DeviceAuthenticator> Log { get; } = new();

        public IPcIdentityProvider Identity { get; }

        public IPrimaryPhoneRegistry Registry { get; }

        public IDeviceAuthenticator Authenticator { get; }

        public PairedCredential PairPhone(string name = "Phone", bool replaceExisting = false)
        {
            _ = replaceExisting;
            return Registry.Pair(name);
        }

        /// <summary>One full challenge/verify round trip, returning the refusal.</summary>
        public CapabilityException AttemptWith(string deviceId, string sharedSecretBase64Url)
        {
            var challenge = Authenticator.IssueChallenge();
            var secret = Base64Url.DecodeFromChars(sharedSecretBase64Url.AsSpan());
            var message = Authenticator.BuildSignedMessage(
                deviceId, Base64Url.DecodeFromChars(challenge.Challenge.AsSpan()));

            var proof = Base64Url.EncodeToString(HMACSHA256.HashData(secret, message));

            return Assert.Throws<CapabilityException>(
                () => Authenticator.Verify(challenge.ChallengeId, deviceId, proof));
        }

        public void Dispose() => _state.Dispose();
    }
}
