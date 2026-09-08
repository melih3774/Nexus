namespace PcCore.NativeInput;

/// <summary>
/// The mark every mouse and keyboard event Nexus injects carries.
/// <para>
/// This is how the security detector tells Nexus's own remote input apart from a person at the
/// keyboard. It is an exact-match tag rather than a timing window: a <c>SendInput</c> event either
/// carries this value in <c>dwExtraInfo</c> or it does not, so remote control during an incident can
/// never be mistaken for the intruder, and using the PC from the phone while armed cannot raise an
/// alarm about the owner.
/// </para>
/// <para>
/// <b>Thirty-two bits, deliberately.</b> Windows preserves the full 64-bit value through the
/// keyboard hook but only the low half through the mouse hook — measured on this machine, where a
/// 64-bit tag came back truncated and every injected mouse event was misattributed as some other
/// program's synthetic input. A value that fits in 32 bits survives both paths intact.
/// </para>
/// <para>
/// It is not a secret and does not need to be. Anything that could forge it could inject input
/// directly, which is a far larger problem than a mislabelled activity event. It is also, on its
/// own, no longer the whole attribution story: touch injection has no field to put it in, which is
/// why <c>INexusInputAttribution</c> exists alongside it.
/// </para>
/// </summary>
public static class NexusInputTag
{
    /// <summary>The tag value, as it appears in <c>dwExtraInfo</c>.</summary>
    public const int Value = 0x4E58_5330;

    /// <summary>The tag as it is written into an <c>INPUT</c> structure.</summary>
    public static readonly nint Signature = Value;

    /// <summary>
    /// Windows' own marker on mouse messages that were promoted from pen or touch input.
    /// <para>
    /// Documented as <c>MI_WP_SIGNATURE</c>. Compared under <see cref="PenOrTouchMask"/>; the low
    /// byte carries a cursor id, and its 0x80 bit distinguishes touch from pen.
    /// </para>
    /// </summary>
    public const long PenOrTouchSignature = 0xFF51_5700;

    public const long PenOrTouchMask = 0xFFFF_FF00;

    /// <summary>True when this extra-info value is Nexus's own tag.</summary>
    public static bool IsNexus(long extraInfo) => (extraInfo & 0xFFFF_FFFF) == Value;

    /// <summary>True when Windows promoted this event from a pen or touch contact.</summary>
    public static bool IsPenOrTouch(long extraInfo) => (extraInfo & PenOrTouchMask) == PenOrTouchSignature;
}
