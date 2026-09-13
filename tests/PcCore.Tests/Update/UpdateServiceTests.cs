using System.Security.Cryptography;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Update;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Update;
using PcCore.Windows.Update;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>PC Core answering <c>update.check</c> and serving the APK, against a real release store.</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();
    private readonly byte[] _apk = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);

    public void Dispose() => _fixture.Dispose();

    private NexusUpdateService Service() =>
        new(_fixture.Store, _fixture.Paths, new FakeComponentProbe(), TestLog.For<NexusUpdateService>());

    private void PublishWithApk(int release = 16, int versionCode = 16)
    {
        _fixture.Install(ReleaseFixture.BaseFiles());
        _fixture.Publish(release, "1.3.1", ReleaseFixture.BaseFiles(), android: (versionCode, _apk));
    }

    [Fact]
    public void AnOlderPhoneIsToldAboutTheNewerApk()
    {
        PublishWithApk();

        var result = Service().Check(refresh: true, clientVersionCode: 15, includePcDetails: false);

        Assert.True(result.ReleaseAvailable);
        Assert.NotNull(result.Android);
        Assert.True(result.Android!.UpdateAvailable);
        Assert.False(result.Android.Required);
        Assert.Equal(16, result.Android.VersionCode);
        Assert.Equal(_apk.Length, result.Android.SizeBytes);
        Assert.Equal(PcCore.Update.FileHash.Sha256OfBytes(_apk), result.Android.Sha256);
    }

    [Fact]
    public void APhoneAlreadyOnThatBuildIsNotOfferedIt()
    {
        PublishWithApk();

        Assert.False(Service().Check(true, 16, false).Android!.UpdateAvailable);
        Assert.False(Service().Check(true, 99, false).Android!.UpdateAvailable);
    }

    [Fact]
    public void APhoneBelowTheMinimumIsToldTheUpdateIsRequired()
    {
        PublishWithApk();

        var result = Service().Check(true, ClientCompatibility.MinimumAndroidVersionCode - 1, false);

        Assert.True(result.Android!.Required);
        Assert.True(result.Android.UpdateAvailable);
    }

    [Fact]
    public void ThePhoneSeesNoDetailOfThePcsPrivilegedComponents()
    {
        PublishWithApk();

        var local = Service().Check(true, null, includePcDetails: true);
        var remote = Service().Check(true, 15, includePcDetails: false);

        Assert.NotEmpty(local.Components);
        Assert.Empty(remote.Components);
        Assert.Equal(0, remote.PcChangedFiles);
    }

    [Fact]
    public async Task TheApkIsServedInRangesThatReassembleToItsManifestHash()
    {
        PublishWithApk();
        var service = Service();
        var token = TestContext.Current.CancellationToken;

        using var assembled = new MemoryStream();
        long offset = 0;
        AndroidPackageChunk chunk;

        do
        {
            chunk = await service.ReadAndroidPackageAsync(16, offset, GetAndroidPackageAction.DefaultChunkBytes, token);
            var bytes = Convert.FromBase64String(chunk.Data ?? string.Empty);
            assembled.Write(bytes);
            offset += bytes.Length;
        }
        while (!chunk.EndOfFile);

        Assert.Equal(_apk, assembled.ToArray());
        Assert.Equal(PcCore.Update.FileHash.Sha256OfBytes(assembled.ToArray()), chunk.Sha256);
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesFromTheLengthThePhoneAlreadyHas()
    {
        PublishWithApk();
        var service = Service();
        var token = TestContext.Current.CancellationToken;

        var first = await service.ReadAndroidPackageAsync(16, 0, 1000, token);

        // The link drops. The phone kept 1000 bytes in its .part file and asks from there.
        var resumed = await service.ReadAndroidPackageAsync(16, 1000, 1000, token);

        Assert.Equal(_apk[..1000], Convert.FromBase64String(first.Data!));
        Assert.Equal(_apk[1000..2000], Convert.FromBase64String(resumed.Data!));
        Assert.Equal(1000, resumed.Offset);
    }

    [Fact]
    public async Task AChunkOfABuildNoLongerOfferedIsRefusedRatherThanMixed()
    {
        PublishWithApk(release: 16, versionCode: 16);
        var service = Service();
        var token = TestContext.Current.CancellationToken;

        await service.ReadAndroidPackageAsync(16, 0, 1000, token);

        // A new release is published during the download.
        _fixture.Publish(17, "1.3.2", ReleaseFixture.BaseFiles(), android: (17, RandomNumberGenerator.GetBytes(4096)));

        var ex = await Assert.ThrowsAsync<CapabilityException>(() => service.ReadAndroidPackageAsync(16, 1000, 1000, token).AsTask());
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task AnApkThatDoesNotMatchItsManifestIsNeverServed()
    {
        PublishWithApk();
        var apk = Directory.EnumerateFiles(Path.Combine(_fixture.Store.Root, "16", "android")).Single();

        var bytes = File.ReadAllBytes(apk);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(apk, bytes);

        var ex = await Assert.ThrowsAsync<CapabilityException>(() =>
            Service().ReadAndroidPackageAsync(16, 0, 1000, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(ErrorCodes.Unsupported, ex.Code);
    }

    [Fact]
    public void WithNothingPublishedTheAnswerSaysSo()
    {
        var result = Service().Check(true, 15, false);

        Assert.False(result.ReleaseAvailable);
        Assert.Null(result.Android);
        Assert.False(result.PcUpdateAvailable);
        Assert.Contains("No Nexus release", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallingOnThePcRefusesWhenThereIsNothingToInstall()
    {
        var files = ReleaseFixture.BaseFiles();
        _fixture.Install(files);
        _fixture.Publish(15, "1.3.0", files);

        var started = Service().StartPcUpdate();

        Assert.False(started.Started);
        Assert.Contains("already has", started.Message, StringComparison.Ordinal);
    }
}

/// <summary>The policy's half of the old-client path.</summary>
public sealed class UpdateRequiredPolicyTests
{
    private static readonly CallerDescriptor OldPhone =
        new CallerDescriptor("remote-ws", "c1", ActorType.Device, "phone") with { ClientVersionCode = 3, RequiresUpdate = true };

    [Theory]
    [InlineData("update.check")]
    [InlineData("update.apk.get")]
    public void AnAppTooOldForThisPcMayStillReachTheUpdate(string action) =>
        Assert.True(new DefaultCapabilityPolicy(new FullAuthority()).Evaluate(action, OldPhone).Allowed);

    [Theory]
    [InlineData("screen.stream.start")]
    [InlineData("input.pointer")]
    [InlineData("files.list")]
    [InlineData("security.evidence.get")]
    [InlineData("system.get_info")]
    [InlineData("session.authorize")]
    [InlineData("update.install")]
    public void AndNothingElseEvenWithAnOwnerAuthenticatedSession(string action)
    {
        var decision = new DefaultCapabilityPolicy(new FullAuthority()).Evaluate(action, OldPhone);

        Assert.False(decision.Allowed);
        Assert.Equal(ErrorCodes.UpdateRequired, decision.Code);
    }

    [Fact]
    public void StartingAPcUpdateIsNeverAvailableToThePhone()
    {
        var current = new CallerDescriptor("remote-ws", "c1", ActorType.Device, "phone") with { ClientVersionCode = 99 };

        Assert.False(new DefaultCapabilityPolicy(new FullAuthority()).Evaluate("update.install", current).Allowed);
        Assert.True(new DefaultCapabilityPolicy(new FullAuthority()).Evaluate("update.install", CallerDescriptor.Internal).Allowed);
    }

    [Fact]
    public void APhoneThatReportsNoVersionIsTheLastBuildBeforeTheUpdatePath()
    {
        Assert.False(ClientCompatibility.RequiresUpdate(null, minimum: ClientCompatibility.LegacyVersionCode));
        Assert.True(ClientCompatibility.RequiresUpdate(null, minimum: ClientCompatibility.LegacyVersionCode + 1));
        Assert.True(ClientCompatibility.RequiresUpdate(15, minimum: 16));
        Assert.False(ClientCompatibility.RequiresUpdate(16, minimum: 16));
    }

    private sealed class FullAuthority : IRemoteAuthorityProvider
    {
        public RemoteAuthority AuthorityOf(CallerDescriptor caller) => RemoteAuthority.FullRemote;
    }
}
