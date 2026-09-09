namespace PcCore.NativeInput.Hid;

/// <summary>
/// Nexus's closed key set, expressed as HID usages on the Keyboard/Keypad page.
/// <para>
/// <b>A different mapping from the SendInput one, and necessarily so.</b> The existing key map
/// produces Windows virtual-key codes, which are an operating-system concept. A HID keyboard reports
/// usages, which are a hardware concept, and Windows derives the virtual key from the usage plus the
/// active layout. That indirection is the point: a usage means "the key in this physical position",
/// so the same report produces the right character on a Turkish layout without Nexus knowing the
/// layout exists.
/// </para>
/// <para>
/// <b>Modifiers are not usages here.</b> A boot keyboard report carries them as bits in its first
/// byte, not as entries in the key array, so they are mapped separately and the two must not be
/// mixed — a Control placed in a key slot is not a held modifier, it is a keypress Windows will
/// ignore.
/// </para>
/// <para>
/// Letters and digits exist for shortcuts only, exactly as in the SendInput map. Typing goes through
/// the text path, because a HID keyboard cannot express an arbitrary character.
/// </para>
/// </summary>
public static class HidKeyMap
{
    /// <summary>The modifier bit for a key that is one, or null when it is an ordinary key.</summary>
    public static HidKeyboardModifiers? Modifier(NexusKeyLike key) => key switch
    {
        NexusKeyLike.Control => HidKeyboardModifiers.LeftControl,
        NexusKeyLike.Shift => HidKeyboardModifiers.LeftShift,
        NexusKeyLike.Alt => HidKeyboardModifiers.LeftAlt,
        NexusKeyLike.Meta => HidKeyboardModifiers.LeftGui,
        _ => null,
    };

    /// <summary>
    /// The HID usage for an ordinary key, or 0 when the key is a modifier or is unmapped.
    /// <para>
    /// Zero is the "no key" value in a boot report, so an unmapped key naturally becomes nothing
    /// rather than becoming some other key. That is the safe direction for a value arriving over the
    /// network.
    /// </para>
    /// </summary>
    public static byte Usage(NexusKeyLike key) => key switch
    {
        NexusKeyLike.A => 0x04,
        NexusKeyLike.B => 0x05,
        NexusKeyLike.C => 0x06,
        NexusKeyLike.D => 0x07,
        NexusKeyLike.E => 0x08,
        NexusKeyLike.F => 0x09,
        NexusKeyLike.G => 0x0A,
        NexusKeyLike.H => 0x0B,
        NexusKeyLike.I => 0x0C,
        NexusKeyLike.J => 0x0D,
        NexusKeyLike.K => 0x0E,
        NexusKeyLike.L => 0x0F,
        NexusKeyLike.M => 0x10,
        NexusKeyLike.N => 0x11,
        NexusKeyLike.O => 0x12,
        NexusKeyLike.P => 0x13,
        NexusKeyLike.Q => 0x14,
        NexusKeyLike.R => 0x15,
        NexusKeyLike.S => 0x16,
        NexusKeyLike.T => 0x17,
        NexusKeyLike.U => 0x18,
        NexusKeyLike.V => 0x19,
        NexusKeyLike.W => 0x1A,
        NexusKeyLike.X => 0x1B,
        NexusKeyLike.Y => 0x1C,
        NexusKeyLike.Z => 0x1D,

        // 1 through 9 are contiguous and 0 follows them, which is a HID quirk rather than an
        // arithmetic pattern: Digit0 is 0x27, not 0x1D.
        NexusKeyLike.Digit1 => 0x1E,
        NexusKeyLike.Digit2 => 0x1F,
        NexusKeyLike.Digit3 => 0x20,
        NexusKeyLike.Digit4 => 0x21,
        NexusKeyLike.Digit5 => 0x22,
        NexusKeyLike.Digit6 => 0x23,
        NexusKeyLike.Digit7 => 0x24,
        NexusKeyLike.Digit8 => 0x25,
        NexusKeyLike.Digit9 => 0x26,
        NexusKeyLike.Digit0 => 0x27,

        NexusKeyLike.Enter => 0x28,
        NexusKeyLike.Escape => 0x29,
        NexusKeyLike.Backspace => 0x2A,
        NexusKeyLike.Tab => 0x2B,
        NexusKeyLike.Space => 0x2C,

        NexusKeyLike.F1 => 0x3A,
        NexusKeyLike.F2 => 0x3B,
        NexusKeyLike.F3 => 0x3C,
        NexusKeyLike.F4 => 0x3D,
        NexusKeyLike.F5 => 0x3E,
        NexusKeyLike.F6 => 0x3F,
        NexusKeyLike.F7 => 0x40,
        NexusKeyLike.F8 => 0x41,
        NexusKeyLike.F9 => 0x42,
        NexusKeyLike.F10 => 0x43,
        NexusKeyLike.F11 => 0x44,
        NexusKeyLike.F12 => 0x45,

        NexusKeyLike.Insert => 0x49,
        NexusKeyLike.Home => 0x4A,
        NexusKeyLike.PageUp => 0x4B,
        NexusKeyLike.Delete => 0x4C,
        NexusKeyLike.End => 0x4D,
        NexusKeyLike.PageDown => 0x4E,

        NexusKeyLike.ArrowRight => 0x4F,
        NexusKeyLike.ArrowLeft => 0x50,
        NexusKeyLike.ArrowDown => 0x51,
        NexusKeyLike.ArrowUp => 0x52,

        _ => 0,
    };

    /// <summary>True when this key can be expressed by the HID keyboard at all.</summary>
    public static bool IsMappable(NexusKeyLike key) => Modifier(key) is not null || Usage(key) != 0;
}

/// <summary>
/// The key set, mirrored here so this project stays free of a reference to the capabilities layer.
/// <para>
/// The values match <c>NexusKey</c> position for position, and <c>HidKeyMapMirrorsNexusKey</c>
/// asserts it — a mirrored enum that silently drifts would map every key to the wrong one, which is
/// the kind of failure that produces garbage rather than an error.
/// </para>
/// </summary>
public enum NexusKeyLike
{
    Escape, Tab, Enter, Backspace, Delete, Insert, Space,
    Home, End, PageUp, PageDown,
    ArrowLeft, ArrowUp, ArrowRight, ArrowDown,
    Control, Alt, Shift, Meta,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,
}
