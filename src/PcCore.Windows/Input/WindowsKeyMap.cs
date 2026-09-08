using PcCore.Capabilities.Input;

namespace PcCore.Windows.Input;

/// <summary>
/// The one translation from a protocol key to a Windows virtual-key code.
/// <para>
/// Shared by both input paths on purpose. If the local path and the broker path each had their own
/// map, the first divergence would show up as "the arrow keys behave differently once native input
/// is installed" — a bug that is tedious to notice and miserable to find.
/// </para>
/// </summary>
internal static class WindowsKeyMap
{
    /// <summary>
    /// Maps a protocol key to a virtual-key code, and says whether it needs the extended-key flag.
    /// <para>
    /// The extended flag matters: without it, the arrow keys and navigation cluster are read as
    /// their numeric-keypad twins, so Home types 7 and the up arrow types 8 whenever Num Lock is on.
    /// </para>
    /// </summary>
    public static (ushort VirtualKey, bool Extended) Map(NexusKey key) => key switch
    {
        NexusKey.Escape => (0x1B, false),
        NexusKey.Tab => (0x09, false),
        NexusKey.Enter => (0x0D, false),
        NexusKey.Backspace => (0x08, false),
        NexusKey.Delete => (0x2E, true),
        NexusKey.Insert => (0x2D, true),
        NexusKey.Space => (0x20, false),

        NexusKey.Home => (0x24, true),
        NexusKey.End => (0x23, true),
        NexusKey.PageUp => (0x21, true),
        NexusKey.PageDown => (0x22, true),

        NexusKey.ArrowLeft => (0x25, true),
        NexusKey.ArrowUp => (0x26, true),
        NexusKey.ArrowRight => (0x27, true),
        NexusKey.ArrowDown => (0x28, true),

        NexusKey.Control => (0x11, false),
        NexusKey.Alt => (0x12, false),
        NexusKey.Shift => (0x10, false),

        // Left Windows key. The right one behaves identically for every shortcut worth sending.
        NexusKey.Meta => (0x5B, true),

        NexusKey.F1 => (0x70, false),
        NexusKey.F2 => (0x71, false),
        NexusKey.F3 => (0x72, false),
        NexusKey.F4 => (0x73, false),
        NexusKey.F5 => (0x74, false),
        NexusKey.F6 => (0x75, false),
        NexusKey.F7 => (0x76, false),
        NexusKey.F8 => (0x77, false),
        NexusKey.F9 => (0x78, false),
        NexusKey.F10 => (0x79, false),
        NexusKey.F11 => (0x7A, false),
        NexusKey.F12 => (0x7B, false),

        // A-Z share their virtual-key codes with their ASCII values, and 0-9 with theirs.
        >= NexusKey.A and <= NexusKey.Z => ((ushort)(0x41 + (key - NexusKey.A)), false),
        >= NexusKey.Digit0 and <= NexusKey.Digit9 => ((ushort)(0x30 + (key - NexusKey.Digit0)), false),

        _ => (0x1B, false),
    };
}
