using System.Runtime.InteropServices;

using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// Everything about the driver package that can be checked without installing it, or even without
/// compiling it.
/// <para>
/// The WDK is not present on this machine, so the driver cannot be built here and InfVerif cannot be
/// run. That rules out a real package validation and rules in this one: the ABI is checkable from
/// managed code, and the INF is a text file with rules about its own contents. Both are the kinds of
/// mistake that would otherwise be found at install time, on the one deliberate pass where the owner
/// is watching.
/// </para>
/// </summary>
public sealed class NexusHidPackageTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PcCore.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string DriverDirectory() => Path.Combine(RepositoryRoot(), "src", "driver", "NexusHid");

    private static string InfText() => File.ReadAllText(Path.Combine(DriverDirectory(), "NexusHid.inf"));

    private static string HeaderText() =>
        File.ReadAllText(Path.Combine(DriverDirectory(), "NexusHidProtocol.h"));

    /// <summary>
    /// The INF with its comments removed, which is what Windows actually acts on.
    /// <para>
    /// Needed because the checks that assert something is <em>absent</em> would otherwise be
    /// checking the prose. The first version of this file failed on the word "Microsoft" inside a
    /// comment citing Microsoft's own VHF documentation, and on ".sys" inside a comment explaining
    /// that there deliberately is not one. Both were the test being wrong; a comment is not a
    /// directive.
    /// </para>
    /// </summary>
    private static string InfDirectives()
    {
        var lines = InfText()
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf(';');
                return comment >= 0 ? line[..comment] : line;
            })
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join('\n', lines);
    }

    // ---------------------------------------------------------------- ABI

    /// <summary>
    /// The managed structs must be exactly the sizes the C header asserts. Both sides are pinned to
    /// literals rather than to each other, so a field moving on either side fails here or fails the
    /// driver build — never silently agrees on a new layout.
    /// </summary>
    [Fact]
    public void TheManagedStructsMatchTheDeclaredAbiSizes()
    {
        Assert.Equal(NexusHidProtocol.Sizes.VersionReply, Marshal.SizeOf<NexusHidVersionReply>());
        Assert.Equal(NexusHidProtocol.Sizes.CreateRequest, Marshal.SizeOf<NexusHidCreateRequest>());
        Assert.Equal(NexusHidProtocol.Sizes.CreateReply, Marshal.SizeOf<NexusHidCreateReply>());
        Assert.Equal(NexusHidProtocol.Sizes.SubmitRequest, Marshal.SizeOf<NexusHidSubmitRequest>());
        Assert.Equal(NexusHidProtocol.Sizes.DeviceStatus, Marshal.SizeOf<NexusHidDeviceStatus>());
        Assert.Equal(NexusHidProtocol.Sizes.StatusReply, Marshal.SizeOf<NexusHidStatusReply>());
        Assert.Equal(NexusHidProtocol.Sizes.NeutralizeRequest, Marshal.SizeOf<NexusHidNeutralizeRequest>());
        Assert.Equal(NexusHidProtocol.Sizes.DeleteRequest, Marshal.SizeOf<NexusHidDeleteRequest>());
    }

    /// <summary>
    /// And the C header must assert the same numbers this file does. Reading the header as text is
    /// crude and it is also the only check available while the WDK is missing.
    /// </summary>
    [Fact]
    public void TheCHeaderAssertsTheSameAbiSizes()
    {
        var header = HeaderText();

        Assert.Contains($"C_ASSERT(sizeof(NEXUS_HID_VERSION_REPLY)      == {NexusHidProtocol.Sizes.VersionReply});", header);
        Assert.Contains($"C_ASSERT(sizeof(NEXUS_HID_CREATE_REQUEST)     == {NexusHidProtocol.Sizes.CreateRequest});", header);
        Assert.Contains($"C_ASSERT(sizeof(NEXUS_HID_CREATE_REPLY)       == {NexusHidProtocol.Sizes.CreateReply});", header);
        Assert.Contains($"C_ASSERT(sizeof(NEXUS_HID_SUBMIT_REQUEST)     == {NexusHidProtocol.Sizes.SubmitRequest});", header);
        Assert.Contains($"C_ASSERT(sizeof(NEXUS_HID_DEVICE_STATUS)      == {NexusHidProtocol.Sizes.DeviceStatus});", header);
        Assert.Contains($"C_ASSERT(sizeof(NEXUS_HID_STATUS_REPLY)       == {NexusHidProtocol.Sizes.StatusReply});", header);
    }

    /// <summary>
    /// The IOCTL codes are computed on both sides from the same CTL_CODE arithmetic. Pinned to
    /// literals because a control code that drifts is a request silently landing on the wrong
    /// handler.
    /// </summary>
    [Fact]
    public void TheIoctlCodesAreStable()
    {
        Assert.Equal(0x22A000u, NexusHidProtocol.IoctlGetVersion);
        Assert.Equal(0x22A004u, NexusHidProtocol.IoctlCreateDevice);
        Assert.Equal(0x22A008u, NexusHidProtocol.IoctlSubmitReport);
        Assert.Equal(0x22A00Cu, NexusHidProtocol.IoctlGetStatus);
        Assert.Equal(0x22A010u, NexusHidProtocol.IoctlNeutralize);
        Assert.Equal(0x22A014u, NexusHidProtocol.IoctlDeleteDevice);
    }

    /// <summary>
    /// The control surface is six operations. This test exists to make adding a seventh a deliberate
    /// act with a failing test attached, rather than a convenience someone adds while debugging.
    /// </summary>
    [Fact]
    public void TheControlSurfaceIsExactlySixOperations()
    {
        var header = HeaderText();
        var count = header.Split('\n').Count(line => line.TrimStart().StartsWith("#define IOCTL_NEXUS_HID_", StringComparison.Ordinal));

        Assert.Equal(6, count);
    }

    /// <summary>Every descriptor has to fit the buffer the create request carries it in.</summary>
    [Fact]
    public void EveryDescriptorFitsTheCreateRequest()
    {
        Assert.True(HidReportDescriptors.Mouse.Length <= NexusHidProtocol.MaxDescriptorLength);
        Assert.True(HidReportDescriptors.Keyboard.Length <= NexusHidProtocol.MaxDescriptorLength);
        Assert.True(HidReportDescriptors.Digitizer.Length <= NexusHidProtocol.MaxDescriptorLength);
    }

    /// <summary>And every report has to fit the submit request.</summary>
    [Fact]
    public void EveryReportFitsTheSubmitRequest()
    {
        Assert.True(HidReportDescriptors.MouseReportLength <= NexusHidProtocol.MaxReportLength);
        Assert.True(HidReportDescriptors.KeyboardReportLength <= NexusHidProtocol.MaxReportLength);
        Assert.True(HidReportDescriptors.DigitizerReportLength <= NexusHidProtocol.MaxReportLength);
    }

    // ---------------------------------------------------------------- INF

    [Fact]
    public void TheInfDeclaresAUserModeDriverAndNotAKernelService()
    {
        var inf = InfDirectives();

        // The whole design rests on this being a DLL in WUDFHost rather than a .sys in the kernel.
        Assert.Contains("NexusHidDriver.dll", inf, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UmdfService", inf, StringComparison.Ordinal);
        Assert.DoesNotContain(".sys", inf, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// VHF must be declared as a lower filter or the device enumerates and never produces a HID
    /// collection. This is the single line whose absence is hardest to diagnose at install time.
    /// </summary>
    [Fact]
    public void TheInfDeclaresVhfAsALowerFilter()
    {
        Assert.Contains(@"HKR,,""LowerFilters"",0x00010000,""vhf""", InfText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both decorated sections must exist: Windows 11 needs the UMDF reflector as an upper filter and
    /// Windows 10 does not, and a package carrying only one of them installs on one and dies on the
    /// other.
    /// </summary>
    [Fact]
    public void TheInfCoversBothWindows10AndWindows11()
    {
        var inf = InfText();

        Assert.Contains("NT$ARCH$.10.0...19041", inf, StringComparison.Ordinal);
        Assert.Contains("NT$ARCH$.10.0...22000", inf, StringComparison.Ordinal);
        Assert.Contains("WUDFRD_UpperFilter", inf, StringComparison.Ordinal);
        Assert.Contains("vhfservice", inf, StringComparison.Ordinal);
    }

    /// <summary>A root-enumerated device: no bus, no hardware, no USB vendor id allocation.</summary>
    [Fact]
    public void TheDeviceIsRootEnumerated()
    {
        Assert.Contains(@"Root\NexusHid", InfText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// PnpLockdown keeps the driver's files from being replaced in place after installation. Its
    /// absence is a supply-chain hole rather than a functional bug, which is exactly the kind that
    /// survives testing.
    /// </summary>
    [Fact]
    public void TheInfSetsPnpLockdown()
    {
        Assert.Matches(@"PnpLockdown\s*=\s*1", InfText());
    }

    [Fact]
    public void TheInfNamesTheCatalogItWillBeSignedWith()
    {
        Assert.Matches(@"CatalogFile\s*=\s*NexusHid\.cat", InfText());
    }

    /// <summary>
    /// Nexus must not claim another company's identity. The provider and the device strings are
    /// Nexus's own, and no other vendor name appears anywhere in the package.
    /// </summary>
    [Theory]
    [InlineData("Logitech")]
    [InlineData("Microsoft")]
    [InlineData("Samsung")]
    [InlineData("Synaptics")]
    [InlineData("Realtek")]
    public void TheInfDoesNotImpersonateAHardwareVendor(string vendor)
    {
        Assert.DoesNotContain(vendor, InfDirectives(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheInfIdentifiesNexusAsTheProvider()
    {
        var inf = InfText();

        Assert.Matches(@"ManufacturerName\s*=\s*""Nexus""", inf);
        Assert.Contains("Provider    = %ManufacturerName%", inf, StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver package has no business installing a service, a task, a network component or a
    /// co-installer. Checking by absence is crude and it catches the case where someone pastes a
    /// section in from a sample without reading it.
    /// </summary>
    [Theory]
    [InlineData("AddService")]
    [InlineData("CoInstallers")]
    [InlineData("RunOnce")]
    [InlineData("AddNetworkComponent")]
    public void TheInfGrantsNoUnrelatedPrivileges(string directive)
    {
        Assert.DoesNotContain(directive, InfDirectives(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The interface GUID has to be the same value in the header and in the managed mirror; they are
    /// written in different notations, which is precisely how they drift.
    /// </summary>
    [Fact]
    public void TheControlInterfaceGuidMatchesBetweenTheHeaderAndManagedCode()
    {
        var header = HeaderText();
        var guid = NexusHidProtocol.ControlInterface;
        var parts = guid.ToString("x").Split(',');

        // The header writes it as DEFINE_GUID(0x6d1f4a2e, 0x9c3b, 0x4e7a, ...).
        Assert.Contains($"0x{guid.ToString("N")[..8]}", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GUID_DEVINTERFACE_NEXUS_HID_CONTROL", header, StringComparison.Ordinal);
        Assert.NotEmpty(parts);
    }

    /// <summary>The vendor id in the header and in managed code must be the same number.</summary>
    [Fact]
    public void TheVendorIdMatchesBetweenTheHeaderAndManagedCode()
    {
        Assert.Contains(
            $"#define NEXUS_HID_VENDOR_ID   0x{NexusHidProtocol.VendorId:X4}",
            HeaderText(),
            StringComparison.OrdinalIgnoreCase);
    }
}
