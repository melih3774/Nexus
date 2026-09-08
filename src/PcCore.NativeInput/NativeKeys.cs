namespace PcCore.NativeInput;

/// <summary>
/// The closed set of virtual-key codes Nexus is allowed to press.
/// <para>
/// <b>Why this exists separately from the protocol's key enum.</b> The capability layer already
/// refuses anything outside <c>NexusKey</c>, but that check happens in a different process from the
/// one that ends up calling <c>SendInput</c> once the Nexus Input Broker is installed. A
/// virtual-key code that crosses a process boundary and lands in a Win32 structure unexamined is the
/// classic shape of "validated somewhere else, therefore not validated" — so the elevated side
/// checks the number itself, against this list, and refuses anything not on it.
/// </para>
/// <para>
/// The list is the exact image of <c>NexusKey</c> under the Windows layer's mapping.
/// <c>NativeKeyAllowListTests</c> asserts that, so a key added to the protocol without being added
/// here fails a test rather than silently doing nothing once native input is installed.
/// </para>
/// </summary>
public static class NativeKeys
{
    private static readonly HashSet<ushort> Allowed = BuildAllowList();

    public static bool IsAllowed(ushort virtualKey) => Allowed.Contains(virtualKey);

    /// <summary>Every code on the list. Exposed for diagnostics and for the cross-check test.</summary>
    public static IReadOnlyCollection<ushort> All => Allowed;

    private static HashSet<ushort> BuildAllowList()
    {
        var set = new HashSet<ushort>
        {
            0x08, // Backspace
            0x09, // Tab
            0x0D, // Enter
            0x10, // Shift
            0x11, // Control
            0x12, // Alt
            0x1B, // Escape
            0x20, // Space
            0x21, // Page Up
            0x22, // Page Down
            0x23, // End
            0x24, // Home
            0x25, // Left
            0x26, // Up
            0x27, // Right
            0x28, // Down
            0x2D, // Insert
            0x2E, // Delete
            0x5B, // Left Windows
        };

        // 0-9 and A-Z share their virtual-key codes with their ASCII values.
        for (ushort key = 0x30; key <= 0x39; key++)
        {
            set.Add(key);
        }

        for (ushort key = 0x41; key <= 0x5A; key++)
        {
            set.Add(key);
        }

        // F1 to F12.
        for (ushort key = 0x70; key <= 0x7B; key++)
        {
            set.Add(key);
        }

        return set;
    }
}
