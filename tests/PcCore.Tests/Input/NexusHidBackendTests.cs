using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// What the virtual-HID client does about a driver that is not there — or is there and unusable.
/// <para>
/// These are not placeholder tests waiting for hardware. "No driver present" is a state the client
/// will be in for much of its life, including every time the driver is being updated or has crashed,
/// and the behaviour that matters there is that it publishes nothing, trusts nothing, refuses every
/// report, and says so.
/// </para>
/// <para>
/// <b>Two of them used to assume this machine had no driver, and then it did.</b> They asserted
/// <c>NotInstalled</c> and got <c>Refused</c>, which was not a regression — it was the suite finding
/// a real control device and reporting honestly about it. A test whose premise is "this machine has
/// no driver" silently stops testing anything the day the owner installs one, so the premise is now
/// read from the machine rather than assumed, and both branches assert something.
/// </para>
/// </summary>
public sealed class NexusHidBackendTests
{
    /// <summary>True when this machine actually has a Nexus HID control device to talk to.</summary>
    private static bool DriverPresent => NexusHidControlDevice.Find() is not null;

    [Fact]
    public void TheReportedStateMatchesWhetherAControlDeviceExists()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        var availability = backend.Probe();

        if (!DriverPresent)
        {
            Assert.Equal(VirtualHidState.NotInstalled, availability.State);
            Assert.Empty(availability.Devices);
            Assert.NotNull(availability.Diagnostic);
            return;
        }

        // A driver is installed. Whatever it did, the one thing the client must never do is claim
        // there is nothing here - that is the reading that sends the owner to reinstall a driver
        // that is already running.
        Assert.NotEqual(VirtualHidState.NotInstalled, availability.State);

        if (availability.State is VirtualHidState.Ready or VirtualHidState.Partial)
        {
            Assert.NotEmpty(availability.Devices);
        }
        else
        {
            // Refused or Unavailable: nothing adopted, and a reason given. The reason is the only
            // account of a driver-side failure that exists, so its absence is itself a defect.
            Assert.Empty(availability.Devices);
            Assert.False(string.IsNullOrWhiteSpace(availability.Diagnostic));
        }
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

        // A path the driver never reported is never trusted, driver installed or not. Nexus vouches
        // for the exact device paths it was told about, and for nothing that merely looks like one.
        Assert.Equal(
            InputSource.Physical,
            registry.Classify(@"\\?\HID#VID_F055&PID_0001#6&1a2b3c4d&0&0000").Source);
    }

    /// <summary>
    /// A report for a device the backend did not adopt is refused, whether that is because no driver
    /// answered or because the driver answered and published nothing usable.
    /// <para>
    /// <b>This caught a real defect once the driver was installed.</b> The submit path checked only
    /// that the control handle was open, so with a driver present and zero devices adopted,
    /// <c>SubmitMouse</c> returned true — a report delivered to a device this backend had refused to
    /// trust, and recorded as a success against the lifecycle's failure budget. Holding a handle is
    /// not having a device.
    /// </para>
    /// </summary>
    [Fact]
    public void AReportForAnUnpublishedDeviceIsRefused()
    {
        var registry = new NexusHidDeviceRegistry();
        using var backend = new NexusHidBackend(registry);

        var availability = backend.Probe();

        if (!availability.Publishes(VirtualHidDeviceKind.Mouse))
        {
            Assert.False(backend.SubmitMouse(HidMouseReport.Neutral));
        }

        if (!availability.Publishes(VirtualHidDeviceKind.Keyboard))
        {
            Assert.False(backend.SubmitKeyboard(HidKeyboardReport.Neutral));
        }

        if (!availability.Publishes(VirtualHidDeviceKind.Digitizer))
        {
            Assert.False(backend.SubmitDigitizer(HidDigitizerReport.Neutral));
        }
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
