using PcCore.Capabilities.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// What <c>input.status</c> says about the input path, and specifically that it does not contradict
/// itself.
/// <para>
/// These exist because of a real defect rather than for coverage. On a machine carrying every kind
/// of input through Nexus HID devices, <c>input.status</c> reported
/// <c>reaches_elevated_windows: true</c> and, in the same object, a diagnostic reading "elevated
/// application windows are out of reach" — and the diagnostic was the part the owner read, and the
/// part PC Core logged as a warning on every start. Both were computed from different inputs a few
/// lines apart in the router.
/// </para>
/// </summary>
public class NativeInputReportingTests
{
    private static InputBackends Backends(
        InputBackend mouse,
        InputBackend keyboard,
        InputBackend touch) => new()
        {
            Mouse = mouse,
            Keyboard = keyboard,
            Touch = touch,
            TextUsesCharacterInjection = true,
        };

    [Fact]
    public void HidCarryingInputIsNotDescribedAsOutOfReach()
    {
        var description = NativeInputStatus.DescribeInputPath(
            Backends(InputBackend.VirtualHid, InputBackend.VirtualHid, InputBackend.VirtualHid),
            brokerRegistered: false,
            brokerDiagnostic: null);

        Assert.DoesNotContain("out of reach", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reachable", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pointer and the keyboard are what decide this. A machine whose digitizer fell back is
    /// still one whose elevated windows are reachable, and saying otherwise would send the owner
    /// installing a broker that would change nothing.
    /// </summary>
    [Fact]
    public void HidMouseAndKeyboardAreEnoughEvenWhenTouchFellBack()
    {
        var description = NativeInputStatus.DescribeInputPath(
            Backends(InputBackend.VirtualHid, InputBackend.VirtualHid, InputBackend.LocalInjection),
            brokerRegistered: false,
            brokerDiagnostic: null);

        Assert.DoesNotContain("out of reach", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutHidTheAbsentBrokerIsStillReportedHonestly()
    {
        var description = NativeInputStatus.DescribeInputPath(
            Backends(InputBackend.LocalInjection, InputBackend.LocalInjection, InputBackend.LocalInjection),
            brokerRegistered: false,
            brokerDiagnostic: null);

        Assert.Contains("not installed", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("out of reach", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInstalledButSilentBrokerReportsItsOwnReason()
    {
        var description = NativeInputStatus.DescribeInputPath(
            Backends(InputBackend.LocalInjection, InputBackend.LocalInjection, InputBackend.LocalInjection),
            brokerRegistered: true,
            brokerDiagnostic: "the broker is running without administrator rights");

        Assert.Equal("the broker is running without administrator rights", description);
    }

    /// <summary>
    /// The invariant the defect broke, stated directly: a status whose backends are HID must not
    /// carry a diagnostic that denies what <see cref="NativeInputStatus.ReachesElevatedWindows"/>
    /// and <see cref="NativeInputStatus.ReachesSecureDesktop"/> assert.
    /// </summary>
    [Fact]
    public void TheStatusObjectDoesNotContradictItself()
    {
        var backends = Backends(InputBackend.VirtualHid, InputBackend.VirtualHid, InputBackend.VirtualHid);

        var status = new NativeInputStatus
        {
            Path = InputPath.Local,
            State = NativeInputState.NotInstalled,
            ReachesElevatedWindows = true,
            TouchAvailable = true,
            PhysicalDigitizer = false,
            Desktop = "DEFAULT",
            Backends = backends,
            Diagnostic = NativeInputStatus.DescribeInputPath(backends, brokerRegistered: false, brokerDiagnostic: null),
        };

        Assert.True(status.ReachesElevatedWindows);
        Assert.True(status.ReachesSecureDesktop);
        Assert.DoesNotContain("out of reach", status.Diagnostic!, StringComparison.OrdinalIgnoreCase);

        // And the one-line summary the phone shows names the backend rather than the broker.
        Assert.Contains("Virtual HID", status.Summary, StringComparison.Ordinal);
    }
}
