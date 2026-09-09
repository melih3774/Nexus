using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// What the virtual-HID client does on a machine with no driver installed — which is this machine,
/// and every machine until the owner runs the install pass.
/// <para>
/// These are not placeholder tests waiting for hardware. "No driver present" is a state the client
/// will be in for most of its life, including every time the driver is being updated or has crashed,
/// and the behaviour that matters there is that it publishes nothing, trusts nothing, refuses every
/// report, and says so. All four are checkable right now.
/// </para>
/// </summary>
public sealed class NexusHidBackendTests
{
    [Fact]
    public void WithNoDriverInstalledTheBackendReportsNotInstalled()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        var availability = backend.Probe();

        Assert.Equal(VirtualHidState.NotInstalled, availability.State);
        Assert.Empty(availability.Devices);
        Assert.NotNull(availability.Diagnostic);
    }

    /// <summary>
    /// The security-relevant half. No driver means no published devices means nothing is trusted,
    /// so every observed input event classifies as physical.
    /// </summary>
    [Fact]
    public void WithNoDriverInstalledNothingIsTrusted()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        backend.Probe();

        Assert.True(registry.IsEmpty);
        Assert.Equal(
            InputSource.Physical,
            registry.Classify(@"\\?\HID#VID_F055&PID_0001#6&1a2b3c4d&0&0000").Source);
    }

    [Fact]
    public void WithNoDriverInstalledEveryReportIsRefused()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        backend.Probe();

        Assert.False(backend.SubmitMouse(HidMouseReport.Neutral));
        Assert.False(backend.SubmitKeyboard(HidKeyboardReport.Neutral));
        Assert.False(backend.SubmitDigitizer(HidDigitizerReport.Neutral));
    }

    /// <summary>
    /// Neutralise is called from every error path there is, including ones where the backend was
    /// never connected. It has to be safe when there is nothing to release, and safe twice.
    /// </summary>
    [Fact]
    public void NeutralizeIsSafeWithNoDriverAndSafeTwice()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        backend.Neutralize();
        backend.Neutralize();
    }

    [Fact]
    public void DisposeIsSafeWithNoDriver()
    {
        var registry = new NexusHidDeviceRegistry();
        var backend = new NexusHidBackend(registry);

        backend.Probe();
        backend.Dispose();
        backend.Dispose();
    }

    /// <summary>
    /// Probing repeatedly must not enumerate devices on every call — the router asks on a hot path.
    /// The observable part of that is simply that repeated probes stay consistent and cheap; the
    /// interval itself is an implementation detail, so this checks the contract rather than the
    /// timer.
    /// </summary>
    [Fact]
    public void RepeatedProbesAreConsistent()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        var first = backend.Probe();
        var second = backend.Probe();
        var third = backend.Availability;

        Assert.Equal(first.State, second.State);
        Assert.Equal(second.State, third.State);
    }
}
