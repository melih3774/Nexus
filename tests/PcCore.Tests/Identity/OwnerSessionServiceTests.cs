using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Identity;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;

using Xunit;

namespace PcCore.Tests.Identity;

/// <summary>
/// Owner presence, as PC Core decides it.
/// <para>
/// The Android half of this cannot be tested here — there is no fingerprint sensor in a test run —
/// but everything that makes the phone's claim <em>checkable</em> is on this side, and all of it is
/// ordinary code: verify a signature against a registered key, hold an authorisation for as long as
/// it is being used, and stop holding it when it is not.
/// </para>
/// </summary>
public sealed class OwnerSessionServiceTests : IDisposable
{
    private readonly TempStateDirectory _state = new("owner-session");
    private readonly PrimaryPhoneRegistry _phones;
    private readonly PcIdentityProvider _identity;
    private readonly AdjustableTime _time = new(new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly OwnerSessionOptions _options = new();
    private readonly OwnerSessionService _sessions;
    private readonly OwnerPhoneKey _key = new();
    private readonly string _deviceId;

    public OwnerSessionServiceTests()
    {
        _phones = new PrimaryPhoneRegistry(
            _state.Directory, new PassThroughSecretProtector(), NullLogger<PrimaryPhoneRegistry>.Instance);

        _identity = new PcIdentityProvider(_state.Directory, NullLogger<PcIdentityProvider>.Instance);

        _sessions = new OwnerSessionService(
            _phones, _identity, _options, NullLogger<OwnerSessionService>.Instance, _time);

        _deviceId = _phones.Pair("Test Phone").Phone.DeviceId;
    }

    public void Dispose()
    {
        _key.Dispose();
        _state.Dispose();
    }

    private CallerDescriptor Caller(string connectionId = "c1") =>
        new("remote", connectionId, ActorType.Device, _deviceId);

    private string Authorize(string connectionId = "c1")
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));

        var challenge = _sessions.IssueChallenge(connectionId, _deviceId);

        var grant = _sessions.Authorize(
            connectionId,
            _deviceId,
            challenge.ChallengeId,
            _key.Sign(_identity.PcInstanceId, _deviceId, challenge.Challenge));

        return grant.Ticket;
    }

    [Fact]
    public void AnAuthenticatedDeviceHasNoAuthorityUntilItProvesTheOwner()
    {
        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller()));
        Assert.False(_sessions.Describe("c1", _deviceId).Authorized);
    }

    [Fact]
    public void AValidSignatureOpensAFullRemoteSession()
    {
        Authorize();

        Assert.Equal(RemoteAuthority.FullRemote, _sessions.AuthorityOf(Caller()));
        Assert.True(_sessions.AnyFullRemote);
        Assert.True(_sessions.Describe("c1", _deviceId).Authorized);
    }

    /// <summary>The authority belongs to the connection that earned it, not to the device.</summary>
    [Fact]
    public void AnotherConnectionDoesNotInheritTheAuthority()
    {
        Authorize("c1");

        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller("c2")));
    }

    [Fact]
    public void ASignatureFromAnotherKeyIsRefused()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));

        using var impostor = new OwnerPhoneKey();
        var challenge = _sessions.IssueChallenge("c1", _deviceId);

        var refused = Assert.Throws<CapabilityException>(() => _sessions.Authorize(
            "c1",
            _deviceId,
            challenge.ChallengeId,
            impostor.Sign(_identity.PcInstanceId, _deviceId, challenge.Challenge)));

        Assert.Equal(ErrorCodes.AuthenticationFailed, refused.Code);
        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller()));
    }

    /// <summary>
    /// A challenge is spent whether it verified or not. Otherwise a wrong answer would buy unlimited
    /// attempts against one nonce.
    /// </summary>
    [Fact]
    public void AChallengeCannotBeUsedTwice()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));

        var challenge = _sessions.IssueChallenge("c1", _deviceId);
        var signature = _key.Sign(_identity.PcInstanceId, _deviceId, challenge.Challenge);

        _sessions.Authorize("c1", _deviceId, challenge.ChallengeId, signature);

        Assert.Throws<CapabilityException>(() =>
            _sessions.Authorize("c1", _deviceId, challenge.ChallengeId, signature));
    }

    [Fact]
    public void AnExpiredChallengeIsRefused()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));

        var challenge = _sessions.IssueChallenge("c1", _deviceId);
        _time.Advance(TimeSpan.FromSeconds(_options.ChallengeSeconds + 5));

        Assert.Throws<CapabilityException>(() => _sessions.Authorize(
            "c1", _deviceId, challenge.ChallengeId,
            _key.Sign(_identity.PcInstanceId, _deviceId, challenge.Challenge)));
    }

    // --- the ticket ---------------------------------------------------------------------------

    /// <summary>
    /// The whole reason a reconnect does not cost a fingerprint. A phone link drops constantly.
    /// </summary>
    [Fact]
    public void AReconnectResumesFromTheTicket()
    {
        var ticket = Authorize("c1");

        _sessions.Forget("c1");

        _sessions.Resume("c2", _deviceId, ticket);

        Assert.Equal(RemoteAuthority.FullRemote, _sessions.AuthorityOf(Caller("c2")));
    }

    [Fact]
    public void AnUnknownTicketIsRefused()
    {
        Authorize();

        var refused = Assert.Throws<CapabilityException>(() => _sessions.Resume(
            "c2", _deviceId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')));

        Assert.Equal(ErrorCodes.AuthenticationFailed, refused.Code);
    }

    [Fact]
    public void FifteenIdleMinutesEndTheSession()
    {
        Authorize();

        _time.Advance(TimeSpan.FromMinutes(_options.IdleMinutes + 1));

        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller()));
        Assert.False(_sessions.AnyFullRemote);
    }

    /// <summary>
    /// Activity slides the window. Without this, a session would die mid-use after fifteen minutes
    /// however busy the owner was.
    /// </summary>
    [Fact]
    public void UsingTheSessionKeepsItAlive()
    {
        Authorize();

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(_options.IdleMinutes - 1));
            Assert.Equal(RemoteAuthority.FullRemote, _sessions.AuthorityOf(Caller()));
        }
    }

    /// <summary>And the ceiling is not slid by anything.</summary>
    [Fact]
    public void AnActiveSessionStillEndsAtTheCeiling()
    {
        Authorize();

        for (var i = 0; i < 200; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            _sessions.AuthorityOf(Caller());
        }

        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller()));
    }

    [Fact]
    public void LockingThePhoneWithdrawsAuthorityAndTheTicket()
    {
        var ticket = Authorize("c1");

        _sessions.Lock(_deviceId, "the phone locked itself");

        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller("c1")));
        Assert.Throws<CapabilityException>(() => _sessions.Resume("c2", _deviceId, ticket));
    }

    /// <summary>
    /// A dropped socket is the ordinary condition on a phone link. Forgetting the connection must
    /// not cost the owner a fingerprint.
    /// </summary>
    [Fact]
    public void ForgettingAConnectionDoesNotRevokeTheTicket()
    {
        var ticket = Authorize("c1");

        _sessions.Forget("c1");

        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller("c1")));

        _sessions.Resume("c3", _deviceId, ticket);
        Assert.Equal(RemoteAuthority.FullRemote, _sessions.AuthorityOf(Caller("c3")));
    }

    // --- the key ------------------------------------------------------------------------------

    [Fact]
    public void RegisteringTheSameKeyTwiceIsFine()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));
    }

    /// <summary>
    /// The property that makes the biometric gate worth having: a phone in the wrong hands is an
    /// authenticated phone, and it must not be able to enroll a key of its own.
    /// </summary>
    [Fact]
    public void ADifferentKeyIsRefusedUntilTheOwnerClearsItAtThePc()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));

        using var replacement = new OwnerPhoneKey();
        Assert.False(_sessions.RegisterOwnerKey(_deviceId, replacement.PublicKey));

        Assert.True(_phones.ClearOwnerKey());
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, replacement.PublicKey));
    }

    [Fact]
    public void TheRegisteredKeySurvivesAReload()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));

        var reopened = new PrimaryPhoneRegistry(
            _state.Directory, new PassThroughSecretProtector(), NullLogger<PrimaryPhoneRegistry>.Instance);

        Assert.True(reopened.Current!.HasOwnerKey);
        Assert.Equal(_key.PublicKey, reopened.Current.OwnerPublicKey);
    }

    [Fact]
    public void UnpairingForgetsTheKey()
    {
        Assert.True(_sessions.RegisterOwnerKey(_deviceId, _key.PublicKey));
        Assert.True(_phones.Unpair());

        var reopened = new PrimaryPhoneRegistry(
            _state.Directory, new PassThroughSecretProtector(), NullLogger<PrimaryPhoneRegistry>.Instance);

        Assert.Null(reopened.Current);
    }

    [Fact]
    public void SomethingThatIsNotAP256KeyIsRefused()
    {
        var refused = Assert.Throws<CapabilityException>(
            () => _sessions.RegisterOwnerKey(_deviceId, Convert.ToBase64String("not a key"u8.ToArray())));

        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);
    }

    /// <summary>
    /// Deciding whether to push an event to a connection is not the owner doing anything. PC Assistant can
    /// stream a reply for minutes; if every event asked through the sliding check, a phone left on the
    /// table with a task running would never time out.
    /// </summary>
    [Fact]
    public void AskingWhetherToPushAnEventDoesNotKeepAnIdleSessionAlive()
    {
        Authorize();
        Assert.True(_sessions.HoldsFullRemote(Caller()));

        for (var minute = 0; minute <= _options.IdleMinutes; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            _sessions.HoldsFullRemote(Caller());
        }

        Assert.False(_sessions.HoldsFullRemote(Caller()));
        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(Caller()));
    }

    [Fact]
    public void OnlyTheAuthorisedConnectionHoldsFullRemoteAndALockEndsIt()
    {
        Authorize("c1");

        Assert.False(_sessions.HoldsFullRemote(Caller("c2")));
        Assert.False(_sessions.HoldsFullRemote(CallerDescriptor.Internal));

        _sessions.Lock(_deviceId, "test");
        Assert.False(_sessions.HoldsFullRemote(Caller("c1")));
    }

    [Fact]
    public void ALocalCallerHasNoOwnerSessionOfItsOwn()
    {
        // Local callers are the owner at the machine and are allowed everything by policy, without
        // ever holding a remote authority. Reporting one here would be a second, quieter grant.
        Assert.Equal(RemoteAuthority.None, _sessions.AuthorityOf(CallerDescriptor.Internal));
    }

    private sealed class AdjustableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
