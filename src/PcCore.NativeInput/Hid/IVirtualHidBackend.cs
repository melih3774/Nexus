namespace PcCore.NativeInput.Hid;

/// <summary>The three devices Nexus asks a backend to publish.</summary>
public enum VirtualHidDeviceKind
{
    Mouse,
    Keyboard,
    Digitizer,
}

/// <summary>
/// What Windows ended up calling one published device, and how to recognise its input later.
/// <para>
/// <b><see cref="DevicePath"/> is the load-bearing field.</b> It is what Raw Input reports as the
/// source of an event, so it is what lets the security detector say "that came from the Nexus mouse"
/// instead of inferring it from a tag or a timing window. A backend that cannot tell Nexus the
/// device path of what it created cannot be used while ARMED, because the alternative is guessing
/// about whether the owner is at their desk.
/// </para>
/// </summary>
public sealed record VirtualHidDeviceIdentity
{
    public required VirtualHidDeviceKind Kind { get; init; }

    /// <summary>
    /// The Windows device interface path, as Raw Input reports it. The comparison key for
    /// attribution, and compared case-insensitively because Windows is inconsistent about case here.
    /// </summary>
    public required string DevicePath { get; init; }

    /// <summary>The PnP instance id, for diagnostics and for the owner to find in Device Manager.</summary>
    public string? InstanceId { get; init; }

    /// <summary>The name Windows shows. Expected to be "Nexus Mouse" and so on.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Vendor and product id the backend published, when it published any.</summary>
    public ushort VendorId { get; init; }

    public ushort ProductId { get; init; }
}

/// <summary>
/// How Raw Input classifies one device. Mirrored here so PcCore.NativeInput can take a device list
/// without referencing the project that owns the Raw Input P/Invoke.
/// </summary>
public enum RawInputDeviceClass
{
    Mouse,
    Keyboard,

    /// <summary>Everything else Raw Input reports, including digitizers.</summary>
    Hid,
}

/// <summary>One device as Raw Input reports it: the interface path, and what kind it is.</summary>
/// <param name="Path">
/// The exact string Raw Input returns for <c>RIDI_DEVICENAME</c>, which is the same string it will
/// report as the source of an event. Matching on anything else risks a near miss.
/// </param>
public readonly record struct RawInputDeviceInfo(string Path, RawInputDeviceClass Class);

/// <summary>Why a virtual-HID backend is or is not carrying input.</summary>
public enum VirtualHidState
{
    /// <summary>Every requested device is published and reports are being accepted.</summary>
    Ready,

    /// <summary>
    /// Some devices are published and others are not. Reported rather than rounded to either
    /// neighbour: a backend with a mouse and no digitizer is a real and useful state, and Direct
    /// mode needs to know it has to fall back while Touchpad mode does not.
    /// </summary>
    Partial,

    /// <summary>No backend is installed on this machine.</summary>
    NotInstalled,

    /// <summary>Installed, but not answering — a stopped service, a driver mid-upgrade, a crash.</summary>
    Unavailable,

    /// <summary>
    /// Installed and answering, but refusing to publish devices — an expired or missing licence, a
    /// protocol-version mismatch, a policy block. Distinct from <see cref="Unavailable"/> because
    /// the owner has something to do about it and the diagnostic should say what.
    /// </summary>
    Refused,
}

/// <summary>What a backend reported about itself when it was last probed.</summary>
public sealed record VirtualHidAvailability
{
    public required VirtualHidState State { get; init; }

    /// <summary>The devices that are actually published right now. Empty unless Ready or Partial.</summary>
    public required IReadOnlyList<VirtualHidDeviceIdentity> Devices { get; init; }

    /// <summary>Short, safe to show the owner. Why it is not Ready, when it is not.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>The backend's own name and version, for the status line and for bug reports.</summary>
    public string? BackendName { get; init; }

    public string? BackendVersion { get; init; }

    public bool Publishes(VirtualHidDeviceKind kind) => Devices.Any(d => d.Kind == kind);

    public static VirtualHidAvailability None(string diagnostic) => new()
    {
        State = VirtualHidState.NotInstalled,
        Devices = [],
        Diagnostic = diagnostic,
    };
}

/// <summary>
/// A source of real HID devices: something that makes Windows enumerate a mouse, a keyboard and a
/// touch screen, and then carries reports to them.
/// <para>
/// <b>This interface is the whole point of the abstraction, and it is deliberately narrow.</b> It
/// takes finished HID reports and returns whether they were delivered. It does not know about
/// Nexus's protocol, about displays, about sessions, or about the phone. Anything that could be
/// decided without a driver — the descriptors, the coordinate mapping, the gesture semantics — has
/// already been decided by the time a call arrives here, so that swapping the implementation cannot
/// change behaviour that has tests.
/// </para>
/// <para>
/// <b>Two implementations are anticipated and neither is installed.</b> A custom Nexus UMDF2 source
/// driver built on the Virtual HID Framework, and a third-party signed backend. They differ in
/// licensing, in what they can publish, and in what they cost; they do not differ in what this
/// interface needs from them, which is why the decision can be deferred without stalling the code
/// around it.
/// </para>
/// <para>
/// <b>Implementations must neutralise on the way out.</b> A backend that disappears while a button
/// is held leaves that button held at the HID level, where nothing Nexus does afterwards can release
/// it. <see cref="Neutralize"/> exists to be called on disconnect, on session end, and from a
/// finaliser of last resort.
/// </para>
/// </summary>
public interface IVirtualHidBackend : IDisposable
{
    /// <summary>What the backend reported at the last probe. Never null, never optimistic.</summary>
    VirtualHidAvailability Availability { get; }

    /// <summary>
    /// Re-examines the backend and republishes devices if it can.
    /// <para>
    /// Called on a timer and after a failure, never on the hot path. Implementations must be cheap
    /// when the answer has not changed, because a machine with no backend installed will call this
    /// forever.
    /// </para>
    /// </summary>
    VirtualHidAvailability Probe();

    /// <summary>Submits one mouse report. False when the backend could not deliver it.</summary>
    bool SubmitMouse(in HidMouseReport report);

    /// <summary>Submits one keyboard report.</summary>
    bool SubmitKeyboard(in HidKeyboardReport report);

    /// <summary>Submits one digitizer report.</summary>
    bool SubmitDigitizer(in HidDigitizerReport report);

    /// <summary>
    /// Releases everything: buttons up, keys up, contacts lifted and out of range.
    /// <para>
    /// Must be safe to call when nothing is held, when the backend is already gone, and twice.
    /// </para>
    /// </summary>
    void Neutralize();

    /// <summary>
    /// Neutralises, then releases whatever connection the backend holds — without disposing it.
    /// <para>
    /// <b>Separate from <see cref="IDisposable.Dispose"/> because shutdown order matters.</b> A
    /// container disposes singletons last, after everything that might still be sending reports has
    /// stopped but also after the point where anything can observe the result. Releasing held input
    /// is not bookkeeping that can happen whenever: it is the difference between a shutdown that
    /// leaves a key held at the HID level and one that does not, and the owner has no way to undo
    /// the former from software. So it gets its own call, made deliberately at a known moment.
    /// </para>
    /// <para>
    /// Implementations that hold no connection may simply neutralise. Must be safe to call twice,
    /// and safe to call before anything was ever connected.
    /// </para>
    /// </summary>
    void Shutdown();
}

/// <summary>
/// The backend used when there is none: it publishes nothing and refuses everything.
/// <para>
/// <b>A real object rather than a null.</b> The router asks the backend what it can do on every
/// event; a null would mean a branch at every call site and a nullable field that someone eventually
/// dereferences. This costs one virtual call and removes the whole category.
/// </para>
/// <para>
/// This is the backend Nexus is running today, and it is honest about it: the status line reads
/// <c>not installed</c>, and nothing anywhere claims virtual HID is working.
/// </para>
/// </summary>
public sealed class NoVirtualHidBackend : IVirtualHidBackend
{
    private readonly VirtualHidAvailability _availability;

    public NoVirtualHidBackend(string? diagnostic = null) =>
        _availability = VirtualHidAvailability.None(
            diagnostic ?? "No virtual HID backend is installed, so Windows enumerates no Nexus input devices.");

    public VirtualHidAvailability Availability => _availability;

    public VirtualHidAvailability Probe() => _availability;

    public bool SubmitMouse(in HidMouseReport report) => false;

    public bool SubmitKeyboard(in HidKeyboardReport report) => false;

    public bool SubmitDigitizer(in HidDigitizerReport report) => false;

    public void Neutralize()
    {
        // Nothing was ever held, because nothing was ever delivered.
    }

    public void Shutdown()
    {
        // Nothing was ever connected, so there is nothing to release and nothing to close.
    }

    public void Dispose()
    {
    }
}
