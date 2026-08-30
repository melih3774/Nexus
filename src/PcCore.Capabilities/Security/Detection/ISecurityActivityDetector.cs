using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Security.Detection;

/// <summary>
/// What kind of local activity happened. Semantic, never raw input.
/// <para>
/// This enum is the entire vocabulary the security subsystem has for describing what someone did at
/// the machine. There is deliberately no member that could carry <em>what</em> was typed or clicked
/// — the question a security incident needs answered is whether a person was there, and anything
/// finer than that would be surveillance of the owner by their own tool.
/// </para>
/// </summary>
public enum ActivityKind
{
    /// <summary>The mouse moved meaningfully, or a button was pressed.</summary>
    [JsonStringEnumMemberName("POINTER_ACTIVITY")]
    PointerActivity,

    /// <summary>
    /// A key was pressed. Which key is never read — see <see cref="LocalActivity"/>.
    /// </summary>
    [JsonStringEnumMemberName("KEYBOARD_ACTIVITY")]
    KeyboardActivity,

    /// <summary>The workstation was unlocked.</summary>
    [JsonStringEnumMemberName("SESSION_UNLOCKED")]
    SessionUnlocked,

    /// <summary>Someone signed in interactively.</summary>
    [JsonStringEnumMemberName("INTERACTIVE_LOGON")]
    InteractiveLogon,

    /// <summary>The foreground window changed. Carries the application name, never window content.</summary>
    [JsonStringEnumMemberName("FOREGROUND_WINDOW_CHANGED")]
    ForegroundWindowChanged,
}

/// <summary>
/// Where an input event came from.
/// <para>
/// The distinction this milestone turns on. Nexus injects input on the owner's behalf, and if that
/// input were read back as somebody sitting at the machine, using the phone during an incident would
/// keep the incident alive forever — and arming the PC then using it remotely would raise an alarm
/// about yourself.
/// </para>
/// </summary>
public enum ActivitySource
{
    /// <summary>A person at the machine. The only kind that can start a security session.</summary>
    Local,

    /// <summary>Injected by Nexus itself, identified by its own signature. Never counted.</summary>
    NexusRemote,

    /// <summary>Injected by some other program. Not a person at the keyboard, so not counted.</summary>
    Synthetic,
}

/// <summary>
/// One thing that happened at the machine.
/// <para>
/// Note what this record does not have: no key code, no scan code, no character, no click position,
/// no window title. <see cref="Detail"/> exists only for the application name behind a foreground
/// change, and nothing writes anything else into it. A type with nowhere to put a keystroke cannot
/// accidentally grow a keylogger.
/// </para>
/// </summary>
public sealed record LocalActivity(
    ActivityKind Kind,
    ActivitySource Source,
    DateTimeOffset AtUtc,
    string? Detail = null)
{
    public bool IsLocalHuman => Source == ActivitySource.Local;
}

/// <summary>
/// Watches for a person using the machine.
/// <para>
/// Always running, but only ever <em>reporting</em>. Deciding whether an activity matters, and
/// whether anything should happen because of it, belongs to <see cref="SecurityActivityMonitor"/> —
/// which does nothing at all unless the owner has armed the PC.
/// </para>
/// </summary>
public interface ISecurityActivityDetector : IDisposable
{
    /// <summary>Raised for every observed activity, including ones attributed to Nexus.</summary>
    event Action<LocalActivity>? Observed;

    /// <summary>Begins watching. Idempotent.</summary>
    void Start();

    /// <summary>Whether the platform hooks are actually in place.</summary>
    bool IsRunning { get; }

    /// <summary>Why detection is unavailable, when it is. Safe to log and show.</summary>
    string? Diagnostic { get; }
}

/// <summary>A detector that never observes anything. Used on hosts without the Windows layer.</summary>
public sealed class NullSecurityActivityDetector : ISecurityActivityDetector
{
    public event Action<LocalActivity>? Observed;

    public bool IsRunning => false;

    public string? Diagnostic => "activity detection is not available on this host";

    public void Start()
    {
    }

    /// <summary>Lets a test drive the monitor without a desktop.</summary>
    public void Emit(LocalActivity activity) => Observed?.Invoke(activity);

    public void Dispose()
    {
    }
}
