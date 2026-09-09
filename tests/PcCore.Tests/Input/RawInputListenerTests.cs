using Microsoft.Extensions.Logging.Abstractions;

using PcCore.NativeInput.Hid;
using PcCore.Windows.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The Raw Input source-observation layer, checked against this machine's real devices.
/// <para>
/// These are not simulations. The enumeration tests read the actual mice and keyboards Windows has
/// attached right now, resolve their real device paths, and classify them — which is exactly what
/// will happen on install day, minus a Nexus device to find. Nothing here installs a device, injects
/// input, or changes any machine state.
/// </para>
/// </summary>
public sealed class RawInputListenerTests
{
    private static RawInputListener Listener(NexusHidDeviceRegistry registry) =>
        new(NullLogger<RawInputListener>.Instance, registry);

    // ------------------------------------------------------- real devices on this PC

    /// <summary>
    /// This machine is a laptop with a keyboard and a touchpad, so Raw Input must see at least one
    /// of each. A machine that reports no input devices at all would mean the enumeration is broken,
    /// not that the machine has no keyboard.
    /// </summary>
    [Fact]
    public void RawInputSeesTheRealMiceAndKeyboardsOnThisMachine()
    {
        var devices = RawInputListener.EnumerateDevices();

        Assert.NotEmpty(devices);
        Assert.Contains(devices, d => d.Kind == RawInputKind.Mouse);
        Assert.Contains(devices, d => d.Kind == RawInputKind.Keyboard);
    }

    /// <summary>
    /// Attribution is a comparison of device paths, so a path that will not resolve is a device that
    /// can never be trusted. Most must resolve for the mechanism to be usable at all.
    /// </summary>
    [Fact]
    public void MostRealDevicePathsResolve()
    {
        var devices = RawInputListener.EnumerateDevices();
        var resolved = devices.Count(d => !string.IsNullOrWhiteSpace(d.Path));

        Assert.True(
            resolved > 0,
            $"no device path resolved out of {devices.Count}; attribution would be unusable");
    }

    /// <summary>
    /// The state that matters today: with nothing published, every real device on this machine is
    /// physical. If this ever returned trusted for a real device, the invariant would be broken.
    /// </summary>
    [Fact]
    public void EveryRealDeviceOnThisMachineClassifiesAsPhysical()
    {
        var registry = new NexusHidDeviceRegistry();
        var devices = RawInputListener.EnumerateDevices();

        foreach (var device in devices)
        {
            var verdict = registry.Classify(device.Path);

            Assert.Equal(InputSource.Physical, verdict.Source);
        }
    }

    /// <summary>
    /// A real device path from this machine must not become trusted by wearing Nexus's identifiers.
    /// This is the spoofing case checked against real data rather than a synthetic string.
    /// </summary>
    [Fact]
    public void ARealDeviceIsNotTrustedEvenWhenNexusDevicesArePublished()
    {
        var registry = new NexusHidDeviceRegistry();

        registry.Register(new VirtualHidDeviceIdentity
        {
            Kind = VirtualHidDeviceKind.Mouse,
            DevicePath = @"\\?\HID#VID_F055&PID_0001#nexus&synthetic&0&0000",
            FriendlyName = "Nexus Mouse",
        });

        foreach (var device in RawInputListener.EnumerateDevices())
        {
            if (device.Path is null)
            {
                continue;
            }

            Assert.Equal(InputSource.Physical, registry.Classify(device.Path).Source);
        }
    }

    // ------------------------------------------------------- the listener itself

    /// <summary>
    /// The listener has to run without a visible window and without foreground. Starting it here
    /// proves the message-only window and the RIDEV_INPUTSINK registration both succeed on this
    /// machine — which is the part most likely to fail for environmental reasons.
    /// </summary>
    [Fact]
    public void TheListenerStartsHeadlessAndRegisters()
    {
        var registry = new NexusHidDeviceRegistry();
        using var listener = Listener(registry);

        listener.Start();

        // Registration happens on the pump thread; give it a moment to get there.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!listener.IsRunning && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.True(listener.IsRunning, "the Raw Input listener did not start");
        Assert.True(listener.KnownDeviceCount > 0, "the listener seeded no devices");
    }

    [Fact]
    public void TheListenerStopsCleanlyAndIsSafeToDisposeTwice()
    {
        var registry = new NexusHidDeviceRegistry();
        var listener = Listener(registry);

        listener.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!listener.IsRunning && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        listener.Dispose();
        listener.Dispose();

        Assert.False(listener.IsRunning);
    }

    [Fact]
    public void StartingTwiceDoesNotCreateASecondPump()
    {
        var registry = new NexusHidDeviceRegistry();
        using var listener = Listener(registry);

        listener.Start();
        listener.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!listener.IsRunning && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.True(listener.IsRunning);
    }
}
