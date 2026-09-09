using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace PcCore.NativeInput.Hid;

/// <summary>
/// Talks to the Nexus HID driver over its control interface.
/// <para>
/// <b>The only implementation of <see cref="IVirtualHidBackend"/> that can actually publish
/// devices</b>, and on a machine with no driver installed it publishes nothing and says so. It
/// finds the control device by interface GUID rather than by a fixed path, so nothing depends on a
/// name Windows chose, and it re-probes on a timer so a driver that appears after PC Core started —
/// or comes back after a restart — is picked up without the owner doing anything.
/// </para>
/// <para>
/// <b>Fail closed on version.</b> A driver reporting a protocol version this build does not know is
/// refused outright rather than used cautiously. Version skew between a driver and its control
/// client means the two disagree about struct layout, and the failure that produces is a report
/// read from the middle of a struct — which is input, not an error.
/// </para>
/// <para>
/// <b>No passthrough.</b> There is no method here that takes an arbitrary control code or an
/// arbitrary buffer, and the device handle is private. Everything the driver can be asked to do is
/// one of the six typed operations below.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NexusHidBackend : IVirtualHidBackend
{
    /// <summary>
    /// How long to wait before looking for the driver again after not finding it.
    /// <para>
    /// Without this, a machine with no driver installed — which is every machine today — would pay
    /// for a full device enumeration on every pointer move.
    /// </para>
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for Windows to enumerate a device that was just created.
    /// <para>
    /// The create IOCTL returns as soon as <c>VhfStart</c> succeeds, and the HID child appears some
    /// time afterwards on the PnP thread. Nothing can be resolved before it does, so this is a wait
    /// rather than a retry-next-probe: coming back in five seconds would mean five seconds of
    /// published-but-unusable devices, and the wait is usually a few hundred milliseconds.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultResolutionTimeout = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan ResolutionPoll = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly NexusHidDeviceRegistry _registry;
    private readonly TimeProvider _time;

    /// <summary>
    /// The Raw Input device list, as Raw Input itself reports it. Null on a build with no enumerator
    /// wired, which can create devices but cannot adopt them.
    /// <para>
    /// <b>Raw Input is the oracle, not SetupAPI.</b> The registry matches on the exact string Raw
    /// Input reports for an incoming event, so the only enumeration that cannot disagree with it is
    /// Raw Input's own. Resolving through SetupAPI would mean composing the same path from PnP data
    /// and hoping the two agree about mangling and interface class — and a near-miss there is a
    /// Nexus device classified as a person.
    /// </para>
    /// </summary>
    private readonly Func<IReadOnlyList<RawInputDeviceInfo>>? _rawInputDevices;

    private readonly TimeSpan _resolutionTimeout;

    /// <summary>
    /// Whether a Raw Input device path belongs to a device descending from the given control
    /// devnode. Injectable only so the resolution rules can be tested without a driver — production
    /// always uses <see cref="NexusHidPnpAncestry"/>, which is the real device tree.
    /// </summary>
    private readonly Func<string, string, bool> _descendsFromControl;

    /// <summary>
    /// The generic-HID device interfaces present on this machine. The touch screen is found here
    /// rather than through Raw Input, which gives every digitizer the same synthetic name.
    /// </summary>
    private readonly Func<IReadOnlyList<string>> _hidInterfacePaths;

    /// <summary>The PnP instance behind a device interface path. Injected only for tests.</summary>
    private readonly Func<string, string?> _instanceIdOf;

    /// <summary>
    /// The connection life of this backend: failure budget, loss backoff, and the neutral handshake
    /// that stands between a freshly connected instance and its first real report.
    /// </summary>
    private readonly VirtualHidLifecycle _lifecycle = new();

    private SafeFileHandle? _control;

    /// <summary>
    /// The PnP instance id of the control device this backend is talking to, learned from the
    /// interface path it opened. Everything Nexus publishes descends from it, and that is the whole
    /// basis on which a device is trusted.
    /// </summary>
    private string? _controlInstanceId;
    private VirtualHidAvailability _availability;
    private DateTimeOffset _nextProbe = DateTimeOffset.MinValue;

    public NexusHidBackend(
        NexusHidDeviceRegistry registry,
        TimeProvider? time = null,
        Func<IReadOnlyList<RawInputDeviceInfo>>? rawInputDevices = null,
        TimeSpan? resolutionTimeout = null,
        Func<string, string, bool>? descendsFromControl = null,
        Func<IReadOnlyList<string>>? hidInterfacePaths = null,
        Func<string, string?>? instanceIdOf = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _time = time ?? TimeProvider.System;
        _rawInputDevices = rawInputDevices;
        _resolutionTimeout = resolutionTimeout ?? DefaultResolutionTimeout;
        _descendsFromControl = descendsFromControl ?? DescendsFromControl;
        _hidInterfacePaths = hidInterfacePaths ?? HidInterfacePaths;
        _instanceIdOf = instanceIdOf ?? InstanceIdOf;
        _availability = VirtualHidAvailability.None(
            "The Nexus HID driver is not installed, so Windows enumerates no Nexus input devices.");
    }

    public VirtualHidAvailability Availability
    {
        get
        {
            lock (_gate)
            {
                return _availability;
            }
        }
    }

    /// <summary>The lifecycle state machine, for eligibility and diagnostics.</summary>
    public VirtualHidLifecycle Lifecycle => _lifecycle;

    /// <summary>
    /// True only when the backend is connected, validated, identified and has completed the neutral
    /// handshake. Eligibility hangs on this rather than on holding a handle.
    /// </summary>
    public bool IsReady => _lifecycle.IsEligible;

    public VirtualHidAvailability Probe()
    {
        VirtualHidAvailability availability;
        ConnectAttempt attempt;
        DateTimeOffset now;

        lock (_gate)
        {
            now = _time.GetUtcNow();

            if (_control is { IsInvalid: false } && _lifecycle.IsEligible)
            {
                return _availability;
            }

            if (now < _nextProbe || !_lifecycle.MayAttemptConnect(now))
            {
                // Either the ordinary probe interval has not elapsed, or the lifecycle is serving a
                // backoff after a loss. Retrying a dead driver on a tight loop is its own kind of
                // flapping: connect, handshake, fail, fall back, repeat.
                return _availability;
            }

            if (!_lifecycle.BeginConnect(now))
            {
                return _availability;
            }

            _nextProbe = now + RetryInterval;
            attempt = ConnectLocked();

            if (attempt.Refusal is not null)
            {
                _availability = attempt.Refusal;
                _lifecycle.Lose(now, attempt.Refusal.Diagnostic ?? "the backend did not connect");
                return _availability;
            }
        }

        // Resolution, outside the lock. It waits on Windows to enumerate the devices that were just
        // created, and holding the gate across that wait would block Availability - which the input
        // path reads on every event - for as long as PnP takes.
        var identities = ResolveIdentities(attempt.Created, attempt.Failures, ControlInstanceId());

        lock (_gate)
        {
            if (_control is null || _control.IsInvalid)
            {
                // Disconnected while resolving. Whatever was created belongs to a handle that is
                // gone, and the driver released it when that handle closed.
                return _availability;
            }

            foreach (var identity in identities)
            {
                _registry.Register(identity);
            }

            availability = Compose(identities, attempt.Failures, attempt.DriverVersion);
            _availability = availability;

            if (availability.State is not (VirtualHidState.Ready or VirtualHidState.Partial))
            {
                Disconnect();
                _lifecycle.Lose(now, availability.Diagnostic ?? "the backend published nothing usable");
                return availability;
            }

            // Connected and identified, and deliberately not yet usable.
            _lifecycle.Connected();
        }

        // The handshake, outside the lock. A freshly connected device is not known to be neutral -
        // a driver restart or an install over a running instance can leave one reporting something
        // held that nothing on this side knows about. Starting from a known position rather than an
        // assumed one is the whole point.
        var accepted = _lifecycle.CompleteHandshake(now, SendNeutralHandshake);

        if (!accepted)
        {
            lock (_gate)
            {
                Disconnect();
                _availability = new VirtualHidAvailability
                {
                    State = VirtualHidState.Refused,
                    Devices = [],
                    Diagnostic = "the Nexus HID backend did not accept a neutral report; it will not be used.",
                };

                return _availability;
            }
        }

        return availability;
    }

    /// <summary>
    /// Sends the neutral report to every published device. The handshake, and also what
    /// <see cref="Neutralize"/> does — the same operation, at two different moments.
    /// </summary>
    private bool SendNeutralHandshake()
    {
        var ok = true;
        var devices = Availability;

        if (devices.Publishes(VirtualHidDeviceKind.Mouse))
        {
            ok &= SubmitRaw(VirtualHidDeviceKind.Mouse, HidMouseReport.Neutral);
        }

        if (devices.Publishes(VirtualHidDeviceKind.Keyboard))
        {
            ok &= SubmitRaw(VirtualHidDeviceKind.Keyboard, HidKeyboardReport.Neutral);
        }

        if (devices.Publishes(VirtualHidDeviceKind.Digitizer))
        {
            ok &= SubmitRaw(VirtualHidDeviceKind.Digitizer, HidDigitizerReport.Neutral);
        }

        return ok;
    }

    private bool SubmitRaw(VirtualHidDeviceKind kind, in HidMouseReport report)
    {
        Span<byte> buffer = stackalloc byte[HidReportDescriptors.MouseReportLength];
        report.Write(buffer);
        return SubmitBytes(kind, buffer);
    }

    private bool SubmitRaw(VirtualHidDeviceKind kind, in HidKeyboardReport report)
    {
        Span<byte> buffer = stackalloc byte[HidReportDescriptors.KeyboardReportLength];
        report.Write(buffer);
        return SubmitBytes(kind, buffer);
    }

    private bool SubmitRaw(VirtualHidDeviceKind kind, in HidDigitizerReport report)
    {
        Span<byte> buffer = stackalloc byte[HidReportDescriptors.DigitizerReportLength];
        report.Write(buffer);
        return SubmitBytes(kind, buffer);
    }

    /// <summary>
    /// One connection attempt: either a refusal, or the devices the driver created and has yet to
    /// be resolved to live device paths.
    /// </summary>
    private readonly record struct ConnectAttempt(
        VirtualHidAvailability? Refusal,
        uint DriverVersion,
        IReadOnlyList<CreatedDevice> Created,
        List<string> Failures);

    /// <summary>
    /// A device the driver created, named by the identity it assigned, before Windows has been
    /// asked where it ended up.
    /// </summary>
    internal readonly record struct CreatedDevice(
        VirtualHidDeviceKind Kind,
        ushort ProductId,
        Guid ContainerId);

    private static ConnectAttempt Refuse(VirtualHidState state, string diagnostic) =>
        new(
            new VirtualHidAvailability { State = state, Devices = [], Diagnostic = diagnostic },
            0,
            [],
            []);

    private ConnectAttempt ConnectLocked()
    {
        Disconnect();

        var path = NexusHidControlDevice.Find();
        if (path is null)
        {
            return Refuse(
                VirtualHidState.NotInstalled,
                "The Nexus HID driver is not installed, so Windows enumerates no Nexus input devices.");
        }

        var handle = NexusHidControlDevice.Open(path);
        if (handle is null || handle.IsInvalid)
        {
            handle?.Dispose();
            return Refuse(
                VirtualHidState.Unavailable,
                "The Nexus HID driver is installed but its control device could not be opened.");
        }

        _control = handle;

        // The devnode behind the interface path we just opened. Resolution walks up from each Raw
        // Input device looking for exactly this, so failing to learn it means adopting nothing.
        _controlInstanceId = NexusHidPnpAncestry.InstanceIdOfInterface(path);

        if (!TryGetVersion(out var version))
        {
            Disconnect();
            return Refuse(
                VirtualHidState.Unavailable,
                "The Nexus HID driver did not answer a version request.");
        }

        if (version.ProtocolVersion != NexusHidProtocol.Version)
        {
            // Deliberately not "use it anyway". See the class remarks.
            Disconnect();
            return Refuse(
                VirtualHidState.Refused,
                $"The Nexus HID driver speaks protocol {version.ProtocolVersion}, "
                + $"this build speaks {NexusHidProtocol.Version}. Update the driver or PC Core.");
        }

        var created = new List<CreatedDevice>();
        var failures = new List<string>();

        foreach (var (kind, descriptor, productId) in Devices())
        {
            if (TryCreate(kind, descriptor, productId, out var device, out var why))
            {
                created.Add(device);
            }
            else
            {
                // The reason, not just the name. A create that fails is the one moment where the
                // driver has something specific to say - a state and an NTSTATUS - and dropping it
                // leaves "did not publish: Mouse, Keyboard, Digitizer", which is the observation
                // rather than the diagnosis.
                failures.Add($"{kind} ({why})");
            }
        }

        return new ConnectAttempt(null, version.DriverVersion, created, failures);
    }

    /// <summary>
    /// Finds where Windows put each device that was just created, by asking the device tree which
    /// devices descend from the Nexus control device.
    /// <para>
    /// <b>Neither the driver nor the create call can answer this.</b> The IOCTL returns when
    /// <c>VhfStart</c> succeeds; the HID child is enumerated afterwards, on the PnP thread, and only
    /// then does a device path exist. VHF offers a source driver no supported way to learn or choose
    /// what Windows will call the device — the two <c>VHF_CONFIG</c> members that look like they
    /// would are undocumented, and setting them stopped enumeration altogether.
    /// </para>
    /// <para>
    /// <b>So the answer comes from ancestry, not from anything the device claims.</b> Every device
    /// Nexus publishes is a child of the Nexus control devnode, and nothing that is not Nexus's can
    /// appear there. A vendor id, a hardware id or a friendly name would all be assertions any driver
    /// on the machine could also make; the device tree is Windows' own record of what created what.
    /// </para>
    /// <para>
    /// <b>The mouse and keyboard are found through Raw Input, and the touch screen cannot be.</b>
    /// Raw Input names a mouse and a keyboard by their device interface path, which is exactly the
    /// string it will later report as the source of an event — so resolving them there guarantees the
    /// registry holds a string that can actually match. It reports a digitizer as
    /// <c>\\?\VIRTUAL_DIGITIZER</c>: one shared synthetic name for the whole touch stack, belonging
    /// to no device in particular and resolving to no PnP instance. Adopting that name would mean
    /// trusting every source of touch on the machine, so the touch screen is found the other way —
    /// by enumerating HID interfaces and keeping the one under our devnode that is not already
    /// spoken for.
    /// </para>
    /// </summary>
    internal List<VirtualHidDeviceIdentity> ResolveIdentities(
        IReadOnlyList<CreatedDevice> created,
        List<string> failures,
        string? controlInstanceId)
    {
        var resolved = new List<VirtualHidDeviceIdentity>();

        if (created.Count == 0)
        {
            return resolved;
        }

        if (_rawInputDevices is null)
        {
            foreach (var device in created)
            {
                failures.Add(
                    $"{device.Kind} (created, but no Raw Input enumerator is wired, so its device "
                    + "path could not be resolved and its input could not be attributed)");
            }

            return resolved;
        }

        if (string.IsNullOrWhiteSpace(controlInstanceId))
        {
            foreach (var device in created)
            {
                failures.Add(
                    $"{device.Kind} (created, but the Nexus control device's own PnP instance could "
                    + "not be resolved, so nothing could be checked against it)");
            }

            return resolved;
        }

        var pending = new List<CreatedDevice>(created);
        var deadline = _time.GetUtcNow() + _resolutionTimeout;
        var lastSeen = "nothing";

        while (true)
        {
            var ours = Descendants(controlInstanceId!);

            // Kept for the failure message. "Windows enumerated no digitizer" and "Windows
            // enumerated nothing at all" call for different investigations, and without this the
            // two read identically.
            lastSeen = ours.Count == 0
                ? "no Raw Input device descends from it"
                : string.Join(", ", ours.Select(d => d.Class).OrderBy(c => c));

            // The touch screen is identified by subtraction, so it goes last within every pass:
            // every Nexus device exposes a HID interface, and only once the mouse and keyboard have
            // claimed their devnodes is the remaining one unambiguous. Resolving it first finds
            // three candidates and refuses all of them.
            var order = pending
                .Where(d => d.Kind != VirtualHidDeviceKind.Digitizer)
                .Concat(pending.Where(d => d.Kind == VirtualHidDeviceKind.Digitizer))
                .ToArray();

            foreach (var device in order)
            {
                // Still waiting on the devices whose devnodes it is identified against.
                if (device.Kind == VirtualHidDeviceKind.Digitizer
                    && pending.Exists(other => other.Kind != VirtualHidDeviceKind.Digitizer))
                {
                    continue;
                }

                var matches = device.Kind == VirtualHidDeviceKind.Digitizer
                    ? TouchCandidates(controlInstanceId!, resolved)
                    : ours.Where(d => d.Class == ClassOf(device.Kind)).Select(d => d.Path).ToArray();

                if (matches.Length == 1)
                {
                    resolved.Add(Identify(device, matches[0]));
                    pending.Remove(device);
                }
                else if (matches.Length > 1)
                {
                    failures.Add(
                        $"{device.Kind} (created, but {matches.Length} devices under the Nexus "
                        + "control device could be it; refusing to guess which one)");
                    pending.Remove(device);
                }
            }

            if (pending.Count == 0 || _time.GetUtcNow() >= deadline)
            {
                break;
            }

            Thread.Sleep(ResolutionPoll);
        }

        foreach (var device in pending)
        {
            failures.Add(
                $"{device.Kind} (created, but Windows enumerated no {ClassOf(device.Kind)} device "
                + $"under the Nexus control device within {_resolutionTimeout.TotalSeconds:0}s; "
                + $"what did descend from it: {lastSeen})");
        }

        return resolved;
    }

    /// <summary>
    /// The HID interfaces under the Nexus control device that no already-adopted device is using.
    /// <para>
    /// Every Nexus device exposes a generic HID interface, not just the touch screen — the mouse and
    /// keyboard each have one alongside their mouse and keyboard interfaces. Taking the HID
    /// interfaces under our devnode therefore finds three, and the one that is the touch screen is
    /// the one that does not sit on the same devnode as a device already resolved through Raw Input.
    /// Subtraction rather than pattern-matching, because the pattern would be a guess about naming.
    /// </para>
    /// </summary>
    private string[] TouchCandidates(string controlInstanceId, List<VirtualHidDeviceIdentity> resolved)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identity in resolved)
        {
            if (_instanceIdOf(identity.DevicePath) is { Length: > 0 } instance)
            {
                taken.Add(instance);
            }
        }

        var candidates = new List<string>();

        foreach (var path in _hidInterfacePaths())
        {
            if (!_descendsFromControl(path, controlInstanceId))
            {
                continue;
            }

            var instance = _instanceIdOf(path);

            if (instance is null || taken.Contains(instance))
            {
                continue;
            }

            candidates.Add(path);
        }

        return [.. candidates];
    }

    /// <summary>
    /// The Raw Input devices that descend from the Nexus control device. Everything else on the
    /// machine — every real mouse, every real keyboard — is excluded here and stays physical.
    /// </summary>
    private List<RawInputDeviceInfo> Descendants(string controlInstanceId)
    {
        var ours = new List<RawInputDeviceInfo>();

        IReadOnlyList<RawInputDeviceInfo> devices;

        try
        {
            devices = _rawInputDevices!() ?? [];
        }
        catch (Exception)
        {
            // An enumeration that throws is an enumeration that found nothing. The caller treats
            // that as "not yet", and the deadline ends it.
            return ours;
        }

        foreach (var device in devices)
        {
            if (_descendsFromControl(device.Path, controlInstanceId))
            {
                ours.Add(device);
            }
        }

        return ours;
    }

    /// <summary>
    /// The real check: resolve the devnode behind the interface path, then walk its parents.
    /// <para>
    /// A device that cannot be resolved is not ours. Unresolvable means untrusted, which is the
    /// direction that produces a false alarm rather than a missed intruder.
    /// </para>
    /// </summary>
    private static bool DescendsFromControl(string devicePath, string controlInstanceId)
    {
        try
        {
            return NexusHidPnpAncestry.DescendsFrom(
                NexusHidPnpAncestry.InstanceIdOfInterface(devicePath), controlInstanceId);
        }
        catch (Exception)
        {
            // A device that went away between enumeration and the query.
            return false;
        }
    }

    /// <summary>GUID_DEVINTERFACE_HID — every HID top-level collection exposes one of these.</summary>
    private static readonly Guid HidInterfaceClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    private static IReadOnlyList<string> HidInterfacePaths()
    {
        try
        {
            return NexusHidControlDevice.InterfacePaths(HidInterfaceClass);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? InstanceIdOf(string devicePath)
    {
        try
        {
            return NexusHidPnpAncestry.InstanceIdOfInterface(devicePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static RawInputDeviceClass ClassOf(VirtualHidDeviceKind kind) => kind switch
    {
        VirtualHidDeviceKind.Mouse => RawInputDeviceClass.Mouse,
        VirtualHidDeviceKind.Keyboard => RawInputDeviceClass.Keyboard,

        // A digitizer is not a mouse or a keyboard to Raw Input, so it arrives as generic HID.
        _ => RawInputDeviceClass.Hid,
    };

    private static VirtualHidDeviceIdentity Identify(CreatedDevice device, string devicePath) => new()
    {
        Kind = device.Kind,

        // The live path Raw Input reports, which is the only string an incoming event can be matched
        // against. Everything else here is a label.
        DevicePath = devicePath,
        InstanceId = devicePath,
        FriendlyName = device.Kind switch
        {
            VirtualHidDeviceKind.Mouse => "Nexus Mouse",
            VirtualHidDeviceKind.Keyboard => "Nexus Keyboard",
            _ => "Nexus Touch",
        },
        VendorId = NexusHidProtocol.VendorId,
        ProductId = device.ProductId,
    };

    /// <summary>
    /// What the backend reports, given what was adopted and what was not.
    /// <para>
    /// A device that fails is not fatal to the others: a backend publishing a mouse and a keyboard
    /// but no digitizer is a real state the router understands, and reporting
    /// <see cref="VirtualHidState.Partial"/> is more useful than refusing everything.
    /// </para>
    /// </summary>
    private static VirtualHidAvailability Compose(
        List<VirtualHidDeviceIdentity> published,
        List<string> failures,
        uint driverVersion)
    {
        var state = published.Count switch
        {
            0 => VirtualHidState.Refused,
            var n when n == NexusHidProtocol.MaxDevices => VirtualHidState.Ready,
            _ => VirtualHidState.Partial,
        };

        return new VirtualHidAvailability
        {
            State = state,
            Devices = published,
            BackendName = "Nexus HID",
            BackendVersion = FormatVersion(driverVersion),
            Diagnostic = failures.Count == 0
                ? null
                : $"The Nexus HID driver did not publish: {string.Join(", ", failures)}.",
        };
    }

    private static IEnumerable<(VirtualHidDeviceKind Kind, byte[] Descriptor, ushort ProductId)> Devices()
    {
        yield return (VirtualHidDeviceKind.Mouse, HidReportDescriptors.Mouse.ToArray(), NexusHidProtocol.ProductIdMouse);
        yield return (VirtualHidDeviceKind.Keyboard, HidReportDescriptors.Keyboard.ToArray(), NexusHidProtocol.ProductIdKeyboard);
        yield return (VirtualHidDeviceKind.Digitizer, HidReportDescriptors.Digitizer.ToArray(), NexusHidProtocol.ProductIdTouch);
    }

    private unsafe bool TryCreate(
        VirtualHidDeviceKind kind,
        byte[] descriptor,
        ushort productId,
        out CreatedDevice device,
        out string failure)
    {
        device = default;
        failure = string.Empty;

        if (descriptor.Length > NexusHidProtocol.MaxDescriptorLength)
        {
            failure = $"the report descriptor is {descriptor.Length} bytes, over the "
                + $"{NexusHidProtocol.MaxDescriptorLength}-byte limit";
            return false;
        }

        var request = new NexusHidCreateRequest
        {
            ProtocolVersion = NexusHidProtocol.Version,
            DeviceKind = (uint)ToDriverKind(kind),
            VendorId = NexusHidProtocol.VendorId,
            ProductId = productId,
            VersionNumber = NexusHidProtocol.DeviceVersion,
            DescriptorLength = (ushort)descriptor.Length,
        };

        fixed (byte* source = descriptor)
        {
            Buffer.MemoryCopy(source, request.Descriptor, NexusHidProtocol.MaxDescriptorLength, descriptor.Length);
        }

        if (!Control(NexusHidProtocol.IoctlCreateDevice, ref request, out NexusHidCreateReply reply))
        {
            // The IOCTL itself did not complete: a wrong buffer size, a device that went away, an
            // access denial, or a slot another control handle still owns. Distinct from a create the
            // driver ran and reported failing.
            failure = $"the create request failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        if ((NexusHidDeviceState)reply.State != NexusHidDeviceState.Ready)
        {
            // The driver ran the create and it failed. reply.Status is the NTSTATUS from VhfCreate
            // or VhfStart, and it is the only account of what went wrong that exists - the driver
            // has no trace provider of its own.
            failure = $"the driver reported {(NexusHidDeviceState)reply.State} "
                + $"with NTSTATUS 0x{(uint)reply.Status:X8}";
            return false;
        }

        device = new CreatedDevice(kind, productId, reply.ContainerId);
        return true;
    }

    public bool SubmitMouse(in HidMouseReport report)
    {
        Span<byte> buffer = stackalloc byte[HidReportDescriptors.MouseReportLength];
        report.Write(buffer);
        return Submit(VirtualHidDeviceKind.Mouse, buffer);
    }

    public bool SubmitKeyboard(in HidKeyboardReport report)
    {
        Span<byte> buffer = stackalloc byte[HidReportDescriptors.KeyboardReportLength];
        report.Write(buffer);
        return Submit(VirtualHidDeviceKind.Keyboard, buffer);
    }

    public bool SubmitDigitizer(in HidDigitizerReport report)
    {
        Span<byte> buffer = stackalloc byte[HidReportDescriptors.DigitizerReportLength];
        report.Write(buffer);
        return Submit(VirtualHidDeviceKind.Digitizer, buffer);
    }

    /// <summary>
    /// Submits and records the outcome against the lifecycle, so a refused report spends the failure
    /// budget rather than immediately moving input to another backend.
    /// </summary>
    private bool Submit(VirtualHidDeviceKind kind, ReadOnlySpan<byte> report)
    {
        var now = _time.GetUtcNow();

        if (SubmitBytes(kind, report))
        {
            _lifecycle.RecordSuccess(now);
            return true;
        }

        // One refused report is not a dead driver. Only a run of them is, and RecordFailure is what
        // holds that line.
        var state = _lifecycle.RecordFailure(now, "the Nexus HID driver refused a report");

        if (state == VirtualHidLifecycleState.Lost)
        {
            lock (_gate)
            {
                OnTransportLostLocked();
            }
        }

        return false;
    }

    private unsafe bool SubmitBytes(VirtualHidDeviceKind kind, ReadOnlySpan<byte> report)
    {
        if (report.Length > NexusHidProtocol.MaxReportLength)
        {
            return false;
        }

        lock (_gate)
        {
            if (_control is null || _control.IsInvalid)
            {
                return false;
            }

            if (!_availability.Publishes(kind))
            {
                // Holding the control handle is not the same as having this device. A create can
                // fail for one kind and succeed for the others, and the driver can be answering
                // perfectly while publishing nothing PC Core is willing to use. Reporting success
                // for a report that went to a device this backend never adopted would spend the
                // lifecycle's failure budget in the wrong direction: it would keep a backend that
                // publishes nothing looking healthy.
                return false;
            }

            var request = new NexusHidSubmitRequest
            {
                DeviceKind = (uint)ToDriverKind(kind),
                ReportLength = (ushort)report.Length,
            };

            fixed (byte* source = report)
            {
                Buffer.MemoryCopy(source, request.Report, NexusHidProtocol.MaxReportLength, report.Length);
            }

            // Whether a refusal means the driver is gone is the lifecycle's decision, not this
            // method's: one refused report is not a loss, and deciding here would reintroduce the
            // flapping the failure budget exists to prevent.
            return ControlNoReplyLocked(NexusHidProtocol.IoctlSubmitReport, ref request);
        }
    }

    public void Neutralize()
    {
        lock (_gate)
        {
            if (_control is null || _control.IsInvalid)
            {
                return;
            }

            var request = new NexusHidNeutralizeRequest { Reserved = 0 };
            ControlNoReplyLocked(NexusHidProtocol.IoctlNeutralize, ref request);
        }
    }

    private void OnTransportLostLocked()
    {
        Disconnect();
        _lifecycle.Lose(_time.GetUtcNow(), "the Nexus HID driver stopped answering");

        _availability = new VirtualHidAvailability
        {
            State = VirtualHidState.Unavailable,
            Devices = [],
            Diagnostic = "The Nexus HID driver stopped answering; PC Core has fallen back to injection.",
        };

        _nextProbe = _time.GetUtcNow() + RetryInterval;
    }

    /// <summary>
    /// Drops the control handle and untrusts every device it published.
    /// <para>
    /// The registry clear is the security-relevant half. Devices Nexus can no longer talk to are
    /// devices Nexus can no longer vouch for, and leaving their paths trusted would mean a device
    /// path Windows later reassigns is treated as Nexus's own.
    /// </para>
    /// </summary>
    private void Disconnect()
    {
        _registry.Clear();
        _control?.Dispose();
        _control = null;
        _controlInstanceId = null;
    }

    private string? ControlInstanceId()
    {
        lock (_gate)
        {
            return _controlInstanceId;
        }
    }

    /// <summary>
    /// Releases every published device and drops the control handle, deliberately and in that
    /// order, without disposing this object.
    /// <para>
    /// <b>The driver's file-cleanup path is a backstop, not the release mechanism.</b> Closing the
    /// control handle does make the driver neutralise on its own — that is what covers a crash or a
    /// kill — but relying on it for an ordinary shutdown means the last thing Nexus does with a
    /// held button is drop a handle and hope. It also inverts the order: cleanup neutralises
    /// <em>because</em> the handle went away, so the release travels through a path that only runs
    /// once the client is already gone and can no longer observe whether it worked. Doing it here
    /// means the neutral reports are submitted while the connection is still open, their acceptance
    /// is visible, and the handle closes over a device that is already quiet.
    /// </para>
    /// <para>Safe when nothing is connected, and safe to call twice.</para>
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            // Best effort: if the handle is already gone the driver has neutralised on its own,
            // because losing the control handle is exactly what it watches for.
            try
            {
                Neutralize();
            }
            catch (Exception)
            {
                // Shutdown must not throw. The driver's own cleanup path covers this case.
            }

            Disconnect();

            // Absent rather than Lost: nothing failed, so there is no cooldown to serve and nothing
            // for a later probe to back off from.
            _lifecycle.Reset();

            _availability = VirtualHidAvailability.None(
                "PC Core released the Nexus HID devices and closed the control handle.");
        }
    }

    public void Dispose() => Shutdown();

    private static NexusHidDeviceKind ToDriverKind(VirtualHidDeviceKind kind) => kind switch
    {
        VirtualHidDeviceKind.Mouse => NexusHidDeviceKind.Mouse,
        VirtualHidDeviceKind.Keyboard => NexusHidDeviceKind.Keyboard,
        _ => NexusHidDeviceKind.Touch,
    };

    private static string FormatVersion(uint packed) =>
        $"{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

    private bool TryGetVersion(out NexusHidVersionReply version)
    {
        version = default;

        if (_control is null || _control.IsInvalid)
        {
            return false;
        }

        return NexusHidControlDevice.Control(_control, NexusHidProtocol.IoctlGetVersion, ref version);
    }

    private bool Control<TRequest, TReply>(uint code, ref TRequest request, out TReply reply)
        where TRequest : unmanaged
        where TReply : unmanaged
    {
        reply = default;

        lock (_gate)
        {
            return _control is { IsInvalid: false }
                && NexusHidControlDevice.Control(_control, code, ref request, ref reply);
        }
    }

    private bool ControlNoReplyLocked<TRequest>(uint code, ref TRequest request)
        where TRequest : unmanaged =>
        _control is { IsInvalid: false } && NexusHidControlDevice.ControlNoReply(_control, code, ref request);
}
