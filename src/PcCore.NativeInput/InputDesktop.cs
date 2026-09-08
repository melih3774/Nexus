using System.Runtime.Versioning;
using System.Text;

namespace PcCore.NativeInput;

/// <summary>Which desktop Windows is currently sending real input to.</summary>
public enum InputDesktopKind
{
    /// <summary>The ordinary interactive desktop. The only one Nexus will ever inject into.</summary>
    Default,

    /// <summary>
    /// The Secure Desktop: a UAC consent prompt, the lock screen, or Ctrl+Alt+Del.
    /// <para>
    /// Windows isolates this desktop from everything running in the interactive session, and that
    /// isolation is the last thing standing between a remote-control tool and full control of the
    /// machine. Nexus treats it as a hard stop.
    /// </para>
    /// </summary>
    Secure,

    /// <summary>A screen saver or another named desktop. Not somewhere to inject either.</summary>
    Other,

    /// <summary>The desktop could not be identified. Treated as "not the default", so input stops.</summary>
    Unknown,
}

public readonly record struct InputDesktopState(InputDesktopKind Kind, string? Name)
{
    /// <summary>True only for the ordinary interactive desktop.</summary>
    public bool IsInteractive => Kind == InputDesktopKind.Default;
}

/// <summary>
/// Reports which desktop is receiving input, so Nexus can stand down when the Secure Desktop is up.
/// <para>
/// <b>This is a boundary, not an obstacle.</b> Nexus does not switch to the Secure Desktop, does not
/// open it, does not automate <c>consent.exe</c>, and does not change any UAC policy. It asks one
/// question — "is the ordinary desktop still the one receiving input?" — and stops injecting when
/// the answer is no. That the elevated input path could technically produce input while a consent
/// prompt is up is exactly why this check exists and is enforced in the broker itself, not only in
/// the caller.
/// </para>
/// <para>
/// The detection is the documented one: <c>OpenInputDesktop</c> then <c>GetUserObjectInformation</c>
/// for the name. When the Secure Desktop is active, a process in the interactive session either
/// cannot open the input desktop at all — which is itself the answer — or opens it and reads back
/// "Winlogon".
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class InputDesktop
{
    private const string DefaultDesktop = "Default";
    private const string WinlogonDesktop = "Winlogon";

    public static InputDesktopState Current()
    {
        var desktop = NativeInputInterop.OpenInputDesktop(0, false, NativeInputInterop.DesktopReadObjects);

        if (desktop == nint.Zero)
        {
            // Access denied here is the ordinary signature of the Secure Desktop being up: the
            // interactive session cannot open Winlogon's desktop. Anything else that stops us
            // identifying the desktop is treated the same way, because the safe answer to "I do not
            // know which desktop is live" is to send nothing.
            return new InputDesktopState(InputDesktopKind.Secure, null);
        }

        try
        {
            var buffer = new byte[256];

            if (!NativeInputInterop.GetUserObjectInformation(
                    desktop, NativeInputInterop.UoiName, buffer, (uint)buffer.Length, out var needed))
            {
                return new InputDesktopState(InputDesktopKind.Unknown, null);
            }

            var length = (int)Math.Min(needed, (uint)buffer.Length);
            var name = Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');

            var kind = name switch
            {
                DefaultDesktop => InputDesktopKind.Default,
                WinlogonDesktop => InputDesktopKind.Secure,
                _ => InputDesktopKind.Other,
            };

            return new InputDesktopState(kind, name);
        }
        finally
        {
            NativeInputInterop.CloseDesktop(desktop);
        }
    }
}
