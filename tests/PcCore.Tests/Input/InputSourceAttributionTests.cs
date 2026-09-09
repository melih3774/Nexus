using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The security invariant that decides whether virtual HID may ever be switched on:
/// <code>
/// Nexus Mouse / Keyboard      -> NEVER starts a security session
/// physical mouse / keyboard   -> CAN start one while ARMED
/// </code>
/// <para>
/// Tested against synthetic device identities because no Nexus device can be enumerated on this
/// machine yet. That is not a weaker test than the real one for the property that matters: the
/// classifier's whole job is to compare an observed device path against the set Nexus published, and
/// whether those paths came from a real driver or from this file does not change the logic being
/// checked. What it cannot prove is that Windows reports the path Nexus expects — that is the real
/// installed-device test, and it is listed as a blocker rather than assumed.
/// </para>
/// </summary>
public sealed class InputSourceAttributionTests
{
    private const string NexusMousePath = @"\\?\HID#VID_F055&PID_0001#6&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string NexusKeyboardPath = @"\\?\HID#VID_F055&PID_0002#6&1a2b3c4d&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string RealKeyboardPath = @"\\?\HID#VID_046D&PID_C31C#7&2f8e9a1b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string RealMousePath = @"\\?\HID#VID_045E&PID_00CB#8&3c7d2e5f&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";

    private static VirtualHidDeviceIdentity Identity(VirtualHidDeviceKind kind, string path) => new()
    {
        Kind = kind,
        DevicePath = path,
        FriendlyName = kind switch
        {
            VirtualHidDeviceKind.Mouse => "Nexus Mouse",
            VirtualHidDeviceKind.Keyboard => "Nexus Keyboard",
            _ => "Nexus Touch",
        },
        VendorId = NexusHidProtocol.VendorId,
    };

    [Fact]
    public void APublishedNexusDeviceIsTrusted()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));

        var verdict = registry.Classify(NexusMousePath);

        Assert.True(verdict.IsNexus);
        Assert.Equal(InputSourceReason.MatchedPublishedDevice, verdict.Reason);
    }

    [Fact]
    public void APhysicalKeyboardIsNotTrustedEvenWhileNexusDevicesExist()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));
        registry.Register(Identity(VirtualHidDeviceKind.Keyboard, NexusKeyboardPath));

        var verdict = registry.Classify(RealKeyboardPath);

        Assert.Equal(InputSource.Physical, verdict.Source);
        Assert.Equal(InputSourceReason.NotAPublishedDevice, verdict.Reason);
    }

    /// <summary>
    /// The state this machine is in today. Nothing published means nothing is trusted, which is the
    /// only honest answer and also the safe one.
    /// </summary>
    [Fact]
    public void WithNothingPublishedEveryDeviceIsPhysical()
    {
        var registry = new NexusHidDeviceRegistry();

        Assert.True(registry.IsEmpty);
        Assert.Equal(InputSource.Physical, registry.Classify(NexusMousePath).Source);
        Assert.Equal(InputSourceReason.NothingPublished, registry.Classify(NexusMousePath).Reason);
    }

    /// <summary>
    /// A device Windows cannot name is physical. This is the failure the classifier is most likely to
    /// meet in practice — a device removed between the event arriving and the path being queried —
    /// and rounding it towards "trusted" would mean an intruder could earn trust by unplugging.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnresolvableDeviceIsPhysical(string? path)
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));

        var verdict = registry.Classify(path);

        Assert.Equal(InputSource.Physical, verdict.Source);
        Assert.Equal(InputSourceReason.Unresolvable, verdict.Reason);
    }

    /// <summary>
    /// <b>The impersonation case, and the reason identity is a path rather than a name.</b> A device
    /// carrying Nexus's own vendor and product ids, that Nexus did not publish, must not be trusted.
    /// Anything that can install a driver can publish those ids; only this process knows what it
    /// actually created.
    /// </summary>
    [Fact]
    public void ADeviceWearingNexusIdentifiersIsNotTrustedUnlessNexusPublishedIt()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));

        const string impostor =
            @"\\?\HID#VID_F055&PID_0001#9&deadbeef&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        var verdict = registry.Classify(impostor);

        Assert.Equal(InputSource.Physical, verdict.Source);
        Assert.Equal(InputSourceReason.NotAPublishedDevice, verdict.Reason);

        // It still *looks* like one, and saying so is a better diagnostic than silence.
        Assert.True(NexusHidDeviceRegistry.LooksLikeNexusIdentifiers(impostor));
    }

    [Fact]
    public void ForgettingADeviceMakesItPhysicalAgain()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));
        Assert.True(registry.Classify(NexusMousePath).IsNexus);

        Assert.True(registry.Forget(NexusMousePath));

        Assert.Equal(InputSource.Physical, registry.Classify(NexusMousePath).Source);
    }

    /// <summary>
    /// Losing the backend must not leave trusted paths behind. If Nexus cannot be sure what it owns,
    /// it owns nothing.
    /// </summary>
    [Fact]
    public void ClearingTheRegistryUntrustsEverything()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));
        registry.Register(Identity(VirtualHidDeviceKind.Keyboard, NexusKeyboardPath));

        registry.Clear();

        Assert.True(registry.IsEmpty);
        Assert.Equal(InputSource.Physical, registry.Classify(NexusMousePath).Source);
        Assert.Equal(InputSource.Physical, registry.Classify(NexusKeyboardPath).Source);
    }

    /// <summary>
    /// Plugging in another mouse must not need a restart and must not need Nexus to know about it:
    /// anything not in the registry is already physical, so a newly arrived device is handled by the
    /// default rather than by an update.
    /// </summary>
    [Fact]
    public void ANewlyArrivedPhysicalDeviceNeedsNoRegistration()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));

        var verdict = registry.Classify(RealMousePath);

        Assert.Equal(InputSource.Physical, verdict.Source);
    }

    [Fact]
    public void PathComparisonIgnoresCaseBecauseWindowsDoes()
    {
        var registry = new NexusHidDeviceRegistry();
        registry.Register(Identity(VirtualHidDeviceKind.Mouse, NexusMousePath));

        Assert.True(registry.Classify(NexusMousePath.ToUpperInvariant()).IsNexus);
        Assert.True(registry.Classify(NexusMousePath.ToLowerInvariant()).IsNexus);
    }

    [Fact]
    public void AnIdentityWithNoDevicePathIsRefused()
    {
        var registry = new NexusHidDeviceRegistry();

        Assert.False(registry.Register(Identity(VirtualHidDeviceKind.Mouse, "  ")));
        Assert.True(registry.IsEmpty);
    }

    [Fact]
    public void APhysicalDevicePathIsNotMistakenForNexusIdentifiers()
    {
        Assert.False(NexusHidDeviceRegistry.LooksLikeNexusIdentifiers(RealKeyboardPath));
        Assert.False(NexusHidDeviceRegistry.LooksLikeNexusIdentifiers(null));
    }
}
