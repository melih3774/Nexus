using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Input;

public enum PointerButton
{
    [JsonStringEnumMemberName("LEFT")]
    Left,

    [JsonStringEnumMemberName("RIGHT")]
    Right,

    [JsonStringEnumMemberName("MIDDLE")]
    Middle,
}

public enum ButtonAction
{
    [JsonStringEnumMemberName("DOWN")]
    Down,

    [JsonStringEnumMemberName("UP")]
    Up,

    /// <summary>Down then up, so a tap is one message rather than two that can be separated by a drop.</summary>
    [JsonStringEnumMemberName("CLICK")]
    Click,

    [JsonStringEnumMemberName("DOUBLE_CLICK")]
    DoubleClick,
}

public enum KeyAction
{
    [JsonStringEnumMemberName("DOWN")]
    Down,

    [JsonStringEnumMemberName("UP")]
    Up,

    /// <summary>Down then up.</summary>
    [JsonStringEnumMemberName("PRESS")]
    Press,
}

/// <summary>
/// The keys a remote caller may name.
/// <para>
/// A closed enum rather than a raw virtual-key code, because a number straight off the network into
/// a Win32 structure is exactly the shape of input validation that gets skipped. Everything here has
/// a deliberate mapping in the Windows layer, and anything not here cannot be pressed at all.
/// </para>
/// <para>
/// Ordinary characters are not in this list on purpose — they go through <c>input.text</c>, which
/// handles Unicode properly. These are the keys that text cannot express.
/// </para>
/// </summary>
public enum NexusKey
{
    [JsonStringEnumMemberName("ESCAPE")] Escape,
    [JsonStringEnumMemberName("TAB")] Tab,
    [JsonStringEnumMemberName("ENTER")] Enter,
    [JsonStringEnumMemberName("BACKSPACE")] Backspace,
    [JsonStringEnumMemberName("DELETE")] Delete,
    [JsonStringEnumMemberName("INSERT")] Insert,
    [JsonStringEnumMemberName("SPACE")] Space,

    [JsonStringEnumMemberName("HOME")] Home,
    [JsonStringEnumMemberName("END")] End,
    [JsonStringEnumMemberName("PAGE_UP")] PageUp,
    [JsonStringEnumMemberName("PAGE_DOWN")] PageDown,

    [JsonStringEnumMemberName("ARROW_LEFT")] ArrowLeft,
    [JsonStringEnumMemberName("ARROW_UP")] ArrowUp,
    [JsonStringEnumMemberName("ARROW_RIGHT")] ArrowRight,
    [JsonStringEnumMemberName("ARROW_DOWN")] ArrowDown,

    [JsonStringEnumMemberName("CONTROL")] Control,
    [JsonStringEnumMemberName("ALT")] Alt,
    [JsonStringEnumMemberName("SHIFT")] Shift,
    [JsonStringEnumMemberName("META")] Meta,

    [JsonStringEnumMemberName("F1")] F1,
    [JsonStringEnumMemberName("F2")] F2,
    [JsonStringEnumMemberName("F3")] F3,
    [JsonStringEnumMemberName("F4")] F4,
    [JsonStringEnumMemberName("F5")] F5,
    [JsonStringEnumMemberName("F6")] F6,
    [JsonStringEnumMemberName("F7")] F7,
    [JsonStringEnumMemberName("F8")] F8,
    [JsonStringEnumMemberName("F9")] F9,
    [JsonStringEnumMemberName("F10")] F10,
    [JsonStringEnumMemberName("F11")] F11,
    [JsonStringEnumMemberName("F12")] F12,

    // Letters and digits exist only so combinations like Ctrl+C, Ctrl+V and Win+R can be sent as
    // key events. Typing goes through input.text.
    [JsonStringEnumMemberName("A")] A,
    [JsonStringEnumMemberName("B")] B,
    [JsonStringEnumMemberName("C")] C,
    [JsonStringEnumMemberName("D")] D,
    [JsonStringEnumMemberName("E")] E,
    [JsonStringEnumMemberName("F")] F,
    [JsonStringEnumMemberName("G")] G,
    [JsonStringEnumMemberName("H")] H,
    [JsonStringEnumMemberName("I")] I,
    [JsonStringEnumMemberName("J")] J,
    [JsonStringEnumMemberName("K")] K,
    [JsonStringEnumMemberName("L")] L,
    [JsonStringEnumMemberName("M")] M,
    [JsonStringEnumMemberName("N")] N,
    [JsonStringEnumMemberName("O")] O,
    [JsonStringEnumMemberName("P")] P,
    [JsonStringEnumMemberName("Q")] Q,
    [JsonStringEnumMemberName("R")] R,
    [JsonStringEnumMemberName("S")] S,
    [JsonStringEnumMemberName("T")] T,
    [JsonStringEnumMemberName("U")] U,
    [JsonStringEnumMemberName("V")] V,
    [JsonStringEnumMemberName("W")] W,
    [JsonStringEnumMemberName("X")] X,
    [JsonStringEnumMemberName("Y")] Y,
    [JsonStringEnumMemberName("Z")] Z,

    [JsonStringEnumMemberName("DIGIT_0")] Digit0,
    [JsonStringEnumMemberName("DIGIT_1")] Digit1,
    [JsonStringEnumMemberName("DIGIT_2")] Digit2,
    [JsonStringEnumMemberName("DIGIT_3")] Digit3,
    [JsonStringEnumMemberName("DIGIT_4")] Digit4,
    [JsonStringEnumMemberName("DIGIT_5")] Digit5,
    [JsonStringEnumMemberName("DIGIT_6")] Digit6,
    [JsonStringEnumMemberName("DIGIT_7")] Digit7,
    [JsonStringEnumMemberName("DIGIT_8")] Digit8,
    [JsonStringEnumMemberName("DIGIT_9")] Digit9,
}

/// <summary>Why an injected event did not reach its target, when Windows says so.</summary>
public enum InputDelivery
{
    Delivered,

    /// <summary>
    /// Windows refused the event because the foreground window belongs to a process at a higher
    /// integrity level. With the Nexus Input Broker installed this should not happen for an ordinary
    /// elevated application; without it, it is the expected outcome and always was.
    /// </summary>
    BlockedByIntegrity,

    /// <summary>
    /// Windows is showing the Secure Desktop — a UAC consent prompt, the lock screen, or
    /// Ctrl+Alt+Del — and Nexus does not send input there.
    /// <para>
    /// This is a refusal by policy, not a failure. The boundary exists to be respected: whatever
    /// the input path is technically capable of, remote approval of an elevation prompt is not
    /// something Nexus offers. The owner answers that prompt at the PC.
    /// </para>
    /// </summary>
    BlockedBySecureDesktop,

    /// <summary>The action is not available on this system. Touch, on a path that cannot inject it.</summary>
    Unsupported,

    /// <summary>The call failed for some other reason. The diagnostic says what Windows reported.</summary>
    Failed,
}

public readonly record struct InputResult(InputDelivery Delivery, string? Diagnostic)
{
    public static readonly InputResult Ok = new(InputDelivery.Delivered, null);

    public static InputResult Blocked(string diagnostic) => new(InputDelivery.BlockedByIntegrity, diagnostic);

    public static InputResult SecureDesktop(string diagnostic) =>
        new(InputDelivery.BlockedBySecureDesktop, diagnostic);

    public static InputResult Unsupported(string diagnostic) => new(InputDelivery.Unsupported, diagnostic);

    public static InputResult Failed(string diagnostic) => new(InputDelivery.Failed, diagnostic);

    public bool Succeeded => Delivery == InputDelivery.Delivered;
}

/// <summary>What one touch contact is being told to do. Mirrors the phone's own touch events.</summary>
public enum TouchAction
{
    [JsonStringEnumMemberName("DOWN")]
    Down,

    [JsonStringEnumMemberName("MOVE")]
    Move,

    [JsonStringEnumMemberName("UP")]
    Up,

    /// <summary>Abandon the contact without completing it. For an interrupted gesture.</summary>
    [JsonStringEnumMemberName("CANCEL")]
    Cancel,
}

/// <summary>
/// Injects pointer and keyboard events into Windows.
/// <para>
/// Everything here is already validated: coordinates are in-range virtual-desktop pixels, keys are
/// from a closed set, magnitudes are bounded. The implementation's job is to translate, not to
/// second-guess — which is what keeps the P/Invoke out of the transport and the policy out of the
/// P/Invoke.
/// </para>
/// </summary>
public interface IInputController
{
    /// <summary>The full virtual desktop rectangle in pixels, used to normalise absolute moves.</summary>
    (int Left, int Top, int Width, int Height) VirtualDesktop { get; }

    /// <summary>Moves the pointer to a point in virtual-desktop pixels.</summary>
    InputResult MovePointerAbsolute(int x, int y);

    /// <summary>Moves the pointer by a delta in pixels, as a trackpad does.</summary>
    InputResult MovePointerRelative(int dx, int dy);

    InputResult Button(PointerButton button, bool down);

    /// <summary>Scrolls, in wheel notches. Positive vertical scrolls up, matching Windows.</summary>
    InputResult Scroll(int horizontalNotches, int verticalNotches);

    InputResult Key(NexusKey key, bool down);

    /// <summary>Types text as Unicode, so anything the phone's keyboard can produce arrives intact.</summary>
    InputResult Text(string text);

    /// <summary>
    /// Drives one touch contact at a point in virtual-desktop pixels.
    /// <para>
    /// Real Windows touch where the platform can inject it, which is what makes Direct mode behave
    /// like a touch display rather than a mouse being dragged around: a tap is a tap, a press and
    /// hold is a right-click without Nexus having to decide that, and applications that handle touch
    /// get touch. Where touch cannot be injected, the implementation is expected to fall back to
    /// pointer and button events rather than refuse — the phone should not have to know which.
    /// </para>
    /// </summary>
    /// <param name="contactId">Which finger. Zero for the single contact Direct mode uses today.</param>
    InputResult Touch(int contactId, TouchAction action, int x, int y);

    /// <summary>Which mechanism is carrying input, and how healthy it is.</summary>
    NativeInputStatus Status { get; }

    /// <summary>
    /// Releases anything this controller is holding at the platform level.
    /// <para>
    /// Distinct from the per-session bookkeeping in <see cref="InputService"/>: that knows what a
    /// particular phone pressed, this knows what Windows currently believes is down. Both have to be
    /// cleared, and only this one can clear a touch contact.
    /// </para>
    /// </summary>
    /// <returns>How many held buttons, keys and contacts were released.</returns>
    int ReleaseAllHeld();
}
