using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Input;
using PcCore.NativeInput;
using PcCore.NativeInput.Hid;

namespace PcCore.Windows.Input;

/// <summary>
/// Chooses the input path, on every event, without ever asking the owner.
/// <para>
/// <b>This is the whole point of the native input work.</b> There is no "enable admin input" and no
/// mode to turn on each session: if the Nexus Input Broker is installed and healthy, it carries the
/// input, and if it is not, PC Core injects for itself exactly as it always did. The phone sends the
/// same messages either way. Direct and Touchpad stay what they always were — a choice about how the
/// phone behaves — and have nothing to do with which of these two paths is live.
/// </para>
/// <para>
/// <b>Failure is a fallback, not an error.</b> A broker that dies mid-session takes its held buttons
/// and contacts with it — it releases everything on disconnect — and the next event goes through the
/// local path. The owner sees the status change from ready to not installed; nothing stops working
/// except the ability to drive an elevated window.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeInputRouter : IInputController, IDisposable
{
    private readonly BrokerInputChannel _broker;
    private readonly WindowsInputController _local;
    private readonly NativeInputInstaller _installer;
    private readonly ILogger<NativeInputRouter> _logger;
    private readonly Lock _gate = new();

    /// <summary>
    /// The desktop gate and held-state ownership, applied before any backend.
    /// <para>
    /// Optional so that every existing construction of this class keeps working unchanged; the host
    /// supplies it, and when it is absent the router behaves exactly as it did before. That is not a
    /// loophole in the policy — a router built without it is one nothing routes remote input
    /// through — but it does keep this change from rewriting a dozen test harnesses.
    /// </para>
    /// </summary>
    private readonly CentralInputPolicy? _policy;

    private readonly RemoteInputOwnership? _ownership;

    /// <summary>
    /// The virtual-HID submission path, and the conditions that decide whether it may be used.
    /// Both null until a backend exists on this machine, which is the state today.
    /// </summary>
    private readonly VirtualHidController? _hid;

    private readonly Func<VirtualHidConditions>? _conditions;

    private bool _usingBroker;

    public NativeInputRouter(
        BrokerInputChannel broker,
        WindowsInputController local,
        NativeInputInstaller installer,
        ILogger<NativeInputRouter> logger,
        CentralInputPolicy? policy = null,
        RemoteInputOwnership? ownership = null,
        VirtualHidController? hid = null,
        Func<VirtualHidConditions>? conditions = null)
    {
        _broker = broker;
        _local = local;
        _installer = installer;
        _logger = logger;
        _policy = policy;
        _ownership = ownership;
        _hid = hid;
        _conditions = conditions;
    }

    /// <summary>
    /// Whether virtual HID may carry this kind of input right now.
    /// <para>
    /// Asked per kind, because a backend publishing a mouse and a keyboard and no digitizer is a
    /// realistic install and should carry the two it has. Cached conditions are supplied by the
    /// caller rather than probed here: this runs on the hot path, and rediscovering a driver for
    /// every pointer packet is how routing starts to flap.
    /// </para>
    /// </summary>
    private bool HidEligible(InputKind kind)
    {
        if (_hid is null || _conditions is null)
        {
            return false;
        }

        return VirtualHidEligibility.Evaluate(_conditions(), kind).Eligible;
    }

    /// <summary>
    /// Which backend instance is carrying input right now.
    /// <para>
    /// The generation advances whenever the path changes, so held state recorded against one
    /// instance can never be confused with state belonging to the path that replaced it.
    /// </para>
    /// </summary>
    public BackendInstance CurrentInstance
    {
        get
        {
            lock (_gate)
            {
                return new BackendInstance(
                    _usingBroker ? InputBackend.ElevatedInjection : InputBackend.LocalInjection,
                    _generation);
            }
        }
    }

    private int _generation = 1;

    public (int Left, int Top, int Width, int Height) VirtualDesktop => _local.VirtualDesktop;

    public NativeInputStatus Status
    {
        get
        {
            var info = _broker.Refresh();

            if (info is not null)
            {
                return new NativeInputStatus
                {
                    VirtualHidDiagnostic = DescribeVirtualHid(),
                    Path = InputPath.NativeBroker,
                    State = info.Elevated ? NativeInputState.Ready : NativeInputState.Degraded,
                    ReachesElevatedWindows = info.Elevated,
                    TouchAvailable = info.TouchAvailable,
                    PhysicalDigitizer = info.PhysicalDigitizer,
                    Desktop = info.Desktop,
                    BrokerVersion = info.BrokerVersion,

                    // Said plainly rather than glossed over. A broker running without elevation is
                    // the one state where claiming "native input: ready" would be a lie the owner
                    // could only discover by trying to click something and failing.
                    Diagnostic = info.Elevated
                        ? null
                        : "The Nexus Input Broker is running without administrator rights, so elevated "
                          + "application windows are still out of reach. Repair native input to fix this.",
                };
            }

            var installation = _installer.GetStatus();
            var local = _local.Status;

            return local with
            {
                VirtualHidDiagnostic = DescribeVirtualHid(),
                State = installation.Registered ? NativeInputState.Unavailable : NativeInputState.NotInstalled,
                Diagnostic = installation.Registered
                    ? _broker.Diagnostic
                      ?? "Native input is installed but not responding; PC Core is injecting input itself."
                    : "Native input is not installed. PC Core is injecting input itself, so elevated "
                      + "application windows are out of reach.",
            };
        }
    }

    public InputResult MovePointerAbsolute(int x, int y) =>
        Route(
            InputKind.Mouse,
            // Always unsupported on a relative HID mouse, so this falls straight through. Wired
            // anyway so the shape is uniform and the reason is reported rather than assumed.
            VirtualHidController.MoveAbsolute,
            new BrokerRequest { Op = BrokerProtocol.Ops.MoveAbsolute, X = x, Y = y },
            () => _local.MovePointerAbsolute(x, y));

    public InputResult MovePointerRelative(int dx, int dy) =>
        Route(
            InputKind.Mouse,
            _hid is null ? null : () => _hid.MoveRelative(dx, dy),
            new BrokerRequest { Op = BrokerProtocol.Ops.MoveRelative, Dx = dx, Dy = dy },
            () => _local.MovePointerRelative(dx, dy));

    public InputResult Button(PointerButton button, bool down) =>
        Route(
            InputKind.Mouse,
            _hid is null ? null : () => _hid.Button((int)button, down),
            new BrokerRequest { Op = BrokerProtocol.Ops.Button, Button = (int)button, Down = down },
            () => _local.Button(button, down));

    public InputResult Scroll(int horizontalNotches, int verticalNotches) =>
        Route(
            InputKind.Mouse,
            _hid is null ? null : () => _hid.Scroll(horizontalNotches, verticalNotches),
            new BrokerRequest
            {
                Op = BrokerProtocol.Ops.Scroll,
                Horizontal = horizontalNotches,
                Vertical = verticalNotches,
            },
            () => _local.Scroll(horizontalNotches, verticalNotches));

    public InputResult Key(NexusKey key, bool down)
    {
        var (virtualKey, extended) = WindowsKeyMap.Map(key);

        return Route(
            InputKind.Keyboard,
            // NexusKeyLike mirrors NexusKey position for position, asserted by HidKeyMapMirrorsNexusKey.
            _hid is null ? null : () => _hid.Key((NexusKeyLike)(int)key, down),
            new BrokerRequest
            {
                Op = BrokerProtocol.Ops.Key,
                VirtualKey = virtualKey,
                Extended = extended,
                Down = down,
            },
            () => _local.Key(key, down));
    }

    /// <summary>
    /// Arbitrary text, which never goes through the HID keyboard.
    /// <para>
    /// A boot keyboard sends key usages and there is no usage for an arbitrary character, so text
    /// stays on character injection whatever the mouse and keyboard are doing. Passing null for the
    /// HID attempt is the honest encoding of that: not "it failed", but "this path does not exist".
    /// Turkish text works for exactly this reason.
    /// </para>
    /// </summary>
    public InputResult Text(string text) =>
        Route(
            InputKind.Keyboard,
            hid: null,
            new BrokerRequest { Op = BrokerProtocol.Ops.Text, Text = text },
            () => _local.Text(text));

    public InputResult Touch(int contactId, TouchAction action, int x, int y)
    {
        var desktop = _local.VirtualDesktop;

        var result = Route(
            InputKind.Touch,
            _hid is null || desktop.Width <= 0 || desktop.Height <= 0
                ? null
                : () => _hid.Touch(
                    action switch
                    {
                        TouchAction.Down => HidTouchPhase.Down,
                        TouchAction.Move => HidTouchPhase.Move,
                        TouchAction.Up => HidTouchPhase.Up,
                        _ => HidTouchPhase.Cancel,
                    },
                    (double)(x - desktop.Left) / desktop.Width,
                    (double)(y - desktop.Top) / desktop.Height),
            new BrokerRequest
            {
                Op = BrokerProtocol.Ops.Touch,
                Contact = contactId,
                Phase = action switch
                {
                    TouchAction.Down => BrokerProtocol.Phases.Down,
                    TouchAction.Move => BrokerProtocol.Phases.Move,
                    TouchAction.Up => BrokerProtocol.Phases.Up,
                    _ => BrokerProtocol.Phases.Cancel,
                },
                X = x,
                Y = y,
            },
            () => _local.Touch(contactId, action, x, y));

        // A broker on a machine that cannot inject touch still has a pointer. Falling back through
        // the same broker keeps Direct mode working there, and — importantly — keeps it working
        // against elevated windows, which the local path could not manage either way.
        return result.Delivery == InputDelivery.Unsupported
            ? TouchFallback.Apply(this, contactId, action, x, y)
            : result;
    }

    /// <summary>
    /// Releases held input on both paths.
    /// <para>
    /// Both, unconditionally, and not only the one currently in use. After a switch — a broker that
    /// died mid-drag, or one that appeared mid-session — the button Windows believes is down may
    /// have been pressed by the other one.
    /// </para>
    /// </summary>
    public int ReleaseAllHeld()
    {
        // The HID path first, and through its own device. This method is where phone disconnect,
        // authenticated session loss and explicit release all arrive, so it is the one place that
        // has to get the ordering right: a HID device holding a modifier is not released by either
        // injection path below it.
        var hidReleased = false;

        if (_hid is not null)
        {
            hidReleased = _hid.Neutralize();

            if (!hidReleased)
            {
                _logger.LogWarning(
                    "The Nexus HID backend did not confirm release; held device state is not known to be clear.");
            }
        }

        var released = _local.ReleaseAllHeld();
        var response = _broker.Send(new BrokerRequest { Op = BrokerProtocol.Ops.ReleaseAll });

        // Ownership is only forgotten when every path that could be holding something confirmed it
        // let go. Forgetting on an unconfirmed release is precisely the lie the ownership model
        // exists to refuse.
        if (_ownership is not null && (_hid is null || hidReleased))
        {
            _ownership.Forget();
        }

        lock (_gate)
        {
            _hidKinds.Clear();
        }

        return released + (response?.Released ?? 0);
    }

    private InputResult Route(
        InputKind kind,
        Func<NativeInjectionResult>? hid,
        BrokerRequest request,
        Func<InputResult> local)
    {
        // The central gate, before any backend. Nothing below this line runs while the Secure
        // Desktop is up, whichever path would have carried it.
        if (_policy is not null)
        {
            var decision = _policy.Evaluate(NeutralizeCurrent);

            if (decision != InputPolicyDecision.Allow)
            {
                return CentralInputPolicy.ToResult(decision);
            }
        }

        // Tier one: the Nexus HID device, when it exists and every eligibility condition holds.
        //
        // Unsupported is not a failure here - it is a capability answer, and the honest response is
        // to try the next tier. A HID mouse genuinely cannot position absolutely, and a backend with
        // no digitizer genuinely cannot do touch.
        if (hid is not null && HidEligible(kind))
        {
            var attempt = hid();

            if (attempt.Delivery != NativeDelivery.Unsupported)
            {
                NoteHid(kind);
                return WindowsInputController.Translate(attempt);
            }
        }
        else if (_hid is not null && WasUsingHid())
        {
            // HID was carrying input and has just stopped being eligible - attribution went
            // unhealthy, the driver vanished, the desktop changed, or a capability disappeared.
            // Whatever it was holding is released through the HID device before anything else
            // carries input, because nothing below can release it afterwards.
            var released = _hid.Neutralize();

            lock (_gate)
            {
                _hidKinds.Clear();
            }

            _logger.LogWarning(
                "Virtual HID stopped being eligible; held HID state {Outcome} and input has fallen back.",
                released ? "was neutralized" : "could NOT be confirmed released");

            if (!released)
            {
                _ownership?.NeutralizeForSwitch(_ => false);
            }
        }

        var response = _broker.Send(request);

        if (response is null)
        {
            Note(usingBroker: false);
            return local();
        }

        Note(usingBroker: true);

        var delivery = BrokerProtocol.Deliveries.To(response.Delivery);

        return WindowsInputController.Translate(new NativeInjectionResult(delivery, response.Diagnostic));
    }

    /// <summary>
    /// Releases everything the named instance holds, through that instance and no other.
    /// <para>
    /// Both injection paths ultimately reach the desktop through <c>SendInput</c>, so either can
    /// release what the other pressed — which is why <see cref="ReleaseAllHeld"/> has always simply
    /// released both. That stops being true for a HID backend, whose held state lives in a device
    /// rather than in a message, and this method exists so the distinction has somewhere to live
    /// before that backend arrives.
    /// </para>
    /// </summary>
    private bool NeutralizeCurrent(BackendInstance instance)
    {
        try
        {
            switch (instance.Backend)
            {
                case InputBackend.ElevatedInjection:
                    return _broker.Send(new BrokerRequest { Op = BrokerProtocol.Ops.ReleaseAll }) is not null;

                case InputBackend.LocalInjection:
                    _local.ReleaseAllHeld();
                    return true;

                default:
                    // A HID backend releases through its own device or not at all. Nothing here can
                    // do it on that backend's behalf, and claiming otherwise is the failure this
                    // whole ownership model exists to prevent.
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Neutralizing {Instance} failed; held state is not confirmed released.", instance);
            return false;
        }
    }

    /// <summary>
    /// Per-kind virtual-HID state, in one line, computed from the same conditions the router routes
    /// on rather than from anything that could disagree with them.
    /// <para>
    /// Reports what is <em>eligible</em>, never what exists on disk. A driver package sitting in the
    /// repository is not a capability, and saying so would be the exact dishonesty this whole status
    /// surface was rebuilt to avoid.
    /// </para>
    /// </summary>
    private string DescribeVirtualHid()
    {
        if (_hid is null || _conditions is null)
        {
            return "Virtual HID: NOT_INSTALLED (no backend wired)";
        }

        var conditions = _conditions();

        var mouse = VirtualHidEligibility.Evaluate(conditions, InputKind.Mouse);
        var keyboard = VirtualHidEligibility.Evaluate(conditions, InputKind.Keyboard);
        var touch = VirtualHidEligibility.Evaluate(conditions, InputKind.Touch);

        if (!mouse.Eligible && !keyboard.Eligible && !touch.Eligible)
        {
            return $"Virtual HID: unavailable — {mouse.Describe()}";
        }

        return $"Virtual HID — mouse: {Describe(mouse)}, keyboard: {Describe(keyboard)}, touch: {Describe(touch)}; "
            + "text always uses character injection";

        static string Describe(VirtualHidVerdict verdict) =>
            verdict.Eligible ? "READY" : verdict.Describe();
    }

    private readonly HashSet<InputKind> _hidKinds = [];

    /// <summary>True when any kind was last carried by the HID path.</summary>
    private bool WasUsingHid()
    {
        lock (_gate)
        {
            return _hidKinds.Count > 0;
        }
    }

    /// <summary>Logs a kind moving onto the HID path, once per kind rather than per report.</summary>
    private void NoteHid(InputKind kind)
    {
        lock (_gate)
        {
            if (!_hidKinds.Add(kind))
            {
                return;
            }
        }

        _logger.LogInformation("{Kind} input is now going through the Nexus HID device.", kind);
    }

    /// <summary>Logs the path changing, once, rather than the path in use, constantly.</summary>
    private void Note(bool usingBroker)
    {
        BackendInstance previous;

        lock (_gate)
        {
            if (_usingBroker == usingBroker)
            {
                return;
            }

            previous = new BackendInstance(
                _usingBroker ? InputBackend.ElevatedInjection : InputBackend.LocalInjection,
                _generation);

            _usingBroker = usingBroker;

            // A new generation for the path being switched to. Anything still recorded against the
            // old one belongs to a backend that is no longer carrying input.
            _generation++;
        }

        // Neutralise before the switch takes effect, never after. Releasing through the backend that
        // pressed something is the invariant; doing it in the other order is how a key ends up held
        // by a path nothing talks to any more.
        if (_ownership is not null && !_ownership.Held.IsNeutral)
        {
            var outcome = _ownership.NeutralizeForSwitch(NeutralizeCurrent);

            if (outcome == RouteSwitchResult.Uncertain)
            {
                _logger.LogWarning(
                    "Input path changed while {Instance} held state that could not be confirmed released; "
                    + "further input is paused until that backend returns and accepts a neutral state.",
                    previous);
            }
        }

        if (usingBroker)
        {
            _logger.LogInformation("Input is now going through the Nexus Input Broker.");
        }
        else
        {
            _logger.LogWarning(
                "Input has fallen back to PC Core's own injection; elevated application windows "
                + "are out of reach until the Nexus Input Broker is available again.");
        }
    }

    /// <summary>
    /// Shutdown. Releases through every path, HID first, so PC Core exiting cannot leave a device
    /// holding a key.
    /// </summary>
    public void Dispose()
    {
        // Release before tearing anything down. A process that exits while a HID device holds a
        // modifier leaves Windows believing a key is down, with nothing left running that could
        // clear it.
        try
        {
            ReleaseAllHeld();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Releasing held input during shutdown failed.");
        }

        _broker.Dispose();
    }
}
