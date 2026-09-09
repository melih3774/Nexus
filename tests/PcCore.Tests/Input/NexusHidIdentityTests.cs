using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// Device identity, slot ownership and report submission — the three defects the first real install
/// exposed, pinned on the side that can be tested without a driver.
/// <para>
/// <b>All three were invisible until the driver ran.</b> The package built clean, the descriptors
/// were right, and <c>VhfCreate</c> and <c>VhfStart</c> succeeded for all three devices on the very
/// first attempt. What did not work was everything after that: the reply carried no identity, so PC
/// Core refused three devices Windows had happily enumerated; handle cleanup neutralised but never
/// deleted, so the devices from a dead client blocked every later one for the rest of the boot; and
/// the submit path hardcoded report id 0, which stopped being right when the digitizer gained
/// <c>REPORT_ID 1</c>.
/// </para>
/// <para>
/// The driver is C and cannot be linked into this suite, so the driver half of these read the
/// source — the same approach <see cref="NeutralReportTests"/> uses, and for the same reason: a
/// property that can only be checked by installing is a property nobody checks.
/// </para>
/// </summary>
public sealed class NexusHidIdentityTests
{
    /// <summary>
    /// The driver source, with line endings normalised. The assertions below anchor on multi-line
    /// signatures, and whether the file is checked out with CRLF is not a property worth testing.
    /// </summary>
    private static string DriverSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "driver", "NexusHid", "Driver.c"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

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

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from > 0, $"could not find '{start}'");

        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"could not find '{end}' after '{start}'");

        return source[from..to];
    }

    // --- identity --------------------------------------------------------------------------------

    /// <summary>
    /// <b>The regression guard that matters most in this file.</b> Setting
    /// <c>VHF_CONFIG.InstanceID</c> or <c>HardwareIDs</c> makes VhfCreate and VhfStart both return
    /// success while VHF enumerates no device at all — no devnode, no entry in setupapi.dev.log,
    /// nothing for Windows to install. Both members are undocumented: the VHF_CONFIG reference lists
    /// them, and their two length members, with empty descriptions while describing everything else.
    /// <para>
    /// This cost an install cycle to find and would cost another to re-find, so it is pinned here.
    /// </para>
    /// </summary>
    [Fact]
    public void TheDriverSetsNoUndocumentedVhfIdentityField()
    {
        var source = DriverSource();

        Assert.DoesNotContain("config.InstanceID", source, StringComparison.Ordinal);
        Assert.DoesNotContain("config.InstanceIDLength", source, StringComparison.Ordinal);
        Assert.DoesNotContain("config.HardwareIDs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("config.HardwareIDsLength", source, StringComparison.Ordinal);
    }

    /// <summary>The one documented identity member is set, and it is the same for all three.</summary>
    [Fact]
    public void TheDriverSetsTheContainerIdAndNothingElse()
    {
        var source = DriverSource();

        Assert.Contains("config.ContainerID = NexusHidContainerId;", source, StringComparison.Ordinal);
        Assert.Contains("Reply->ContainerId = NexusHidContainerId;", source, StringComparison.Ordinal);

        // One constant, so Windows groups the three devices as one piece of hardware.
        Assert.Equal(1, Occurrences(source, "static const GUID NexusHidContainerId"));
    }

    /// <summary>
    /// The driver asserts no identity a caller could also assert. Nothing in the create path reads a
    /// name, a hardware id or an instance id out of the request, because a device that could name
    /// itself could name itself someone else's.
    /// </summary>
    [Fact]
    public void IdentityIsNeverTakenFromTheRequest()
    {
        var create = Between(DriverSource(), "NexusHidCreateDevice(", "NexusHidSubmitReport(");

        Assert.DoesNotContain("Request->InstanceId", create, StringComparison.Ordinal);
        Assert.DoesNotContain("Request->HardwareId", create, StringComparison.Ordinal);
        Assert.DoesNotContain("Request->Name", create, StringComparison.Ordinal);
    }

    /// <summary>
    /// The create reply carries no device path, because the driver has no supported way to learn one.
    /// A reply field that could only ever be empty is worse than no field: it invites a caller to
    /// trust it.
    /// </summary>
    [Fact]
    public void TheCreateReplyClaimsNoDevicePath()
    {
        Assert.Equal(24, NexusHidProtocol.Sizes.CreateReply);
        Assert.Equal(
            NexusHidProtocol.Sizes.CreateReply,
            System.Runtime.InteropServices.Marshal.SizeOf<NexusHidCreateReply>());
    }

    private static int Occurrences(string source, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    // --- ownership, cleanup and stale slots --------------------------------------------------------

    /// <summary>A created device records the handle that created it; a failed one records nothing.</summary>
    [Fact]
    public void CreateRecordsTheOwnerAndFailureClearsIt()
    {
        var create = Between(DriverSource(), "NexusHidCreateDevice(", "static NTSTATUS\nNexusHidSubmitReport");

        Assert.Contains("slot->Owner = Owner;", create, StringComparison.Ordinal);

        var failed = create[create.IndexOf("Failed:", StringComparison.Ordinal)..];
        Assert.Contains("slot->Owner = NULL;", failed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stale-slot defect, pinned. Handle cleanup must release — neutralise <em>and</em> delete —
    /// not merely neutralise. The slot state lives in the control device's context, which outlives
    /// every client handle, so a cleanup that only neutralises leaves the devices Ready forever and
    /// every later client is refused by the already-exists guard.
    /// </summary>
    [Fact]
    public void HandleCleanupReleasesRatherThanOnlyNeutralizing()
    {
        var cleanup = Between(DriverSource(), "NexusHidEvtFileCleanup(\n    _In_ WDFFILEOBJECT FileObject", "NexusHidEvtDeviceCleanup(");

        Assert.Contains("NexusHidReleaseOwned(control, FileObject);", cleanup, StringComparison.Ordinal);
    }

    /// <summary>Cleanup releases only what that handle made, so a second handle is not a demolition tool.</summary>
    [Fact]
    public void CleanupTouchesOnlyTheOwningHandlesDevices()
    {
        var source = DriverSource();

        // The file-object cleanup passes its own file object, never NULL - NULL means "everything"
        // and is correct only when the device itself is going away.
        Assert.Contains("NexusHidReleaseOwned(control, FileObject);", source, StringComparison.Ordinal);
        Assert.Contains("NexusHidReleaseOwned(control, NULL);", source, StringComparison.Ordinal);

        var release = Between(source, "NexusHidReleaseOwned(\n    _In_ NEXUS_CONTROL_CONTEXT* Control", "/* ------------------------------------------------------------------ IOCTL */");
        Assert.Contains("if (Owner != NULL && Control->Devices[kind].Owner != Owner)", release, StringComparison.Ordinal);
    }

    /// <summary>Release neutralises before it deletes: let go, then leave.</summary>
    [Fact]
    public void ReleaseNeutralizesBeforeDeleting()
    {
        var release = Between(DriverSource(), "NexusHidReleaseOwned(\n    _In_ NEXUS_CONTROL_CONTEXT* Control", "/* ------------------------------------------------------------------ IOCTL */");

        var neutralize = release.IndexOf("NexusHidNeutralizeOwned(Control, Owner);", StringComparison.Ordinal);
        var delete = release.IndexOf("NexusHidDeleteDevice(Control, kind);", StringComparison.Ordinal);

        Assert.True(neutralize > 0, "release does not neutralise");
        Assert.True(delete > neutralize, "release deletes before it neutralises, which strands held input");
    }

    /// <summary>
    /// Deleting a slot clears everything about it, so the next create starts from nothing rather
    /// than inheriting a dead client's owner or identity.
    /// </summary>
    [Fact]
    public void DeletingASlotClearsEverythingAboutIt()
    {
        var delete = Between(DriverSource(), "NexusHidDeleteDevice(\n    _In_ NEXUS_CONTROL_CONTEXT* Control", "/*\n * Release everything one owner published");

        Assert.Contains("slot->State = NexusHidDeviceAbsent;", delete, StringComparison.Ordinal);
        Assert.Contains("slot->Owner = NULL;", delete, StringComparison.Ordinal);
        Assert.Contains("slot->ReportId = 0;", delete, StringComparison.Ordinal);
        Assert.Contains("slot->DescriptorLength = 0;", delete, StringComparison.Ordinal);
        Assert.Contains("slot->ExpectedReportLength = 0;", delete, StringComparison.Ordinal);
    }

    /// <summary>
    /// An occupied slot is refused, and the refusal says which kind of occupied: the same handle
    /// asking twice is a caller bug, another handle's device is not this caller's to take.
    /// </summary>
    [Fact]
    public void AnOccupiedSlotDistinguishesTheOwnerFromAStranger()
    {
        Assert.Contains(
            "return slot->Owner == Owner ? STATUS_INVALID_DEVICE_REQUEST : STATUS_DEVICE_BUSY;",
            DriverSource(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the owner may drive a device. Opening the control device is not permission to move the
    /// pointer — reports submitted through it arrive as hardware.
    /// </summary>
    [Fact]
    public void OnlyTheOwnerMaySubmitReports()
    {
        var submit = Between(DriverSource(), "NexusHidSubmitReport(\n    _In_ NEXUS_CONTROL_CONTEXT* Control", "RtlZeroMemory(&packet");

        Assert.Contains("if (slot->Owner != Owner) {", submit, StringComparison.Ordinal);
        Assert.Contains("return STATUS_ACCESS_DENIED;", submit, StringComparison.Ordinal);
    }

    /// <summary>And only the owner may delete, or a stray handle could unpublish PC Core's devices.</summary>
    [Fact]
    public void OnlyTheOwnerMayDeleteADevice()
    {
        var ioctl = Between(DriverSource(), "case IOCTL_NEXUS_HID_DELETE_DEVICE:", "default:");

        Assert.Contains("Owner != owner", ioctl, StringComparison.Ordinal);
        Assert.Contains("status = STATUS_ACCESS_DENIED;", ioctl, StringComparison.Ordinal);
    }

    /// <summary>Neutralising is owner-scoped too: releasing another owner's held input generates input.</summary>
    [Fact]
    public void NeutralizeIsScopedToTheCallersOwnDevices()
    {
        var ioctl = Between(DriverSource(), "case IOCTL_NEXUS_HID_NEUTRALIZE:", "case IOCTL_NEXUS_HID_DELETE_DEVICE:");

        Assert.Contains("NexusHidNeutralizeOwned(control, owner);", ioctl, StringComparison.Ordinal);
    }

    /// <summary>A request with no file object cannot reach any owning path — NULL means "everything".</summary>
    [Fact]
    public void ARequestWithoutAFileObjectCannotReachTheOwningPaths()
    {
        var source = DriverSource();

        Assert.Contains("WDFFILEOBJECT owner = WdfRequestGetFileObject(Request);", source, StringComparison.Ordinal);
        Assert.Contains("if (owner == NULL &&", source, StringComparison.Ordinal);
    }

    // --- report id -------------------------------------------------------------------------------

    /// <summary>
    /// The submit path sends the device's own report id. It sent 0, with a comment explaining that
    /// the descriptors declare none — true when written, and false from the moment the digitizer
    /// gained <c>REPORT_ID 1</c>, which Windows requires on a touch screen.
    /// </summary>
    [Fact]
    public void SubmitUsesTheDevicesOwnReportId()
    {
        var submit = Between(DriverSource(), "NexusHidSubmitReport(\n    _In_ NEXUS_CONTROL_CONTEXT* Control", "static VOID");

        Assert.Contains("packet.reportId = slot->ReportId;", submit, StringComparison.Ordinal);
        Assert.DoesNotContain("packet.reportId = 0;", submit, StringComparison.Ordinal);
    }

    /// <summary>Neither path hardcodes an id any more — they were meant to agree and did not.</summary>
    [Fact]
    public void NoPathHardcodesAReportId()
    {
        Assert.DoesNotContain("packet.reportId = 0;", DriverSource(), StringComparison.Ordinal);
    }

    /// <summary>
    /// And the two sides agree about which id that is: the digitizer declares 1, the mouse and
    /// keyboard declare none, which is what makes a hardcoded 0 wrong for exactly one of the three.
    /// </summary>
    [Fact]
    public void OnlyTheDigitizerDeclaresAReportId()
    {
        var digitizer = new byte[HidReportDescriptors.DigitizerReportLength];
        HidDigitizerReport.Neutral.Write(digitizer);

        var mouse = new byte[HidReportDescriptors.MouseReportLength];
        HidMouseReport.Neutral.Write(mouse);

        Assert.Equal(1, HidReportDescriptors.DigitizerInputReportId);
        Assert.Equal(HidReportDescriptors.DigitizerInputReportId, digitizer[0]);
        Assert.Equal(0, mouse[0]);
    }
}
