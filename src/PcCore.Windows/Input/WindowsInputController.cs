using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Input;
using PcCore.NativeInput;

namespace PcCore.Windows.Input;

/// <summary>
/// Input injected by PC Core itself, in process, as a standard-user application.
/// <para>
/// This is the fallback path, and it is the same code the Nexus Input Broker runs — the only
/// difference is the integrity level of the process running it. Everything works: pointer, buttons,
/// wheel, keyboard, Unicode text, and real Windows touch. The one thing it cannot do is reach a
/// window belonging to an elevated application, because Windows refuses input sent from a lower
/// integrity level to a higher one. That is a protection, not a defect, and Nexus does not disable
/// it; installing the broker moves the injection to a process that is allowed to do it instead.
/// </para>
/// <para>
/// It receives already-validated values. Range checks, rate limits and the closed key set live in
/// <see cref="InputService"/>; the translation from a protocol value to a native call is the only
/// thing happening here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInputController : IInputController
{
    private readonly NativeInjector _injector;
    private readonly IDisplayGeometry _geometry;
    private readonly ILogger<WindowsInputController> _logger;

    public WindowsInputController(IDisplayGeometry geometry, ILogger<WindowsInputController> logger)
    {
        _geometry = geometry;
        _logger = logger;
        _injector = new NativeInjector();

        if (!_injector.TouchAvailable)
        {
            _logger.LogWarning(
                "Windows touch injection is unavailable ({Reason}); Direct mode will use the pointer.",
                _injector.TouchDiagnostic);
        }
    }

    public (int Left, int Top, int Width, int Height) VirtualDesktop
    {
        get
        {
            var bounds = _geometry.VirtualBounds;
            return (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }

    /// <summary>
    /// Where the pointer is now, in virtual-desktop pixels.
    /// <para>
    /// Exposed for diagnostics rather than for the protocol: nothing remote needs to ask, and the
    /// self-test needs it to prove that an injected move actually moved something.
    /// </para>
    /// </summary>
    public static bool TryGetPointerPosition(out int x, out int y) =>
        NativeInjector.TryGetCursorPosition(out x, out y);

    public NativeInputStatus Status => new()
    {
        Path = InputPath.Local,
        State = NativeInputState.NotInstalled,
        ReachesElevatedWindows = false,
        TouchAvailable = _injector.TouchAvailable,
        PhysicalDigitizer = NativeInjector.HasPhysicalDigitizer,
        Desktop = DescribeDesktop(),
        Diagnostic = "PC Core is injecting input itself, so elevated application windows are out of reach.",
    };

    public InputResult MovePointerAbsolute(int x, int y) => Translate(_injector.MoveAbsolute(x, y));

    public InputResult MovePointerRelative(int dx, int dy) => Translate(_injector.MoveRelative(dx, dy));

    public InputResult Button(PointerButton button, bool down) =>
        Translate(_injector.Button((int)button, down));

    public InputResult Scroll(int horizontalNotches, int verticalNotches) =>
        Translate(_injector.Scroll(horizontalNotches, verticalNotches));

    public InputResult Key(NexusKey key, bool down)
    {
        var (virtualKey, extended) = WindowsKeyMap.Map(key);
        return Translate(_injector.Key(virtualKey, extended, down));
    }

    public InputResult Text(string text) => Translate(_injector.Text(text));

    public InputResult Touch(int contactId, TouchAction action, int x, int y)
    {
        if (!_injector.TouchAvailable)
        {
            return TouchFallback.Apply(this, contactId, action, x, y);
        }

        var result = Translate(_injector.Touch((uint)contactId, Phase(action), x, y));

        // A machine that reports touch injection as available can still refuse it — a policy, a
        // session type, a driver in a bad state. Falling back rather than failing is what keeps
        // Direct mode working on a machine where touch turns out not to.
        return result.Delivery == InputDelivery.Unsupported
            ? TouchFallback.Apply(this, contactId, action, x, y)
            : result;
    }

    public int ReleaseAllHeld() => _injector.ReleaseAll();

    internal static TouchPhase Phase(TouchAction action) => action switch
    {
        TouchAction.Down => TouchPhase.Down,
        TouchAction.Move => TouchPhase.Move,
        TouchAction.Up => TouchPhase.Up,
        _ => TouchPhase.Cancel,
    };

    internal static string DescribeDesktop() => InputDesktop.Current().Kind switch
    {
        InputDesktopKind.Default => "DEFAULT",
        InputDesktopKind.Secure => "SECURE",
        InputDesktopKind.Other => "OTHER",
        _ => "UNKNOWN",
    };

    /// <summary>Maps the native layer's result onto the capability layer's, one member for one member.</summary>
    internal static InputResult Translate(NativeInjectionResult result) => result.Delivery switch
    {
        NativeDelivery.Delivered => InputResult.Ok,
        NativeDelivery.BlockedByIntegrity => InputResult.Blocked(result.Diagnostic ?? "Windows blocked this input."),
        NativeDelivery.BlockedBySecureDesktop => InputResult.SecureDesktop(
            result.Diagnostic ?? "Local administrator confirmation is required on the PC."),
        NativeDelivery.Unsupported => InputResult.Unsupported(result.Diagnostic ?? "unsupported on this system"),
        _ => InputResult.Failed(result.Diagnostic ?? "Windows rejected the input."),
    };
}

/// <summary>
/// Turns a touch contact into pointer and button events, for a machine that cannot inject touch.
/// <para>
/// <b>Why the fallback lives on the PC and not the phone.</b> Direct mode sends touch because that
/// is what Direct mode <em>is</em> — a finger on a picture of the desktop. Whether Windows can be
/// told that directly is a property of the PC, and asking the phone to find out and then send a
/// different kind of message would put a second gesture implementation on the far side of the
/// network from the thing that knows the answer.
/// </para>
/// <para>
/// The translation is deliberately the pre-touch behaviour, exactly: move, then press; move while
/// down; release. Notably a plain move never presses anything — the bug that once made every swipe
/// in Direct mode drag a selection box across the desktop lived in precisely this translation, and
/// it does not get to come back through the back door.
/// </para>
/// </summary>
internal static class TouchFallback
{
    public static InputResult Apply(IInputController controller, int contactId, TouchAction action, int x, int y)
    {
        // Only the first contact can be expressed as a mouse. A second finger has no pointer to be,
        // and inventing one would move the cursor somewhere the owner did not touch.
        if (contactId != 0)
        {
            return InputResult.Ok;
        }

        switch (action)
        {
            case TouchAction.Down:
                var moved = controller.MovePointerAbsolute(x, y);
                return moved.Succeeded ? controller.Button(PointerButton.Left, down: true) : moved;

            case TouchAction.Move:
                return controller.MovePointerAbsolute(x, y);

            default:
                return controller.Button(PointerButton.Left, down: false);
        }
    }
}
