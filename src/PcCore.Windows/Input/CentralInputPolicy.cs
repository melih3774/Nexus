using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Input;
using PcCore.NativeInput;

namespace PcCore.Windows.Input;

/// <summary>What the policy decided about one attempted input.</summary>
public enum InputPolicyDecision
{
    /// <summary>May proceed to a backend.</summary>
    Allow,

    /// <summary>
    /// The input desktop is not the ordinary one and the route that would carry this input cannot
    /// reach it. Nothing is submitted.
    /// </summary>
    BlockedBySecureDesktop,

    /// <summary>
    /// A previous backend held state that was never confirmed released, so nothing new may be
    /// routed until a fresh backend instance accepts a neutral report.
    /// </summary>
    BlockedByUncertainState,
}

/// <summary>
/// The one gate every remote input passes before any backend sees it.
/// <para>
/// <b>Why centralised rather than per backend.</b> Until v0.9 the desktop check lived in the elevated
/// broker, and only there. That was defensible when the broker was the only path that could reach
/// anywhere interesting — but it means the check is a property of one implementation rather than a
/// property of Nexus, and a third backend arriving would silently not have it. Virtual HID is that
/// third backend, and it is the one closest to hardware, so it is the last place the gate should be
/// optional. The broker keeps its own check as defence in depth.
/// </para>
/// <para>
/// <b>What changed in v1.0, and why it is not a weakening.</b> The rule used to be "the Secure
/// Desktop is a hard stop for everything". That was the right rule while the only paths were
/// <c>SendInput</c> and an elevated broker doing <c>SendInput</c> — neither can reach the Winlogon
/// desktop, so anything they sent would vanish and the honest answer was to refuse. It stopped being
/// the right rule when input started coming from a HID device: the owner's own keyboard drives a UAC
/// prompt, and so does theirs, for the same reason and by the same mechanism. Refusing there was not
/// protecting the machine from anything — the report is indistinguishable from the desk keyboard —
/// it was only stopping the owner from answering their own prompt.
/// </para>
/// <para>
/// The rule is now about the route rather than the desktop: <b>a device-backed route may carry input
/// anywhere; an injection route may not leave the ordinary desktop.</b> Who is allowed to ask at all
/// is a separate question, answered earlier and elsewhere — the capability policy refuses every
/// input action to a phone whose owner has not authenticated. Nothing here can be reached by an
/// unauthorised caller, so nothing here needs to re-ask.
/// </para>
/// <para>
/// <b>Held state is watched across transitions, not only at the moment of a call.</b> Checking the
/// desktop when input arrives is not enough: the interesting case is a modifier held through a
/// backend when the desktop changes, where nothing arrives at all and the held state simply stays
/// held. So the policy is asked on every input, and the transition from allowed to blocked is what
/// triggers neutralisation — see <see cref="Evaluate"/>. A device-backed route has no such
/// transition, and must not: releasing a held key because a UAC prompt appeared would be Nexus
/// inventing a key-up the owner did not perform.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CentralInputPolicy
{
    private readonly RemoteInputOwnership _ownership;
    private readonly ILogger<CentralInputPolicy> _logger;
    private readonly Func<InputDesktopState> _desktop;
    private readonly Lock _gate = new();

    private bool _lastAllowed = true;
    private InputDesktopKind _lastKind = InputDesktopKind.Default;

    public CentralInputPolicy(
        RemoteInputOwnership ownership,
        ILogger<CentralInputPolicy> logger,
        Func<InputDesktopState>? desktop = null)
    {
        _ownership = ownership;
        _logger = logger;
        _desktop = desktop ?? InputDesktop.Current;
    }

    /// <summary>The desktop as the policy last saw it. Diagnostics.</summary>
    public string CurrentDesktop { get; private set; } = "UNKNOWN";

    /// <summary>
    /// Looks at the input desktop now and returns what it is.
    /// <para>
    /// <b>Reading <see cref="CurrentDesktop"/> instead was a real defect.</b> That field is only
    /// written inside <see cref="Evaluate"/>, which runs when input is actually attempted — so
    /// before the first event it read "UNKNOWN", the eligibility rules concluded the desktop did not
    /// permit remote input, and <c>input.status</c> reported local injection on a machine whose
    /// virtual HID devices were published and ready. The state was correct and the question was
    /// asked of the wrong thing.
    /// </para>
    /// <para>
    /// One <c>OpenInputDesktop</c> per call. It is on the input path, and it is a handle open and a
    /// close — cheap next to the injection it gates.
    /// </para>
    /// </summary>
    public string PeekDesktop()
    {
        CurrentDesktop = Describe(_desktop().Kind);
        return CurrentDesktop;
    }

    private static string Describe(InputDesktopKind kind) => kind switch
    {
        InputDesktopKind.Default => "DEFAULT",
        InputDesktopKind.Secure => "SECURE",
        InputDesktopKind.Other => "OTHER",
        _ => "UNKNOWN",
    };

    /// <summary>True when held remote state exists whose release was never confirmed.</summary>
    public bool NeutralizationUncertain => _ownership.IsUncertain;

    /// <summary>
    /// Decides whether input may proceed, and neutralises held state when an injection route has
    /// just stopped being able to reach the live desktop.
    /// </summary>
    /// <param name="deviceBackedRoute">
    /// True when this input will be carried by a HID device rather than injected. A device reaches
    /// every desktop, including Winlogon, because Windows does not distinguish it from the hardware
    /// on the owner's desk.
    /// </param>
    /// <param name="neutralize">
    /// How the caller releases whatever the owning backend holds; it returns whether the backend
    /// accepted the neutral report. Invoked only on the allowed-to-blocked edge, not on every
    /// blocked call, so a UAC prompt left up for a minute does not produce a neutral report per
    /// pointer packet.
    /// </param>
    public InputPolicyDecision Evaluate(bool deviceBackedRoute, Func<BackendInstance, bool> neutralize)
    {
        ArgumentNullException.ThrowIfNull(neutralize);

        var desktop = _desktop();
        CurrentDesktop = Describe(desktop.Kind);

        // Unknown is treated as not-Default. A desktop that cannot be identified is one an injection
        // path has no business sending into, and failing open would mean the one case where the
        // check is most likely to matter is the one case it does not apply.
        var allowed = desktop.IsInteractive || deviceBackedRoute;

        bool wasAllowed;
        bool changed;

        lock (_gate)
        {
            wasAllowed = _lastAllowed;
            changed = _lastKind != desktop.Kind;
            _lastAllowed = allowed;
            _lastKind = desktop.Kind;
        }

        if (changed)
        {
            _logger.LogInformation(
                "Input desktop is now {Desktop}; remote input {Outcome}.",
                CurrentDesktop,
                allowed
                    ? deviceBackedRoute && !desktop.IsInteractive
                        ? "continues over the Nexus HID device"
                        : "continues"
                    : "is paused until it returns");
        }

        if (!allowed)
        {
            if (wasAllowed)
            {
                // The edge. Whatever is held belongs to a backend that is about to stop receiving
                // input, so it is released now rather than left held behind a consent prompt.
                var result = _ownership.NeutralizeForSwitch(neutralize);

                _logger.LogInformation(
                    "Held state from the injection path {Outcome}.",
                    result switch
                    {
                        RouteSwitchResult.Clean => "was already neutral",
                        RouteSwitchResult.Neutralized => "was neutralized",
                        _ => "could NOT be confirmed released",
                    });
            }

            return InputPolicyDecision.BlockedBySecureDesktop;
        }

        return _ownership.IsUncertain
            ? InputPolicyDecision.BlockedByUncertainState
            : InputPolicyDecision.Allow;
    }

    /// <summary>Turns a policy decision into the protocol result the caller returns.</summary>
    public static InputResult ToResult(InputPolicyDecision decision) => decision switch
    {
        InputPolicyDecision.BlockedBySecureDesktop => InputResult.SecureDesktop(
            "Windows is showing a secure screen, and this PC has no Nexus HID device able to reach it. "
            + "Answer the prompt at the PC."),
        InputPolicyDecision.BlockedByUncertainState => InputResult.Failed(
            "Held remote input from a previous backend has not been confirmed released; input is paused "
            + "until the backend returns and accepts a neutral state."),
        _ => InputResult.Ok,
    };
}
