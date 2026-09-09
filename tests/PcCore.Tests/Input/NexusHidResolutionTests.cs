using PcCore.NativeInput.Hid;
using PcCore.Windows.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// Deciding which devices on this machine are Nexus's, and which kind each one is.
/// <para>
/// <b>Ancestry, not identifiers.</b> A hardware id, a vendor and product id, a friendly name and a
/// container id are all things a device <em>claims</em>, and anything able to install a driver can
/// claim any of them. The device tree is not a claim: Windows records which devnode enumerated
/// which, and every device Nexus publishes is a child of the Nexus control device because VHF
/// created it there.
/// </para>
/// <para>
/// <b>The alternative was tried and it did not work.</b> The first design had the driver assign each
/// device a hardware id through <c>VHF_CONFIG.InstanceID</c> and <c>HardwareIDs</c> and matched Raw
/// Input names against it. Those members are undocumented, and setting them made VhfCreate and
/// VhfStart both return success while VHF enumerated no device at all. Ancestry needs nothing from
/// VHF and is the stronger check anyway.
/// </para>
/// </summary>
public sealed class NexusHidResolutionTests
{
    private const string Control = @"ROOT\DEVGEN\NEXUSHID";

    private const string MousePath =
        @"\\?\HID#HID_DEVICE_SYSTEM_VHF#2&37c680e2&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";

    private const string KeyboardPath =
        @"\\?\HID#HID_DEVICE_SYSTEM_VHF#2&300bea7b&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

    private const string TouchPath =
        @"\\?\HID#HID_DEVICE_SYSTEM_VHF#2&12020798&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private const string RealMouse =
        @"\\?\HID#SYNA32E3&Col01#5&26977154&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";

    private static RawInputDeviceInfo Mouse(string path) => new(path, RawInputDeviceClass.Mouse);

    private static RawInputDeviceInfo Keyboard(string path) => new(path, RawInputDeviceClass.Keyboard);

    private static RawInputDeviceInfo Hid(string path) => new(path, RawInputDeviceClass.Hid);

    /// <summary>Every Nexus device also exposes a generic HID interface on its own devnode.</summary>
    private const string MouseHidPath =
        @"\\?\HID#HID_DEVICE_SYSTEM_VHF#2&37c680e2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private const string KeyboardHidPath =
        @"\\?\HID#HID_DEVICE_SYSTEM_VHF#2&300bea7b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}\KBD";

    /// <summary>
    /// A backend whose device list, HID interfaces and ancestry answers are all scripted.
    /// <paramref name="ours"/> is the set of paths the device tree would say descend from the
    /// control device. A path's PnP instance is taken to be everything before the interface GUID,
    /// which is how Windows actually composes it and is what makes the mouse's HID interface share a
    /// devnode with the mouse.
    /// </summary>
    private static NexusHidBackend Backend(
        IReadOnlyList<RawInputDeviceInfo> devices,
        IReadOnlyList<string>? hidInterfaces = null,
        params string[] ours) =>
        new(
            new NexusHidDeviceRegistry(),
            rawInputDevices: () => devices,
            resolutionTimeout: TimeSpan.Zero,
            descendsFromControl: (path, control) =>
                control == Control && ours.Contains(path, StringComparer.OrdinalIgnoreCase),
            hidInterfacePaths: () => hidInterfaces ?? [],
            instanceIdOf: InstanceOf);

    /// <summary>
    /// The devnode a path belongs to: everything up to the interface GUID. Two interfaces on one
    /// device share it, which is exactly the relationship touch resolution subtracts by.
    /// </summary>
    private static string? InstanceOf(string path)
    {
        var hash = path.LastIndexOf("#{", StringComparison.Ordinal);
        return hash < 0 ? path : path[..hash];
    }

    private static NexusHidBackend.CreatedDevice Device(VirtualHidDeviceKind kind) =>
        new(kind, NexusHidProtocol.ProductIdMouse, Guid.Empty);

    private static NexusHidBackend.CreatedDevice[] AllThree() =>
    [
        Device(VirtualHidDeviceKind.Mouse),
        Device(VirtualHidDeviceKind.Keyboard),
        Device(VirtualHidDeviceKind.Digitizer),
    ];

    // --- the ordinary case -------------------------------------------------------------------------

    [Fact]
    public void AllThreeResolveToTheirOwnRawInputPaths()
    {
        var failures = new List<string>();
        var backend = Backend(
            [Mouse(RealMouse), Mouse(MousePath), Keyboard(KeyboardPath)],
            [MouseHidPath, KeyboardHidPath, TouchPath],
            MousePath, KeyboardPath, TouchPath, MouseHidPath, KeyboardHidPath);

        var resolved = backend.ResolveIdentities(AllThree(), failures, Control);

        Assert.Empty(failures);
        Assert.Equal(3, resolved.Count);
        Assert.Equal(MousePath, resolved.Single(r => r.Kind == VirtualHidDeviceKind.Mouse).DevicePath);
        Assert.Equal(KeyboardPath, resolved.Single(r => r.Kind == VirtualHidDeviceKind.Keyboard).DevicePath);
        Assert.Equal(TouchPath, resolved.Single(r => r.Kind == VirtualHidDeviceKind.Digitizer).DevicePath);
    }

    /// <summary>
    /// The machine's real mouse sits right next to Nexus's and reports the same Raw Input kind. Only
    /// ancestry separates them, which is exactly why identity does not rest on anything else.
    /// </summary>
    [Fact]
    public void ARealDeviceOfTheSameKindIsNotAdopted()
    {
        var backend = Backend([Mouse(RealMouse), Mouse(MousePath)], null, MousePath);

        var identity = Assert.Single(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], [], Control));

        Assert.Equal(MousePath, identity.DevicePath);
        Assert.NotEqual(RealMouse, identity.DevicePath);
    }

    /// <summary>A device that descends from nothing of ours is left alone, whatever it looks like.</summary>
    [Fact]
    public void ADeviceThatDoesNotDescendFromTheControlDeviceIsNeverAdopted()
    {
        var failures = new List<string>();

        // Same path shape, same Raw Input kind, and the tree says it is not ours.
        var backend = Backend([Mouse(MousePath)]);


        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, Control));
        Assert.Single(failures);
    }

    /// <summary>Kind comes from Raw Input's own classification, so a digitizer is not read as a mouse.</summary>
    [Fact]
    public void KindComesFromRawInputRatherThanFromThePath()
    {
        var failures = new List<string>();

        // Ours, and it is a HID interface rather than a mouse - so it satisfies Touch, not Mouse.
        var backend = Backend([], [TouchPath], TouchPath);

        Assert.Single(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Digitizer)], [], Control));
        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, Control));
        Assert.Single(failures);
    }

    /// <summary>A partial set is a real state: adopt what resolved, report what did not.</summary>
    [Fact]
    public void APartialSetAdoptsWhatItCanAndSaysWhatItCannot()
    {
        var failures = new List<string>();
        var backend = Backend([Mouse(MousePath), Keyboard(KeyboardPath)], null, MousePath, KeyboardPath);

        var resolved = backend.ResolveIdentities(AllThree(), failures, Control);

        Assert.Equal(2, resolved.Count);
        Assert.Contains("Digitizer", Assert.Single(failures), StringComparison.Ordinal);
    }

    // --- refusing rather than guessing ---------------------------------------------------------------

    /// <summary>
    /// Two devices of one kind under our own devnode should be impossible. If it happens, both are
    /// refused: adopting the wrong one would mean trusting input Nexus does not drive, and the
    /// security detector would then read a person's typing as Nexus's own.
    /// </summary>
    [Fact]
    public void TwoDevicesOfOneKindAreRefusedRatherThanGuessedBetween()
    {
        var failures = new List<string>();
        var second = MousePath.Replace("2&37c680e2", "2&99999999", StringComparison.Ordinal);
        var backend = Backend([Mouse(MousePath), Mouse(second)], null, MousePath, second);

        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, Control));
        Assert.Contains("refusing to guess", Assert.Single(failures), StringComparison.Ordinal);
    }

    /// <summary>Created, but Windows enumerated nothing. Reported, not silently dropped.</summary>
    [Fact]
    public void ADeviceWindowsNeverEnumeratedIsNotAdopted()
    {
        var failures = new List<string>();
        var backend = Backend([Mouse(RealMouse)]);

        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, Control));
        Assert.Contains("enumerated no", Assert.Single(failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the control device's own instance id there is nothing to compare ancestry against, so
    /// nothing is adopted. Trusting everything would be the alternative, and it is not one.
    /// </summary>
    [Fact]
    public void WithoutTheControlDevicesInstanceNothingIsAdopted()
    {
        var failures = new List<string>();
        var backend = Backend([Mouse(MousePath)], null, MousePath);

        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, null));
        Assert.Contains("could not be resolved", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnEnumeratorNothingIsAdopted()
    {
        var failures = new List<string>();
        var backend = new NexusHidBackend(new NexusHidDeviceRegistry(), resolutionTimeout: TimeSpan.Zero);

        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, Control));
        Assert.Contains("no Raw Input enumerator is wired", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void AThrowingEnumeratorIsTreatedAsFindingNothing()
    {
        var failures = new List<string>();
        var backend = new NexusHidBackend(
            new NexusHidDeviceRegistry(),
            rawInputDevices: () => throw new InvalidOperationException("Raw Input is unavailable"),
            resolutionTimeout: TimeSpan.Zero);

        Assert.Empty(backend.ResolveIdentities([Device(VirtualHidDeviceKind.Mouse)], failures, Control));
        Assert.Single(failures);
    }

    [Fact]
    public void ResolvingNothingIsNotAFailure()
    {
        var failures = new List<string>();
        var backend = Backend([Mouse(MousePath)], null, MousePath);

        Assert.Empty(backend.ResolveIdentities([], failures, Control));
        Assert.Empty(failures);
    }

    // --- the real Configuration Manager walk -------------------------------------------------------

    /// <summary>
    /// Every device Raw Input names by a device interface path resolves to a PnP instance — and some
    /// devices it does not name that way at all.
    /// <para>
    /// <b>This is why the touch screen is not resolved through Raw Input.</b> Raw Input reports a
    /// digitizer as <c>\?\VIRTUAL_DIGITIZER</c>: one synthetic name shared by the whole touch
    /// stack, belonging to no device and resolving to no instance. A mouse and a keyboard get their
    /// real interface paths; a digitizer does not, so it is found through the HID interface class
    /// and the device tree instead.
    /// </para>
    /// </summary>
    [Fact]
    public void RawInputNamesSomeDevicesByPathAndOthersSynthetically()
    {
        var devices = RawInputListener.EnumerateDevices()
            .Where(d => !string.IsNullOrWhiteSpace(d.Path))
            .ToArray();

        Assert.NotEmpty(devices);

        foreach (var device in devices)
        {
            var instance = NexusHidPnpAncestry.InstanceIdOfInterface(device.Path!);

            // A device interface path carries the interface class GUID at the end. Anything else is
            // a synthetic name, and it is expected not to resolve.
            if (device.Path!.Contains("#{", StringComparison.Ordinal))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(instance),
                    $"no PnP instance for the interface path {device.Path}");
            }
            else
            {
                Assert.True(
                    string.IsNullOrWhiteSpace(instance),
                    $"{device.Path} is not an interface path yet resolved to {instance}");
            }
        }
    }

    /// <summary>
    /// A real device descends from itself and from its own enumerator, and does not descend from a
    /// devnode that does not exist. The walk terminates in both directions.
    /// </summary>
    [Fact]
    public void TheAncestryWalkAnswersTrueForSelfAndFalseForAStranger()
    {
        var device = RawInputListener.EnumerateDevices()
            .FirstOrDefault(d => d.Path is { Length: > 0 } p && p.Contains("#{", StringComparison.Ordinal));

        var instance = NexusHidPnpAncestry.InstanceIdOfInterface(device.Path!);
        Assert.NotNull(instance);

        Assert.True(NexusHidPnpAncestry.DescendsFrom(instance, instance!));
        Assert.False(NexusHidPnpAncestry.DescendsFrom(instance, @"ROOT\NOTHING\AT_ALL"));
        Assert.False(NexusHidPnpAncestry.DescendsFrom(null, @"ROOT\DEVGEN\NEXUSHID"));
        Assert.False(NexusHidPnpAncestry.DescendsFrom(instance, string.Empty));
    }
}
