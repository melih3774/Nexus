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

    public string? BrokerVersion { get; init; }

    /// <summary>Short, safe to show. Why the native path is not in use, when it is not.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>A one-line summary, which is all the phone shows.</summary>
    public string Summary => (Path, State) switch
    {
        (InputPath.NativeBroker, NativeInputState.Ready) => "Native input: ready",
        (InputPath.NativeBroker, NativeInputState.Degraded) => "Native input: not elevated",
        (_, NativeInputState.NotInstalled) => "Native input: not installed",
        (_, NativeInputState.Unavailable) => "Native input: unavailable",
        _ => "Standard input",
    };
}
