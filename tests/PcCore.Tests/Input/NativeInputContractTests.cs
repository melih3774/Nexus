using System.Text;
using PcCore.Capabilities.Input;
using PcCore.NativeInput;
using PcCore.Windows.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// The contracts that hold the two input paths and the two processes together.
/// <para>
/// Everything here is the kind of agreement that is invisible until it breaks: a key the protocol
/// gained but the elevated side refuses, a frame length nobody bounded, an operation name that no
/// longer matches. None of it is testable through the running system without a second process, and
/// all of it is cheap to hold still here.
/// </para>
/// </summary>
public class NativeInputContractTests
{
    /// <summary>
    /// Every key the protocol can name must be one the broker will actually press.
    /// <para>
    /// The allow-list exists because a virtual-key code crossing a process boundary must be checked
    /// on arrival rather than trusted. That check is only safe if it is complete: a key added to
    /// <see cref="NexusKey"/> and forgotten here would silently do nothing the moment native input
    /// was installed, and work fine on the fallback path — which is the worst way to find a bug.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryProtocolKeyIsOneTheBrokerWillPress()
    {
        var missing = Enum.GetValues<NexusKey>()
            .Select(key => (Key: key, Mapped: WindowsKeyMap.Map(key)))
            .Where(x => !NativeKeys.IsAllowed(x.Mapped.VirtualKey))
            .Select(x => $"{x.Key} -> 0x{x.Mapped.VirtualKey:X2}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryProtocolKeyMapsToADistinctVirtualKey()
    {
        // Two protocol keys mapping to the same code would mean one of them is unreachable, which
        // is a copy-paste slip the compiler cannot see.
        var duplicates = Enum.GetValues<NexusKey>()
            .Select(WindowsKeyMap.Map)
            .GroupBy(x => x)
            .Where(g => g.Count() > 1)
            .Select(g => $"0x{g.Key.VirtualKey:X2}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void TheAllowListRefusesKeysTheProtocolCannotName()
    {
        // A sample of codes that are perfectly real virtual keys and deliberately not reachable:
        // Print Screen, Caps Lock, Num Lock, Scroll Lock, and the Apps key.
        foreach (var virtualKey in new ushort[] { 0x2C, 0x14, 0x90, 0x91, 0x5D })
        {
            Assert.False(NativeKeys.IsAllowed(virtualKey), $"0x{virtualKey:X2} should not be allowed");
        }
    }

    [Fact]
    public void TheArrowAndNavigationKeysCarryTheExtendedFlag()
    {
        // Without it Windows reads them as their numeric-keypad twins, so Home types 7 and the up
        // arrow types 8 whenever Num Lock is on.
        foreach (var key in new[]
                 {
                     NexusKey.ArrowLeft, NexusKey.ArrowUp, NexusKey.ArrowRight, NexusKey.ArrowDown,
                     NexusKey.Home, NexusKey.End, NexusKey.PageUp, NexusKey.PageDown,
                     NexusKey.Insert, NexusKey.Delete,
                 })
        {
            Assert.True(WindowsKeyMap.Map(key).Extended, $"{key} needs the extended-key flag");
        }
    }

    [Fact]
    public void TheOperationListIsClosed()
    {
        // The broker refuses anything not on this list before parsing it further, so the list is the
        // complete statement of what an elevated process will do on request.
        Assert.Equal(
            [
                BrokerProtocol.Ops.Button,
                BrokerProtocol.Ops.Desktop,
                BrokerProtocol.Ops.Hello,
                BrokerProtocol.Ops.Key,
                BrokerProtocol.Ops.MoveAbsolute,
                BrokerProtocol.Ops.MoveRelative,
                BrokerProtocol.Ops.ReleaseAll,
                BrokerProtocol.Ops.Scroll,
                BrokerProtocol.Ops.Text,
                BrokerProtocol.Ops.Touch,
            ],
            BrokerProtocol.Ops.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePipeNameIsPerUserAndPerProtocolVersion()
    {
        var name = BrokerProtocol.PipeName("S-1-5-21-1-2-3-1001");

        Assert.Contains("S-1-5-21-1-2-3-1001", name, StringComparison.Ordinal);
        Assert.Contains($"v{BrokerProtocol.Version}", name, StringComparison.Ordinal);
        Assert.NotEqual(name, BrokerProtocol.PipeName("S-1-5-21-1-2-3-1002"));
    }

    [Fact]
    public void AFrameSurvivesARoundTrip()
    {
        using var stream = new MemoryStream();

        var request = new BrokerRequest
        {
            Op = BrokerProtocol.Ops.Touch,
            Sequence = 42,
            Contact = 3,
            Phase = BrokerProtocol.Phases.Move,
            X = -1600,
            Y = -200,
            Text = "çğışüö",
        };

        BrokerFraming.Write(stream, request);
        stream.Position = 0;

        var read = BrokerFraming.Read<BrokerRequest>(stream);

        Assert.NotNull(read);
        Assert.Equal(request.Op, read.Op);
        Assert.Equal(42, read.Sequence);
        Assert.Equal(3, read.Contact);
        Assert.Equal(-1600, read.X);
        Assert.Equal(-200, read.Y);
        Assert.Equal("çğışüö", read.Text);
    }

    [Fact]
    public void AnEmptyStreamReadsAsADisconnectRatherThanAnError()
    {
        using var stream = new MemoryStream();

        Assert.Null(BrokerFraming.Read<BrokerRequest>(stream));
    }

    /// <summary>
    /// A length prefix is checked before anything is allocated for it. Without that, four bytes off
    /// the pipe could make either process reserve two gigabytes on request.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(BrokerProtocol.MaxFrameBytes + 1)]
    public void AFrameLengthOutsideTheBoundsIsRefused(int length)
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(length));
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => BrokerFraming.Read<BrokerRequest>(stream));
    }

    [Fact]
    public void AFrameLargerThanTheProtocolAllowsIsNotWritten()
    {
        using var stream = new MemoryStream();

        var oversized = new BrokerRequest
        {
            Op = BrokerProtocol.Ops.Text,
            Text = new string('a', BrokerProtocol.MaxFrameBytes),
        };

        Assert.Throws<InvalidOperationException>(() => BrokerFraming.Write(stream, oversized));
    }

    /// <summary>
    /// The largest legal request — one <c>input.text</c> at the capability layer's own limit, made
    /// entirely of characters JSON has to escape — still fits inside a frame.
    /// </summary>
    [Fact]
    public void TheLargestLegalTextMessageFits()
    {
        using var stream = new MemoryStream();

        var text = string.Concat(Enumerable.Repeat("\"\\", new InputOptions().MaxTextLength / 3));

        BrokerFraming.Write(stream, new BrokerRequest { Op = BrokerProtocol.Ops.Text, Text = text });

        Assert.InRange(stream.Length, 1, BrokerProtocol.MaxFrameBytes);
    }

    [Theory]
    [InlineData("DOWN", TouchPhase.Down)]
    [InlineData("MOVE", TouchPhase.Move)]
    [InlineData("UP", TouchPhase.Up)]
    [InlineData("CANCEL", TouchPhase.Cancel)]
    public void TouchPhasesParse(string wire, TouchPhase expected) =>
        Assert.Equal(expected, BrokerProtocol.Phases.Parse(wire));

    [Theory]
    [InlineData("down")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SOMETHING_ELSE")]
    public void AnUnknownTouchPhaseParsesToNothing(string? wire) =>
        Assert.Null(BrokerProtocol.Phases.Parse(wire));

    [Fact]
    public void EveryDeliveryValueSurvivesTheRoundTrip()
    {
        foreach (var delivery in Enum.GetValues<NativeDelivery>())
        {
            var wire = BrokerProtocol.Deliveries.From(delivery);
            Assert.Equal(delivery, BrokerProtocol.Deliveries.To(wire));
        }
    }

    [Fact]
    public void AnUnknownDeliveryIsTreatedAsAFailure() =>
        Assert.Equal(NativeDelivery.Failed, BrokerProtocol.Deliveries.To("SOMETHING_NEW"));

    /// <summary>
    /// The tag is compared on its low thirty-two bits, because that is all Windows carries through
    /// the mouse hook. A change that widened it would silently stop matching injected mouse events.
    /// </summary>
    [Fact]
    public void TheNexusTagIsRecognisedWhateverTheHighBitsHold()
    {
        Assert.True(NexusInputTag.IsNexus(NexusInputTag.Value));
        Assert.True(NexusInputTag.IsNexus(unchecked((long)0xDEAD_BEEF_4E58_5330)));
        Assert.False(NexusInputTag.IsNexus(0x4E58_5331));
    }

    [Fact]
    public void WindowsOwnPenAndTouchSignatureIsRecognised()
    {
        // Measured on this machine: injected touch reaches a legacy window as mouse messages
        // carrying 0xFF515792 and 0xFF515793 — the documented MI_WP_SIGNATURE, plus a cursor id
        // whose 0x80 bit marks it as touch rather than pen.
        Assert.True(NexusInputTag.IsPenOrTouch(0xFF51_5792));
        Assert.True(NexusInputTag.IsPenOrTouch(0xFF51_5793));
        Assert.False(NexusInputTag.IsPenOrTouch(0));
        Assert.False(NexusInputTag.IsPenOrTouch(NexusInputTag.Value));
    }

    [Fact]
    public void ABrokerResponseCarriesItsInfoBack()
    {
        using var stream = new MemoryStream();

        BrokerFraming.Write(stream, new BrokerResponse
        {
            Sequence = 7,
            Delivery = BrokerProtocol.Deliveries.BlockedBySecureDesktop,
            Diagnostic = "secure desktop",
            Info = new BrokerInfo { BrokerVersion = "0.8.0", ProcessId = 123, Elevated = true, Desktop = "SECURE" },
        });

        stream.Position = 0;
        var read = BrokerFraming.Read<BrokerResponse>(stream);

        Assert.NotNull(read);
        Assert.Equal(7, read.Sequence);
        Assert.Equal(NativeDelivery.BlockedBySecureDesktop, BrokerProtocol.Deliveries.To(read.Delivery));
        Assert.True(read.Info!.Elevated);
        Assert.Equal("SECURE", read.Info.Desktop);
    }

    [Fact]
    public void PathsThatDifferOnlyInSpellingAreTheSameImage()
    {
        var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        Assert.True(BrokerPipe.SameImage(
            Path.Combine(directory, "PcCore.InputBroker.exe"),
            Path.Combine(directory, "sub", "..", "PcCore.InputBroker.exe")));

        Assert.False(BrokerPipe.SameImage(
            Path.Combine(directory, "PcCore.InputBroker.exe"),
            Path.Combine(directory, "PcCore.Host.exe")));

        // A missing path is never "the same image": failing open here would be failing open on the
        // check that decides who may drive the elevated input path.
        Assert.False(BrokerPipe.SameImage(null, Path.Combine(directory, "PcCore.InputBroker.exe")));
        Assert.False(BrokerPipe.SameImage(string.Empty, string.Empty));
    }

    [Fact]
    public void TheBrokerExecutableIsExpectedBesidePcCore()
    {
        var expected = NativeInputInstaller.ExpectedBrokerPath;

        Assert.Equal(NativeInputInstaller.BrokerExecutableName, Path.GetFileName(expected));
        Assert.Equal(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(Path.GetFullPath(expected)));
    }

    [Fact]
    public void TheInstalledTaskIsNamedSoAPersonCanFindIt()
    {
        // The owner has to be able to see this in Task Scheduler and remove it. A task called
        // something opaque would be indistinguishable from the stealth persistence this is not.
        Assert.StartsWith(@"\Nexus\", NativeInputInstaller.TaskPath, StringComparison.Ordinal);
        Assert.Contains("Input Broker", NativeInputInstaller.TaskPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether this machine has a touchscreen must not change because Nexus started injecting touch.
    /// <para>
    /// It did, twice, in two different ways. First <c>SM_DIGITIZER</c> went from <c>0x0</c> to
    /// <c>0xC1</c> once <c>InitializeTouchInjection</c> had been called. Then, after switching to
    /// enumerating raw input devices, the injection device turned out to be enumerable too — a
    /// touch-screen digitizer named VIRTUAL_DIGITIZER, appearing the moment injection started.
    /// </para>
    /// <para>
    /// The answer feeds the security detector's decision about whether pen/touch-signature input
    /// could have come from a person, so an answer that flips when Nexus starts up is exactly the
    /// wrong kind of wrong. This asserts the property directly rather than the mechanism.
    /// </para>
    /// </summary>
    [Fact]
    public void CreatingAnInjectorDoesNotGiveTheMachineATouchscreen()
    {
        var before = NativeInjector.HasPhysicalDigitizer;

        var injector = new NativeInjector();

        try
        {
            Assert.Equal(before, NativeInjector.HasPhysicalDigitizer);
        }
        finally
        {
            injector.ReleaseAll();
        }
    }

    [Fact]
    public void TheFrameHeaderIsFourLittleEndianBytes()
    {
        using var stream = new MemoryStream();
        BrokerFraming.Write(stream, new BrokerRequest { Op = BrokerProtocol.Ops.Hello });

        var frame = stream.ToArray();
        var length = BitConverter.ToInt32(frame, 0);

        Assert.Equal(frame.Length - 4, length);
        Assert.Equal(length, Encoding.UTF8.GetString(frame, 4, length).Length);
    }
}
