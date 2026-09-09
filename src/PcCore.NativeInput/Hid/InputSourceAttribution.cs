using System.Collections.Immutable;

namespace PcCore.NativeInput.Hid;

/// <summary>What produced one observed input event, once the device behind it is known.</summary>
public enum InputSource
{
    /// <summary>
    /// A device Nexus published and is driving. Never raises a security incident.
    /// <para>
    /// Only ever returned for a device whose identity Nexus recorded at publication time. There is
    /// no heuristic that promotes a device into this value.
    /// </para>
    /// </summary>
    NexusVirtualHid,

    /// <summary>
    /// Anything else: a real mouse, a real keyboard, a device Nexus does not recognise, or an event
    /// whose device could not be resolved. Can raise an incident while ARMED.
    /// </summary>
    Physical,
}

/// <summary>Why the classifier decided what it decided. Diagnostics, and it is worth logging.</summary>
public enum InputSourceReason
{
    /// <summary>The device path matched one Nexus recorded when it published the device.</summary>
    MatchedPublishedDevice,

    /// <summary>The device path is not one of Nexus's. An ordinary physical device.</summary>
    NotAPublishedDevice,

    /// <summary>
    /// The device could not be resolved to a path at all — a removed device, a handle Windows no
    /// longer knows, or a failed query. Treated as physical, deliberately.
    /// </summary>
    Unresolvable,

    /// <summary>
    /// Nexus has published nothing, so every device is by definition someone else's. The state this
    /// machine is in today.
    /// </summary>
    NothingPublished,
}

/// <summary>One classification, with the reasoning attached.</summary>
public readonly record struct InputSourceVerdict(InputSource Source, InputSourceReason Reason)
{
    public bool IsNexus => Source == InputSource.NexusVirtualHid;
}

/// <summary>
/// Which HID device paths belong to Nexus, so that input arriving from them can be told apart from
/// a person at the keyboard.
/// <para>
/// <b>This replaces the <c>dwExtraInfo</c> tag, and it has to.</b> v0.7 attribution works because a
/// <c>SendInput</c> event carries a caller-supplied field that a real keypress does not. A HID
/// report has no such field: it travels the real HID stack and arrives indistinguishable from
/// hardware, which is the entire point of the virtual-HID work and simultaneously the thing that
/// breaks the old mechanism. What remains distinguishable is <em>which device</em> it came from, and
/// Raw Input reports that.
/// </para>
/// <para>
/// <b>Identity is the device path, not the name.</b> A friendly string like "Nexus Mouse" is
/// attacker-chosen — anything able to publish a HID device can publish one with that name, and a
/// security decision must not rest on a display string. The registry is populated from what the
/// driver reports at creation time, when Nexus knows with certainty which PnP identity it just
/// caused to exist.
/// </para>
/// <para>
/// <b>Fail closed, in both directions.</b> An unknown device is physical. An unresolvable device is
/// physical. An empty registry makes everything physical. The failure mode of being wrong in this
/// direction is a false alarm the owner dismisses; the failure mode of the other direction is an
/// intruder typing while Nexus reports the machine is fine.
/// </para>
/// <para>
/// <b>Device paths are not guaranteed stable across reinstall.</b> They are stable across the life
/// of a published device, which is what matters, because the registry is repopulated every time
/// Nexus publishes. Nothing persists a device path to disk, and nothing should: a stale trusted path
/// that Windows later reassigns to a different device would be a trusted physical keyboard.
/// </para>
/// </summary>
public sealed class NexusHidDeviceRegistry
{
    private readonly Lock _gate = new();

    private ImmutableDictionary<string, VirtualHidDeviceIdentity> _published =
        ImmutableDictionary.Create<string, VirtualHidDeviceIdentity>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every device Nexus currently believes it has published.</summary>
    public IReadOnlyCollection<VirtualHidDeviceIdentity> Published
    {
        get
        {
            lock (_gate)
            {
                return _published.Values.ToArray();
            }
        }
    }

    /// <summary>True when Nexus has published nothing, which is the state before any install.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _published.IsEmpty;
            }
        }
    }

    /// <summary>
    /// Records a device Nexus just published. Called by the backend, with what the driver reported.
    /// <para>
    /// An identity with a blank device path is rejected rather than stored: it could never match an
    /// incoming event, and storing it would make <see cref="IsEmpty"/> lie about whether attribution
    /// is usable.
    /// </para>
    /// </summary>
    public bool Register(VirtualHidDeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.DevicePath))
        {
            return false;
        }

        lock (_gate)
        {
            _published = _published.SetItem(identity.DevicePath, identity);
        }

        return true;
    }

    /// <summary>Forgets one device, by path. Called when a device is deleted or disappears.</summary>
    public bool Forget(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_published.ContainsKey(devicePath))
            {
                return false;
            }

            _published = _published.Remove(devicePath);
            return true;
        }
    }

    /// <summary>
    /// Forgets everything. Called when the backend disconnects, the driver stops answering, or PC
    /// Core shuts down — every path where Nexus can no longer be sure what it owns.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _published = _published.Clear();
        }
    }

    /// <summary>
    /// Classifies one observed event by the device path Raw Input reported for it.
    /// <para>
    /// <paramref name="devicePath"/> being null or empty is the unresolvable case: Windows could not
    /// name the device, which happens for a device removed between the event and the query.
    /// </para>
    /// </summary>
    public InputSourceVerdict Classify(string? devicePath)
    {
        lock (_gate)
        {
            if (_published.IsEmpty)
            {
                return new InputSourceVerdict(InputSource.Physical, InputSourceReason.NothingPublished);
            }

            if (string.IsNullOrWhiteSpace(devicePath))
            {
                return new InputSourceVerdict(InputSource.Physical, InputSourceReason.Unresolvable);
            }

            return _published.ContainsKey(devicePath)
                ? new InputSourceVerdict(InputSource.NexusVirtualHid, InputSourceReason.MatchedPublishedDevice)
                : new InputSourceVerdict(InputSource.Physical, InputSourceReason.NotAPublishedDevice);
        }
    }

    /// <summary>
    /// Whether a device path <em>looks</em> like one of Nexus's, by vendor and product id.
    /// <para>
    /// <b>Diagnostic only, and never an input to a security decision.</b> A Raw Input device path
    /// for a HID device embeds its identifiers, so a Nexus device reads
    /// <c>\\?\HID#VID_F055&amp;PID_0001#...</c>. That is useful when explaining to the owner why a
    /// device was not trusted — "this looks like a Nexus mouse but was not published by this
    /// process" is a far better diagnostic than silence — but publishing a device with those ids is
    /// something any driver install can do, so trusting it would move the security boundary to
    /// whoever can install a driver.
    /// </para>
    /// </summary>
    public static bool LooksLikeNexusIdentifiers(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        return devicePath.Contains($"VID_{NexusHidProtocol.VendorId:X4}", StringComparison.OrdinalIgnoreCase)
            && (devicePath.Contains($"PID_{NexusHidProtocol.ProductIdMouse:X4}", StringComparison.OrdinalIgnoreCase)
                || devicePath.Contains($"PID_{NexusHidProtocol.ProductIdKeyboard:X4}", StringComparison.OrdinalIgnoreCase)
                || devicePath.Contains($"PID_{NexusHidProtocol.ProductIdTouch:X4}", StringComparison.OrdinalIgnoreCase));
    }
}
