using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// What a neutral report actually contains, on both sides of the ABI.
/// <para>
/// <b>These exist because reviewing the driver found a real defect.</b> The driver built its neutral
/// report by zeroing a buffer, on the stated reasoning that zero means released for every field that
/// matters — buttons, modifiers, key slots, tip switch, in range, contact count. That was true when
/// it was written and stopped being true when the digitizer gained <c>REPORT_ID 1</c>, which Windows
/// requires on a touch screen. Byte zero of that report is the id, and zero is not a valid id, so
/// the all-zero digitizer report was malformed — on the one path whose entire job is releasing a
/// stuck contact.
/// </para>
/// </summary>
public sealed class NeutralReportTests
{
    private static byte[] Bytes(HidMouseReport report)
    {
        var buffer = new byte[HidReportDescriptors.MouseReportLength];
        report.Write(buffer);
        return buffer;
    }

    private static byte[] Bytes(HidKeyboardReport report)
    {
        var buffer = new byte[HidReportDescriptors.KeyboardReportLength];
        report.Write(buffer);
        return buffer;
    }

    private static byte[] Bytes(HidDigitizerReport report)
    {
        var buffer = new byte[HidReportDescriptors.DigitizerReportLength];
        report.Write(buffer);
        return buffer;
    }

    // --- mouse -----------------------------------------------------------------------------------

    [Fact]
    public void ANeutralMouseReportReleasesButtonsAndMovesNothing()
    {
        var bytes = Bytes(HidMouseReport.Neutral);

        Assert.Equal(HidReportDescriptors.MouseReportLength, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    /// <summary>The mouse declares no report id, so its first byte is buttons and zero is correct.</summary>
    [Fact]
    public void TheMouseHasNoReportIdSoAZeroFirstByteIsData()
    {
        Assert.Equal(0, Bytes(HidMouseReport.Neutral)[0]);
    }

    // --- keyboard --------------------------------------------------------------------------------

    [Fact]
    public void ANeutralKeyboardReportClearsModifiersAndEveryKeySlot()
    {
        var bytes = Bytes(HidKeyboardReport.Neutral);

        Assert.Equal(HidReportDescriptors.KeyboardReportLength, bytes.Length);
        Assert.Equal(0, bytes[0]);                       // modifiers
        Assert.Equal(0, bytes[1]);                       // reserved

        for (var i = 2; i < bytes.Length; i++)
        {
            Assert.Equal(0, bytes[i]);                   // all six key slots
        }
    }

    /// <summary>
    /// A modifier left set is the failure that motivated the whole ownership model, so it gets its
    /// own assertion rather than being covered only by the all-zero sweep.
    /// </summary>
    [Fact]
    public void NoModifierSurvivesANeutralKeyboardReport()
    {
        var held = new HidKeyboardReport(
            HidKeyboardModifiers.LeftControl | HidKeyboardModifiers.LeftShift,
            [HidKeyMap.Usage(NexusKeyLike.A)]);

        Assert.NotEqual(0, Bytes(held)[0]);
        Assert.Equal(0, Bytes(HidKeyboardReport.Neutral)[0]);
    }

    // --- digitizer -------------------------------------------------------------------------------

    /// <summary>The defect, pinned. Byte zero is the report id and must be 1, not 0.</summary>
    [Fact]
    public void ANeutralDigitizerReportCarriesItsReportId()
    {
        var bytes = Bytes(HidDigitizerReport.Neutral);

        Assert.Equal(HidReportDescriptors.DigitizerInputReportId, bytes[0]);
        Assert.NotEqual(0, bytes[0]);
    }

    [Fact]
    public void ANeutralDigitizerReportLiftsTheContactAndClearsTheCount()
    {
        var bytes = Bytes(HidDigitizerReport.Neutral);

        Assert.Equal(HidReportDescriptors.DigitizerReportLength, bytes.Length);
        Assert.Equal(0, bytes[1] & 0x01);   // tip switch clear
        Assert.Equal(0, bytes[1] & 0x02);   // out of range
        Assert.Equal(0, bytes[7]);          // contact count zero
    }

    /// <summary>
    /// A contact that is down reports a count of one; the neutral report must report zero, or
    /// Windows keeps the contact alive and reads the next press as a continuation.
    /// </summary>
    [Fact]
    public void ContactCountDistinguishesAHeldContactFromAReleasedOne()
    {
        var down = Bytes(new HidDigitizerReport(true, true, 0, 100, 100, 5));
        var up = Bytes(HidDigitizerReport.Neutral);

        Assert.Equal(1, down[7]);
        Assert.Equal(0, up[7]);
    }

    // --- the driver side -------------------------------------------------------------------------

    private static string DriverSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "driver", "NexusHid", "Driver.c"));

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

    /// <summary>
    /// The driver must set the report id on the neutral report rather than sending all zeroes. This
    /// is the same defect as above, checked on the side that would actually send it.
    /// </summary>
    [Fact]
    public void TheDriverSetsTheReportIdOnNeutralReports()
    {
        var source = DriverSource();

        Assert.Contains("neutral[0] = slot->ReportId;", source, StringComparison.Ordinal);
        Assert.Contains("packet.reportId = slot->ReportId;", source, StringComparison.Ordinal);
    }

    /// <summary>The driver records each device's report id when it walks the descriptor.</summary>
    [Fact]
    public void TheDriverCapturesTheReportIdFromTheDescriptor()
    {
        var source = DriverSource();

        Assert.Contains("UINT8                   ReportId;", source, StringComparison.Ordinal);
        Assert.Contains("slot->ReportId = reportId;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neutralisation must run on control-handle close, which is how the driver learns PC Core is
    /// gone whether it exited, crashed or was killed.
    /// </summary>
    [Fact]
    public void TheDriverNeutralizesOnControlHandleClose()
    {
        var source = DriverSource();

        var cleanup = source.IndexOf("NexusHidEvtFileCleanup(", StringComparison.Ordinal);
        Assert.True(cleanup > 0, "the file-cleanup callback is missing");

        var body = source[cleanup..];
        Assert.Contains("NexusHidReleaseOwned(control, FileObject);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDriverNeutralizesOnDeviceCleanup()
    {
        var source = DriverSource();

        var cleanup = source.IndexOf("NexusHidEvtDeviceCleanup(", StringComparison.Ordinal);
        Assert.True(cleanup > 0, "the device-cleanup callback is missing");

        Assert.Contains("NexusHidReleaseOwned(control, NULL);", source[cleanup..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Neutralisation releases; it never generates. A timer that decided on its own to press or move
    /// something would be a driver producing input nobody asked for, which is the failure the whole
    /// design exists to prevent.
    /// </summary>
    [Fact]
    public void TheDriverHasNoTimerThatCouldGenerateInput()
    {
        var source = DriverSource();

        Assert.DoesNotContain("WdfTimerCreate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeSetTimer", source, StringComparison.Ordinal);
    }

    /// <summary>All three collections are released, not just the one that was in use.</summary>
    [Fact]
    public void TheDriverNeutralizesEveryPublishedDevice()
    {
        var source = DriverSource();
        var start = source.IndexOf("NexusHidNeutralizeOwned(\n", StringComparison.Ordinal);

        if (start < 0)
        {
            start = source.IndexOf("NexusHidNeutralizeOwned(", StringComparison.Ordinal);
        }

        Assert.True(start > 0);

        // The loop runs over every device slot rather than a single kind.
        Assert.Contains("for (kind = 0; kind < NEXUS_HID_MAX_DEVICES; kind++)", source, StringComparison.Ordinal);
    }
}
