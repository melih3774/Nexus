using System.Buffers.Binary;

namespace PcCore.NativeInput.Hid;

/// <summary>Which mouse button a report bit belongs to. The order is the descriptor's.</summary>
[Flags]
public enum HidMouseButtons : byte
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}

/// <summary>The eight modifier bits of a boot-layout keyboard report, in descriptor order.</summary>
[Flags]
public enum HidKeyboardModifiers : byte
{
    None = 0,
    LeftControl = 1 << 0,
    LeftShift = 1 << 1,
    LeftAlt = 1 << 2,
    LeftGui = 1 << 3,
    RightControl = 1 << 4,
    RightShift = 1 << 5,
    RightAlt = 1 << 6,
    RightGui = 1 << 7,
}

/// <summary>
/// One <c>Nexus Mouse</c> input report.
/// <para>
/// A struct with a <c>Write</c> rather than a byte array with offsets, because the offsets are the
/// part that goes wrong. The report is little-endian, which is what HID specifies and what every
/// consumer on this platform expects.
/// </para>
/// </summary>
public readonly record struct HidMouseReport(
    HidMouseButtons Buttons,
    short DeltaX,
    short DeltaY,
    sbyte Wheel,
    sbyte Pan)
{
    /// <summary>A report that presses nothing and moves nothing. Sent to neutralise the device.</summary>
    public static readonly HidMouseReport Neutral = new(HidMouseButtons.None, 0, 0, 0, 0);

    /// <summary>
    /// Writes this report into <paramref name="destination"/>, which must be at least
    /// <see cref="HidReportDescriptors.MouseReportLength"/> bytes.
    /// </summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < HidReportDescriptors.MouseReportLength)
        {
            throw new ArgumentException(
                $"a mouse report needs {HidReportDescriptors.MouseReportLength} bytes",
                nameof(destination));
        }

        destination[0] = (byte)((byte)Buttons & 0x1F);
        BinaryPrimitives.WriteInt16LittleEndian(destination[1..], DeltaX);
        BinaryPrimitives.WriteInt16LittleEndian(destination[3..], DeltaY);
        destination[5] = (byte)Wheel;
        destination[6] = (byte)Pan;
    }
}

/// <summary>
/// One <c>Nexus Keyboard</c> input report: a modifier byte, a reserved byte, and six key slots.
/// <para>
/// The six slots are an <em>array</em>, not a bitmap: each byte holds a HID usage, and an empty slot
/// is zero. Releasing one key of three means rewriting the array without it and sending the whole
/// report, which is how HID keyboards have always worked and why the caller has to hold the set of
/// pressed keys rather than send deltas.
/// </para>
/// </summary>
public readonly record struct HidKeyboardReport
{
    private readonly byte _key0;
    private readonly byte _key1;
    private readonly byte _key2;
    private readonly byte _key3;
    private readonly byte _key4;
    private readonly byte _key5;

    public HidKeyboardReport(HidKeyboardModifiers modifiers, ReadOnlySpan<byte> keys)
    {
        if (keys.Length > MaxKeys)
        {
            throw new ArgumentException($"a boot keyboard report holds at most {MaxKeys} keys", nameof(keys));
        }

        Modifiers = modifiers;

        _key0 = keys.Length > 0 ? keys[0] : (byte)0;
        _key1 = keys.Length > 1 ? keys[1] : (byte)0;
        _key2 = keys.Length > 2 ? keys[2] : (byte)0;
        _key3 = keys.Length > 3 ? keys[3] : (byte)0;
        _key4 = keys.Length > 4 ? keys[4] : (byte)0;
        _key5 = keys.Length > 5 ? keys[5] : (byte)0;
    }

    /// <summary>How many non-modifier keys one report can carry.</summary>
    public const int MaxKeys = 6;

    public HidKeyboardModifiers Modifiers { get; }

    /// <summary>A report holding nothing down. Sent to neutralise the device.</summary>
    public static readonly HidKeyboardReport Neutral = new(HidKeyboardModifiers.None, []);

    /// <summary>
    /// Writes this report into <paramref name="destination"/>, which must be at least
    /// <see cref="HidReportDescriptors.KeyboardReportLength"/> bytes.
    /// </summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < HidReportDescriptors.KeyboardReportLength)
        {
            throw new ArgumentException(
                $"a keyboard report needs {HidReportDescriptors.KeyboardReportLength} bytes",
                nameof(destination));
        }

        destination[0] = (byte)Modifiers;
        destination[1] = 0; // reserved, and the descriptor declares it constant
        destination[2] = _key0;
        destination[3] = _key1;
        destination[4] = _key2;
        destination[5] = _key3;
        destination[6] = _key4;
        destination[7] = _key5;
    }
}

/// <summary>
/// One <c>Nexus Touch</c> input report: a single contact, in the digitizer's own logical space.
/// <para>
/// <b>In Range without Tip Switch is a hover, and that matters.</b> A contact that lifts must send
/// one final report with the tip up before it stops reporting, or Windows keeps the contact alive
/// and the next press is interpreted as a continuation. The lifecycle is the part of touch that
/// breaks, not the coordinates.
/// </para>
/// </summary>
public readonly record struct HidDigitizerReport(
    bool TipSwitch,
    bool InRange,
    byte ContactId,
    ushort X,
    ushort Y,
    ushort ScanTime)
{
    /// <summary>
    /// A report with the contact lifted and out of range. Sent to neutralise the device, and the
    /// only correct way to end a contact that a dropped connection interrupted.
    /// </summary>
    public static readonly HidDigitizerReport Neutral = new(false, false, 0, 0, 0, 0);

    /// <summary>
    /// Writes this report into <paramref name="destination"/>, which must be at least
    /// <see cref="HidReportDescriptors.DigitizerReportLength"/> bytes.
    /// </summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < HidReportDescriptors.DigitizerReportLength)
        {
            throw new ArgumentException(
                $"a digitizer report needs {HidReportDescriptors.DigitizerReportLength} bytes",
                nameof(destination));
        }

        byte flags = 0;
        if (TipSwitch)
        {
            flags |= 0x01;
        }

        if (InRange)
        {
            flags |= 0x02;
        }

        // The digitizer is the one device that uses report ids, so the id leads the buffer. Mouse
        // and keyboard reports have no such byte.
        destination[0] = HidReportDescriptors.DigitizerInputReportId;
        destination[1] = flags;
        destination[2] = ContactId;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..], X);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[5..], Y);

        // Contact Count. One while a finger is present, zero once it has lifted: Windows uses the
        // count to know a frame is finished, and a lift that still claims one contact hangs.
        destination[7] = TipSwitch || InRange ? (byte)1 : (byte)0;

        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], ScanTime);
    }
}
