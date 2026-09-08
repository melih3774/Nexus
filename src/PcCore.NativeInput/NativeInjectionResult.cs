namespace PcCore.NativeInput;

/// <summary>Why an injected event did or did not reach the desktop.</summary>
public enum NativeDelivery
{
    Delivered,

    /// <summary>
    /// Windows refused the event because the foreground window belongs to a process at a higher
    /// integrity level. This is what happens when an unelevated process aims input at an
    /// Administrator window, and it is precisely the case the Nexus Input Broker exists to fix.
    /// </summary>
    BlockedByIntegrity,

    /// <summary>
    /// The Secure Desktop is up — a UAC consent prompt, the lock screen, or Ctrl+Alt+Del.
    /// <para>
    /// Nexus stops here by policy, not because it failed. The owner has to answer that prompt at
    /// the PC.
    /// </para>
    /// </summary>
    BlockedBySecureDesktop,

    /// <summary>The capability is not available on this system. Touch injection, typically.</summary>
    Unsupported,

    /// <summary>The call failed for some other reason. The diagnostic says what Windows reported.</summary>
    Failed,
}

public readonly record struct NativeInjectionResult(NativeDelivery Delivery, string? Diagnostic)
{
    public static readonly NativeInjectionResult Ok = new(NativeDelivery.Delivered, null);

    public static NativeInjectionResult Blocked(string diagnostic) =>
        new(NativeDelivery.BlockedByIntegrity, diagnostic);

    public static NativeInjectionResult SecureDesktop(string diagnostic) =>
        new(NativeDelivery.BlockedBySecureDesktop, diagnostic);

    public static NativeInjectionResult Unsupported(string diagnostic) =>
        new(NativeDelivery.Unsupported, diagnostic);

    public static NativeInjectionResult Failed(string diagnostic) => new(NativeDelivery.Failed, diagnostic);

    public bool Succeeded => Delivery == NativeDelivery.Delivered;
}

/// <summary>What one touch contact is being told to do.</summary>
public enum TouchPhase
{
    Down,
    Move,
    Up,

    /// <summary>Abandon the contact without completing it. Used on cleanup paths.</summary>
    Cancel,
}
