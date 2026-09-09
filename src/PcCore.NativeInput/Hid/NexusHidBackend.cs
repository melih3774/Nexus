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

    private readonly Lock _gate = new();
    private readonly NexusHidDeviceRegistry _registry;
    private readonly TimeProvider _time;

    private SafeFileHandle? _control;
    private VirtualHidAvailability _availability;
    private DateTimeOffset _nextProbe = DateTimeOffset.MinValue;

    public NexusHidBackend(NexusHidDeviceRegistry registry, TimeProvider? time = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _time = time ?? TimeProvider.System;
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

    public VirtualHidAvailability Probe()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            if (_control is { IsInvalid: false } && _availability.State == VirtualHidState.Ready)
            {
                return _availability;
            }

            if (now < _nextProbe)
            {
                return _availability;
            }

            _nextProbe = now + RetryInterval;
            return ConnectLocked();
        }
    }

    private VirtualHidAvailability ConnectLocked()
    {
        Disconnect();

        var path = NexusHidControlDevice.Find();
        if (path is null)
        {
            return _availability = VirtualHidAvailability.None(
                "The Nexus HID driver is not installed, so Windows enumerates no Nexus input devices.");
        }

        var handle = NexusHidControlDevice.Open(path);
        if (handle is null || handle.IsInvalid)
        {
            handle?.Dispose();
            return _availability = new VirtualHidAvailability
            {
                State = VirtualHidState.Unavailable,
                Devices = [],
                Diagnostic = "The Nexus HID driver is installed but its control device could not be opened.",
            };
        }

        _control = handle;

        if (!TryGetVersion(out var version))
        {
            Disconnect();
            return _availability = new VirtualHidAvailability
            {
                State = VirtualHidState.Unavailable,
                Devices = [],
                Diagnostic = "The Nexus HID driver did not answer a version request.",
            };
        }

        if (version.ProtocolVersion != NexusHidProtocol.Version)
        {
            // Deliberately not "use it anyway". See the class remarks.
            Disconnect();
            return _availability = new VirtualHidAvailability
            {
                State = VirtualHidState.Refused,
                Devices = [],
                Diagnostic = $"The Nexus HID driver speaks protocol {version.ProtocolVersion}, "
                    + $"this build speaks {NexusHidProtocol.Version}. Update the driver or PC Core.",
            };
        }

        return _availability = PublishLocked(version.DriverVersion);
    }

    /// <summary>
    /// Creates the three devices and records what the driver reported about each.
    /// <para>
    /// A device that fails to create is not fatal to the others: a backend publishing a mouse and a
    /// keyboard but no digitizer is a real state the router understands, and reporting
    /// <see cref="VirtualHidState.Partial"/> is more useful than refusing everything.
    /// </para>
    /// </summary>
    private VirtualHidAvailability PublishLocked(uint driverVersion)
    {
        var published = new List<VirtualHidDeviceIdentity>();
        var failures = new List<string>();

        foreach (var (kind, descriptor, productId) in Devices())
        {
            if (TryCreate(kind, descriptor, productId, out var identity) && identity is not null)
            {
                published.Add(identity);
                _registry.Register(identity);
            }
            else
            {
                failures.Add(kind.ToString());
            }
        }

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
        out VirtualHidDeviceIdentity? identity)
    {
        identity = null;

        if (descriptor.Length > NexusHidProtocol.MaxDescriptorLength)
        {
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
            return false;
        }

        if ((NexusHidDeviceState)reply.State != NexusHidDeviceState.Ready)
        {
            return false;
        }

        string instanceId;
        var length = Math.Min((int)reply.DeviceInstanceIdLength, NexusHidProtocol.InstanceIdCapacity);
        instanceId = new string(reply.DeviceInstanceId, 0, length);

        identity = new VirtualHidDeviceIdentity
        {
            Kind = kind,
            // The instance id is what the driver knows at creation time. Attribution keys on the
            // device path Raw Input reports, which the listener resolves and reconciles against
            // this; a device with no instance id is unusable for attribution and so is refused.
            DevicePath = instanceId,
            InstanceId = instanceId,
            FriendlyName = kind switch
            {
                VirtualHidDeviceKind.Mouse => "Nexus Mouse",
                VirtualHidDeviceKind.Keyboard => "Nexus Keyboard",
                _ => "Nexus Touch",
            },
            VendorId = NexusHidProtocol.VendorId,
            ProductId = productId,
        };

        return !string.IsNullOrWhiteSpace(instanceId);
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

    private unsafe bool Submit(VirtualHidDeviceKind kind, ReadOnlySpan<byte> report)
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

            var request = new NexusHidSubmitRequest
            {
                DeviceKind = (uint)ToDriverKind(kind),
                ReportLength = (ushort)report.Length,
            };

            fixed (byte* source = report)
            {
                Buffer.MemoryCopy(source, request.Report, NexusHidProtocol.MaxReportLength, report.Length);
            }

            if (ControlNoReplyLocked(NexusHidProtocol.IoctlSubmitReport, ref request))
            {
                return true;
            }

            // A failed submit is how a driver that has gone away announces itself on the hot path.
            // Drop the connection and let the next probe find out; the router falls back meanwhile.
            OnTransportLostLocked();
            return false;
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
    }

    public void Dispose()
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
                // Disposal must not throw. The driver's own cleanup path covers this case.
            }

            Disconnect();
        }
    }

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
