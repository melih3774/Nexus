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

    /// <summary>The input desktop is not the ordinary one. Nothing is submitted.</summary>
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
/// <b>Why centralised rather than per backend.</b> Until now the desktop check lived in the elevated
/// broker, and only there. That was defensible when the broker was the only path that could reach
/// anywhere interesting — but it means the check is a property of one implementation rather than a
/// property of Nexus, and a third backend arriving would silently not have it. Virtual HID is that
/// third backend, and it is the one closest to hardware, so it is the last place the gate should be
/// optional. The broker keeps its own check as defence in depth.
/// </para>
/// <para>
/// <b>Held state is watched across transitions, not only at the moment of a call.</b> Checking the
/// desktop when input arrives is not enough: the interesting case is a modifier held through a
/// backend when the Secure Desktop appears, where nothing arrives at all and the held state simply
/// stays held. So the policy is asked on every input, and the transition from allowed to blocked is
/// what triggers neutralisation — see <see cref="Evaluate"/>.
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
    /// Decides whether input may proceed, and neutralises held state when the desktop has just
    /// stopped allowing it.
    /// <para>
    /// <paramref name="neutralize"/> is how the caller releases whatever the owning backend holds;
    /// it returns whether the backend accepted the neutral report. It is invoked only on the
    /// allowed-to-blocked edge, not on every blocked call, so a UAC prompt left up for a minute does
    /// not produce a neutral report per pointer packet.
    /// </para>
    /// </summary>
    public InputPolicyDecision Evaluate(Func<BackendInstance, bool> neutralize)
    {
        ArgumentNullException.ThrowIfNull(neutralize);

        var desktop = _desktop();
        CurrentDesktop = Describe(desktop.Kind);

        // Unknown is treated as not-Default. A desktop that cannot be identified is one Nexus has no
        // business injecting into, and failing open here would mean the one case where the check is
        // most likely to matter is the one case it does not apply.
        var allowed = desktop.IsInteractive;

        bool wasAllowed;

        lock (_gate)
        {
            wasAllowed = _lastAllowed;
            _lastAllowed = allowed;
        }

        if (!allowed)
        {
            if (wasAllowed)
            {
                // The edge. Whatever is held belongs to a backend that is about to stop receiving
                // input, so it is released now rather than left held behind a consent prompt.
                var result = _ownership.NeutralizeForSwitch(neutralize);

                _logger.LogInformation(
                    "Secure Desktop entered ({Desktop}); remote input blocked and held state {Outcome}.",
                    CurrentDesktop,
                    result switch
                    {
                        RouteSwitchResult.Clean => "was already neutral",
                        RouteSwitchResult.Neutralized => "neutralized",
                        _ => "could NOT be confirmed released",
                    });
            }

            return InputPolicyDecision.BlockedBySecureDesktop;
        }

        if (!wasAllowed)
        {
            // Returning to the ordinary desktop does not restore anything. Whatever was held was
            // released on the way out, and the owner has to press it again.
            _logger.LogInformation("Input desktop is {Desktop} again; remote input may resume.", CurrentDesktop);
        }

        return _ownership.IsUncertain
            ? InputPolicyDecision.BlockedByUncertainState
            : InputPolicyDecision.Allow;
    }

    /// <summary>Turns a policy decision into the protocol result the caller returns.</summary>
    public static InputResult ToResult(InputPolicyDecision decision) => decision switch
    {
        InputPolicyDecision.BlockedBySecureDesktop => InputResult.SecureDesktop(
            "Windows is showing a secure screen. Nexus does not send input there; answer the prompt at the PC."),
        InputPolicyDecision.BlockedByUncertainState => InputResult.Failed(
            "Held remote input from a previous backend has not been confirmed released; input is paused "
            + "until the backend returns and accepts a neutral state."),
        _ => InputResult.Ok,
    };
}
