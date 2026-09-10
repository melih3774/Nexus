using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Input;

/// <summary>
/// Which mechanism is actually carrying input right now.
/// <para>
/// This is a diagnostic, not a setting. The owner never picks one: Nexus uses the best path
/// available and says which it got. Direct and Touchpad remain a choice about how the phone
/// behaves, and have nothing to do with this.
/// </para>
/// </summary>
public enum InputPath
{
    /// <summary>
    /// The Nexus Input Broker: a separate elevated process that injects on PC Core's behalf, so
    /// ordinary Administrator windows stay controllable.
    /// </summary>
    [JsonStringEnumMemberName("NATIVE_BROKER")]
    NativeBroker,

    /// <summary>
    /// PC Core injecting for itself, as a standard-user process. Everything works except windows
    /// belonging to elevated applications.
    /// </summary>
    [JsonStringEnumMemberName("LOCAL")]
    Local,
}

/// <summary>
/// Which mechanism actually generates input, named for what it is.
/// <para>
/// <b>This vocabulary exists because the old one was misleading.</b> Up to v0.8 the elevated broker
/// was called "native input", and "native" reads as "the operating system's own input path" — a
/// device. It is not one. The broker runs the same <c>SendInput</c> and <c>InjectTouchInput</c>
/// calls PC Core runs, in a process at a higher integrity level; that solves exactly one problem,
/// which is that Windows refuses input aimed at an Administrator window from a medium-integrity
/// process, and it solves nothing about how Windows perceives the input. These three names each say
/// which of those two questions they answer.
/// </para>
/// <para>
/// <see cref="InputPath"/> remains on the wire alongside this, unchanged, so the shipped Android
/// client keeps decoding <c>input.status</c> while it is updated to the clearer vocabulary.
/// </para>
/// </summary>
public enum InputBackend
{
    /// <summary>
    /// Windows enumerates a Nexus HID device and the input arrives as HID reports from it. The only
    /// value of the three that means Windows perceives hardware. Not reachable on this machine yet:
    /// it needs a virtual-HID backend that is installed and publishing.
    /// </summary>
    [JsonStringEnumMemberName("VIRTUAL_HID")]
    VirtualHid,

    /// <summary>
    /// The Nexus Input Broker: <c>SendInput</c> and <c>InjectTouchInput</c>, run by a separate
    /// elevated process so that ordinary Administrator windows accept them. Windows perceives
    /// injected input, from a program.
    /// </summary>
    [JsonStringEnumMemberName("ELEVATED_INJECTION")]
    ElevatedInjection,

    /// <summary>
    /// The same calls, made by PC Core in its own standard-user process. Everything works except
    /// windows belonging to elevated applications.
    /// </summary>
    [JsonStringEnumMemberName("LOCAL_INJECTION")]
    LocalInjection,
}

/// <summary>
/// Which backend carries each kind of input, reported separately because they can differ.
/// <para>
/// <b>One value would be a lie the moment virtual HID is partly installed.</b> A backend that
/// publishes a mouse and a keyboard but no digitizer is a realistic and useful state — it is what
/// integrating a third-party backend would produce, since the maintained ones publish mice and
/// keyboards and no Windows touch device at all. In that state the pointer is genuinely hardware and
/// Direct-mode touch genuinely is not, and a single summary line cannot say so.
/// </para>
/// </summary>
public sealed record InputBackends
{
    public required InputBackend Mouse { get; init; }

    public required InputBackend Keyboard { get; init; }

    public required InputBackend Touch { get; init; }

    /// <summary>
    /// True when arbitrary Unicode text still goes through character injection rather than the
    /// keyboard backend.
    /// <para>
    /// Always true today and likely to stay true: a HID boot keyboard sends key usages, and there is
    /// no key usage for an arbitrary character. Turkish text works because it is injected as
    /// characters, and pretending otherwise would break it — the same key usage produces a different
    /// character on a different keyboard layout, so routing text through HID would silently corrupt
    /// it. Reported so the owner can see that one path differs rather than discovering it.
    /// </para>
    /// <para>
    /// <b>It has one visible consequence, measured on this machine.</b> Keys sent through the HID
    /// keyboard reach an elevated application's window, because they arrive from a device and UIPI
    /// has nothing to filter. Text sent as characters does not: it is a <c>SendInput</c> call from a
    /// medium-integrity process, and Windows drops it at the window. So <c>input.key</c> works in an
    /// elevated window and <c>input.text</c> does not.
    /// </para>
    /// </summary>
    public required bool TextUsesCharacterInjection { get; init; }

    /// <summary>True when all three are the same backend, which is every state Nexus has shipped.</summary>
    public bool Uniform => Mouse == Keyboard && Keyboard == Touch;

    public static InputBackends All(InputBackend backend) => new()
    {
        Mouse = backend,
        Keyboard = backend,
        Touch = backend,
        TextUsesCharacterInjection = true,
    };
}

/// <summary>How the native path is doing. Never optimistic: a degraded path says so.</summary>
public enum NativeInputState
{
    /// <summary>Installed, running elevated, and in use. Elevated windows are reachable.</summary>
    [JsonStringEnumMemberName("READY")]
    Ready,

    /// <summary>
    /// The broker is connected but is <em>not</em> running elevated, so it reaches no further than
    /// PC Core does. Reported rather than hidden: claiming elevated input that does not work would
    /// be worse than not having it.
    /// </summary>
    [JsonStringEnumMemberName("DEGRADED")]
    Degraded,

    /// <summary>Native input has never been installed on this machine.</summary>
    [JsonStringEnumMemberName("NOT_INSTALLED")]
    NotInstalled,

    /// <summary>Installed, but not answering. PC Core is injecting for itself in the meantime.</summary>
    [JsonStringEnumMemberName("UNAVAILABLE")]
    Unavailable,
}

/// <summary>
/// The full picture of the input stack, for diagnostics and for the phone's status line.
/// </summary>
public sealed record NativeInputStatus
{
    public required InputPath Path { get; init; }

    public required NativeInputState State { get; init; }

    /// <summary>True when input can reach the windows of ordinary elevated applications.</summary>
    public required bool ReachesElevatedWindows { get; init; }

    /// <summary>True when Direct mode can use real Windows touch rather than a dragged pointer.</summary>
    public required bool TouchAvailable { get; init; }

    /// <summary>
    /// True when this machine has a pen or touch digitizer of its own, independent of Nexus.
    /// <para>
    /// Relevant to security, not to input: on a machine with a real touchscreen, a person touching
    /// it produces the same kind of promoted mouse messages Nexus's own touch does.
    /// </para>
    /// </summary>
    public required bool PhysicalDigitizer { get; init; }

    /// <summary><c>DEFAULT</c>, <c>SECURE</c>, <c>OTHER</c> or <c>UNKNOWN</c>.</summary>
    public required string Desktop { get; init; }

    /// <summary>True while Windows is showing a UAC prompt, the lock screen or Ctrl+Alt+Del.</summary>
    public bool SecureDesktopActive => Desktop is not "DEFAULT";

    /// <summary>
    /// Whether an owner-authenticated session can drive the Secure Desktop.
    /// <para>
    /// True exactly when the pointer and the keyboard are Nexus HID devices, because that is the
    /// only backend that can: a device produces reports Windows delivers to whichever desktop has
    /// focus, and <c>SendInput</c> from the interactive session cannot reach Winlogon at all. It is
    /// reported rather than assumed, because the honest answer on a machine without the driver is
    /// no — and an owner who cannot answer a UAC prompt remotely should be told why.
    /// </para>
    /// </summary>
    public bool ReachesSecureDesktop =>
        Backends.Mouse == InputBackend.VirtualHid && Backends.Keyboard == InputBackend.VirtualHid;

    /// <summary>
    /// Whether the privileged capture helper is installed, so the Secure Desktop is also
    /// <em>visible</em> remotely rather than only controllable.
    /// </summary>
    public bool SecureDesktopCaptureInstalled { get; init; }

    public string? BrokerVersion { get; init; }

    /// <summary>Short, safe to show. Why the native path is not in use, when it is not.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>
    /// Which backend carries each kind of input.
    /// <para>
    /// Derived rather than stored, so that no existing call site has to be changed to keep it
    /// truthful, and so it cannot drift from <see cref="Path"/>. Once a virtual-HID backend exists,
    /// this is where its per-device availability lands — a backend publishing a mouse and a keyboard
    /// but no digitizer sets the first two to <see cref="InputBackend.VirtualHid"/> and leaves Touch
    /// on whichever injection path is live.
    /// </para>
    /// </summary>
    public InputBackends Backends
    {
        get => _backends ?? InputBackends.All(
            Path == InputPath.NativeBroker ? InputBackend.ElevatedInjection : InputBackend.LocalInjection);

        init => _backends = value;
    }

    /// <summary>
    /// What the router actually decided, when someone bothered to ask it.
    /// <para>
    /// <b>The fallback above used to be the only answer, and it could never say VIRTUAL_HID.</b> It
    /// derives the backend from <see cref="Path"/>, which only distinguishes the broker from local
    /// injection — so a machine with three published, resolved, handshaken Nexus HID devices still
    /// reported local injection on all three kinds. The status was computed from the wrong source,
    /// not from a wrong decision.
    /// </para>
    /// </summary>
    private readonly InputBackends? _backends;

    /// <summary>
    /// The per-kind backend line, and why it is not virtual HID.
    /// <para>
    /// Deliberately more than one line when the answer is more than one thing. The old single-line
    /// summary said "Native input: not installed", which is true and tells the owner nothing about
    /// what is carrying their pointer right now.
    /// </para>
    /// </summary>
    public string BackendSummary
    {
        get
        {
            var backends = Backends;
            var reason = VirtualHidDiagnostic ?? "No virtual HID backend is installed.";

            return $"Mouse: {Describe(backends.Mouse)} · Keyboard: {Describe(backends.Keyboard)} · "
                + $"Touch: {Describe(backends.Touch)} — {reason}";

            static string Describe(InputBackend backend) => backend switch
            {
                InputBackend.VirtualHid => "Virtual HID",
                InputBackend.ElevatedInjection => "Elevated injection",
                _ => "Local injection",
            };
        }
    }

    /// <summary>
    /// Why virtual HID is not carrying input, when it is not. Null once a backend is publishing.
    /// </summary>
    public string? VirtualHidDiagnostic { get; init; }

    /// <summary>
    /// A one-line summary, which is all the phone shows.
    /// <para>
    /// <b>Virtual HID answers first, because it is the better answer.</b> <see cref="State"/> and
    /// <see cref="Path"/> describe the elevated broker and stay on the wire unchanged for the
    /// shipped Android client — but on a machine carrying input through real Nexus HID devices,
    /// "Native input: not installed" is true about the broker and useless about the machine. The
    /// broker is not installed and is also not needed.
    /// </para>
    /// </summary>
    public string Summary
    {
        get
        {
            var backends = Backends;

            if (backends.Mouse == InputBackend.VirtualHid && backends.Keyboard == InputBackend.VirtualHid)
            {
                return backends.Touch == InputBackend.VirtualHid
                    ? "Virtual HID: mouse, keyboard and touch"
                    : "Virtual HID: mouse and keyboard";
            }

            return BrokerSummary;
        }
    }

    private string BrokerSummary => (Path, State) switch
    {
        (InputPath.NativeBroker, NativeInputState.Ready) => "Native input: ready",
        (InputPath.NativeBroker, NativeInputState.Degraded) => "Native input: not elevated",
        (_, NativeInputState.NotInstalled) => "Native input: not installed",
        (_, NativeInputState.Unavailable) => "Native input: unavailable",
        _ => "Standard input",
    };

    /// <summary>
    /// The sentence the owner reads about what is carrying their input.
    /// <para>
    /// <b>Here rather than at the one call site, because it has to agree with the fields beside
    /// it.</b> The router used to compose this from the broker's installation state alone, which
    /// produced "elevated application windows are out of reach" on a machine whose own
    /// <see cref="ReachesElevatedWindows"/> was true in the same object. Both facts were computed a
    /// few lines apart and nothing made them agree. Keeping the rule next to the fields it must be
    /// consistent with is what makes that a testable invariant rather than a matter of care.
    /// </para>
    /// </summary>
    /// <param name="backends">Which backend is carrying each kind.</param>
    /// <param name="brokerRegistered">Whether the elevated broker task is installed.</param>
    /// <param name="brokerDiagnostic">What the broker said, when it is the thing being described.</param>
    public static string DescribeInputPath(
        InputBackends backends,
        bool brokerRegistered,
        string? brokerDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(backends);

        // A device produces reports Windows delivers to whichever desktop has focus, so UIPI has
        // nothing to filter and the broker is not merely absent but unnecessary.
        if (backends.Mouse == InputBackend.VirtualHid && backends.Keyboard == InputBackend.VirtualHid)
        {
            return "Nexus HID devices are carrying input, so elevated application windows are "
                + "reachable. The elevated broker is not installed and is not needed.";
        }

        return brokerRegistered
            ? brokerDiagnostic
              ?? "Native input is installed but not responding; PC Core is injecting input itself."
            : "Native input is not installed. PC Core is injecting input itself, so elevated "
              + "application windows are out of reach.";
    }
}
