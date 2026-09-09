namespace PcCore.NativeInput.Hid;

/// <summary>
/// The HID Report Descriptors for the three devices Nexus wants Windows to enumerate.
/// <para>
/// <b>These are the product, not an implementation detail.</b> Everything else in the virtual-HID
/// work is plumbing that carries bytes; these bytes are what makes Windows create a <c>Nexus
/// Mouse</c>, a <c>Nexus Keyboard</c> and a <c>Nexus Touch</c> rather than accept injected events
/// from a program. They live here, in the dependency-free project, and not inside a driver: a
/// descriptor is data, it is the same data whichever backend submits it, and it can therefore be
/// written and tested now — before any driver exists, is signed, or is installed.
/// </para>
/// <para>
/// <b>Three devices, not one device with three collections.</b> A single descriptor carrying three
/// Top-Level Collections behind report IDs would work and would be less code. It is the wrong shape
/// here for one reason: security attribution. Telling Nexus's input apart from a person's depends on
/// knowing <em>which device</em> produced it, and Raw Input identifies a device, not a collection.
/// Three devices means three device paths, three Raw Input handles, and an attribution rule that
/// reads "this came from the Nexus mouse" instead of a timing guess. That is worth the extra
/// descriptor.
/// </para>
/// <para>
/// No report IDs, for the same reason: each descriptor owns its device, so there is nothing to
/// disambiguate, and a report-ID-free descriptor makes every report one byte shorter and one fewer
/// thing to get wrong at several hundred reports a second.
/// </para>
/// </summary>
public static class HidReportDescriptors
{
    /// <summary>
    /// <c>Nexus Mouse</c> — relative pointer, five buttons, vertical wheel and horizontal pan.
    /// <para>
    /// <b>Sixteen-bit X and Y, not eight.</b> An 8-bit relative axis saturates at 127 counts per
    /// report. Touchpad mode on a phone routinely produces a flick larger than that, and a saturated
    /// axis does not merely move less — it changes the pointer's direction of travel relative to the
    /// finger, which reads as the pointer fighting the user. Sixteen bits cost two bytes per report
    /// and remove the ceiling entirely.
    /// </para>
    /// <para>
    /// <b>Five buttons.</b> Left, right, middle and the two thumb buttons, because declaring them
    /// costs nothing while they are unused and they cannot be added later without Windows
    /// re-enumerating the device.
    /// </para>
    /// <para>
    /// <b>AC Pan for horizontal scroll.</b> Usage Page Consumer, usage <c>0x0238</c>. This is the
    /// usage Windows maps to horizontal wheel messages; a second Generic Desktop Wheel would not
    /// produce them. Declared even though the phone does not send horizontal scroll yet, because the
    /// alternative is a descriptor change and a re-enumeration later.
    /// </para>
    /// </summary>
    public static ReadOnlySpan<byte> Mouse =>
    [
        0x05, 0x01,       // USAGE_PAGE (Generic Desktop)
        0x09, 0x02,       // USAGE (Mouse)
        0xA1, 0x01,       // COLLECTION (Application)
        0x09, 0x01,       //   USAGE (Pointer)
        0xA1, 0x00,       //   COLLECTION (Physical)

        0x05, 0x09,       //     USAGE_PAGE (Button)
        0x19, 0x01,       //     USAGE_MINIMUM (Button 1)
        0x29, 0x05,       //     USAGE_MAXIMUM (Button 5)
        0x15, 0x00,       //     LOGICAL_MINIMUM (0)
        0x25, 0x01,       //     LOGICAL_MAXIMUM (1)
        0x95, 0x05,       //     REPORT_COUNT (5)
        0x75, 0x01,       //     REPORT_SIZE (1)
        0x81, 0x02,       //     INPUT (Data,Var,Abs)
        0x95, 0x01,       //     REPORT_COUNT (1)
        0x75, 0x03,       //     REPORT_SIZE (3)
        0x81, 0x03,       //     INPUT (Cnst,Var,Abs)   - padding to a byte

        0x05, 0x01,       //     USAGE_PAGE (Generic Desktop)
        0x09, 0x30,       //     USAGE (X)
        0x09, 0x31,       //     USAGE (Y)
        0x16, 0x00, 0x80, //     LOGICAL_MINIMUM (-32768)
        0x26, 0xFF, 0x7F, //     LOGICAL_MAXIMUM (32767)
        0x75, 0x10,       //     REPORT_SIZE (16)
        0x95, 0x02,       //     REPORT_COUNT (2)
        0x81, 0x06,       //     INPUT (Data,Var,Rel)

        0x09, 0x38,       //     USAGE (Wheel)
        0x15, 0x81,       //     LOGICAL_MINIMUM (-127)
        0x25, 0x7F,       //     LOGICAL_MAXIMUM (127)
        0x75, 0x08,       //     REPORT_SIZE (8)
        0x95, 0x01,       //     REPORT_COUNT (1)
        0x81, 0x06,       //     INPUT (Data,Var,Rel)

        0x05, 0x0C,       //     USAGE_PAGE (Consumer)
        0x0A, 0x38, 0x02, //     USAGE (AC Pan)
        0x15, 0x81,       //     LOGICAL_MINIMUM (-127)
        0x25, 0x7F,       //     LOGICAL_MAXIMUM (127)
        0x75, 0x08,       //     REPORT_SIZE (8)
        0x95, 0x01,       //     REPORT_COUNT (1)
        0x81, 0x06,       //     INPUT (Data,Var,Rel)

        0xC0,             //   END_COLLECTION
        0xC0,             // END_COLLECTION
    ];

    /// <summary>
    /// <c>Nexus Keyboard</c> — the standard boot-protocol layout: modifiers, then six key slots.
    /// <para>
    /// <b>Boot layout on purpose.</b> Eight modifier bits, one reserved byte and six usage slots is
    /// the shape every Windows component already understands, including the ones that run before a
    /// full HID parser is available. Nexus does not need a richer keyboard, and a non-standard one
    /// would be a new class of bug for no gain.
    /// </para>
    /// <para>
    /// <b>Six slots is the rollover limit, and it is enough.</b> A remote keyboard sends what a
    /// person could press, and six simultaneous non-modifier keys is already beyond any real
    /// shortcut. The LED output report is declared because Windows sends one — a keyboard that
    /// refuses <c>SetReport</c> for LEDs is a keyboard that logs an error on every Caps Lock.
    /// </para>
    /// <para>
    /// This descriptor expresses <em>physical keys</em>. It cannot express arbitrary Unicode, which
    /// is a property of HID keyboards rather than a gap in this one. The text path stays on a
    /// separate mechanism for that reason, and says so in diagnostics.
    /// </para>
    /// </summary>
    public static ReadOnlySpan<byte> Keyboard =>
    [
        0x05, 0x01,       // USAGE_PAGE (Generic Desktop)
        0x09, 0x06,       // USAGE (Keyboard)
        0xA1, 0x01,       // COLLECTION (Application)

        0x05, 0x07,       //   USAGE_PAGE (Keyboard/Keypad)
        0x19, 0xE0,       //   USAGE_MINIMUM (Left Control)
        0x29, 0xE7,       //   USAGE_MAXIMUM (Right GUI)
        0x15, 0x00,       //   LOGICAL_MINIMUM (0)
        0x25, 0x01,       //   LOGICAL_MAXIMUM (1)
        0x75, 0x01,       //   REPORT_SIZE (1)
        0x95, 0x08,       //   REPORT_COUNT (8)
        0x81, 0x02,       //   INPUT (Data,Var,Abs)     - modifier byte

        0x95, 0x01,       //   REPORT_COUNT (1)
        0x75, 0x08,       //   REPORT_SIZE (8)
        0x81, 0x03,       //   INPUT (Cnst,Var,Abs)     - reserved byte

        0x05, 0x08,       //   USAGE_PAGE (LEDs)
        0x19, 0x01,       //   USAGE_MINIMUM (Num Lock)
        0x29, 0x05,       //   USAGE_MAXIMUM (Kana)
        0x95, 0x05,       //   REPORT_COUNT (5)
        0x75, 0x01,       //   REPORT_SIZE (1)
        0x91, 0x02,       //   OUTPUT (Data,Var,Abs)    - LED report
        0x95, 0x01,       //   REPORT_COUNT (1)
        0x75, 0x03,       //   REPORT_SIZE (3)
        0x91, 0x03,       //   OUTPUT (Cnst,Var,Abs)    - LED padding

        0x05, 0x07,       //   USAGE_PAGE (Keyboard/Keypad)
        0x19, 0x00,       //   USAGE_MINIMUM (0)
        0x2A, 0xFF, 0x00, //   USAGE_MAXIMUM (0xFF)
        0x15, 0x00,       //   LOGICAL_MINIMUM (0)
        0x26, 0xFF, 0x00, //   LOGICAL_MAXIMUM (255)
        0x75, 0x08,       //   REPORT_SIZE (8)
        0x95, 0x06,       //   REPORT_COUNT (6)
        0x81, 0x00,       //   INPUT (Data,Ary,Abs)     - six key slots

        0xC0,             // END_COLLECTION
    ];

    /// <summary>
    /// <c>Nexus Touch</c> — a single-contact HID touch screen.
    /// <para>
    /// <b>One contact, deliberately, for now.</b> The Android client's Direct mode sends one
    /// contact; a descriptor claiming ten would be a promise the phone does not keep and ten times
    /// the state to get wrong. Contact Count Maximum is a feature report, so raising it later is a
    /// descriptor change — exactly the kind of change that should wait for a second finger to exist
    /// in the UX.
    /// </para>
    /// <para>
    /// <b>Windows requires more than it looks.</b> A touch screen is not a digitizer with X and Y:
    /// Windows will not treat the collection as a touch screen without Tip Switch, In Range, a
    /// Contact Identifier, Contact Count, Scan Time, and a <em>feature</em> report carrying Contact
    /// Count Maximum. Each of those is here because leaving any one out yields a device that
    /// enumerates and then produces nothing.
    /// </para>
    /// <para>
    /// <b>Logical maximum 32767 on both axes.</b> The digitizer reports a position within its own
    /// logical range, and the backend maps the streamed display's rectangle onto that range. Fixing
    /// the range in the descriptor and doing the mapping in software is what keeps a resolution
    /// change, a DPI change or a negative desktop origin from being a device change.
    /// </para>
    /// <para>
    /// Scan Time is in 100-microsecond units, per the Windows digitizer specification. It is not
    /// decorative: Windows uses it to reconstruct velocity for flicks and inertia, and a device that
    /// reports a constant scan time produces gestures that do not throw.
    /// </para>
    /// </summary>
    public static ReadOnlySpan<byte> Digitizer =>
    [
        0x05, 0x0D,       // USAGE_PAGE (Digitizers)
        0x09, 0x04,       // USAGE (Touch Screen)
        0xA1, 0x01,       // COLLECTION (Application)
        0x85, 0x01,       //   REPORT_ID (1)

        0x09, 0x22,       //   USAGE (Finger)
        0xA1, 0x02,       //   COLLECTION (Logical)

        0x09, 0x42,       //     USAGE (Tip Switch)
        0x15, 0x00,       //     LOGICAL_MINIMUM (0)
        0x25, 0x01,       //     LOGICAL_MAXIMUM (1)
        0x75, 0x01,       //     REPORT_SIZE (1)
        0x95, 0x01,       //     REPORT_COUNT (1)
        0x81, 0x02,       //     INPUT (Data,Var,Abs)

        0x09, 0x32,       //     USAGE (In Range)
        0x81, 0x02,       //     INPUT (Data,Var,Abs)

        0x95, 0x06,       //     REPORT_COUNT (6)
        0x81, 0x03,       //     INPUT (Cnst,Var,Abs)   - padding to a byte

        0x75, 0x08,       //     REPORT_SIZE (8)
        0x09, 0x51,       //     USAGE (Contact Identifier)
        0x95, 0x01,       //     REPORT_COUNT (1)
        0x25, 0x7F,       //     LOGICAL_MAXIMUM (127)
        0x81, 0x02,       //     INPUT (Data,Var,Abs)

        // X and Y carry the full set of global items Windows demands: logical range, physical
        // range, unit and unit exponent. Declaring only the logical range — which is what the
        // first draft did — makes the collection non-functional as a touch screen, because the
        // host has no way to relate reported values to a surface.
        0x05, 0x01,       //     USAGE_PAGE (Generic Desktop)
        0x15, 0x00,       //     LOGICAL_MINIMUM (0)
        0x26, 0xFF, 0x7F, //     LOGICAL_MAXIMUM (32767)
        0x55, 0x0E,       //     UNIT_EXPONENT (-2)
        0x65, 0x13,       //     UNIT (Inch, EngLinear)
        0x35, 0x00,       //     PHYSICAL_MINIMUM (0)
        0x46, 0xB5, 0x04, //     PHYSICAL_MAXIMUM (1205)  - 12.05 inches
        0x75, 0x10,       //     REPORT_SIZE (16)
        0x95, 0x01,       //     REPORT_COUNT (1)
        0x09, 0x30,       //     USAGE (X)
        0x81, 0x02,       //     INPUT (Data,Var,Abs)
        0x46, 0x8A, 0x03, //     PHYSICAL_MAXIMUM (906)   - 9.06 inches
        0x09, 0x31,       //     USAGE (Y)
        0x81, 0x02,       //     INPUT (Data,Var,Abs)

        0xC0,             //   END_COLLECTION

        0x05, 0x0D,       //   USAGE_PAGE (Digitizers)
        0x35, 0x00,       //   PHYSICAL_MINIMUM (0)
        0x45, 0x00,       //   PHYSICAL_MAXIMUM (0)
        0x65, 0x00,       //   UNIT (None)
        0x55, 0x00,       //   UNIT_EXPONENT (0)
        0x09, 0x54,       //   USAGE (Contact Count)
        0x25, 0x7F,       //   LOGICAL_MAXIMUM (127)
        0x75, 0x08,       //   REPORT_SIZE (8)
        0x95, 0x01,       //   REPORT_COUNT (1)
        0x81, 0x02,       //   INPUT (Data,Var,Abs)

        0x55, 0x0C,       //   UNIT_EXPONENT (-4)
        0x66, 0x01, 0x10, //   UNIT (Seconds)
        0x27, 0xFF, 0xFF, 0x00, 0x00, // LOGICAL_MAXIMUM (65535)
        0x75, 0x10,       //   REPORT_SIZE (16)
        0x95, 0x01,       //   REPORT_COUNT (1)
        0x09, 0x56,       //   USAGE (Scan Time)
        0x81, 0x02,       //   INPUT (Data,Var,Abs)

        0x65, 0x00,       //   UNIT (None)
        0x55, 0x00,       //   UNIT_EXPONENT (0)

        // The Contact Count Maximum feature report. It sits in the top-level collection and not in
        // the Finger collection, which Microsoft states explicitly, and it carries its own report
        // id so the host can request it without ambiguity against the input report.
        0x85, 0x02,       //   REPORT_ID (2)
        0x09, 0x55,       //   USAGE (Contact Count Maximum)
        0x25, 0x7F,       //   LOGICAL_MAXIMUM (127)
        0x75, 0x08,       //   REPORT_SIZE (8)
        0x95, 0x01,       //   REPORT_COUNT (1)
        0xB1, 0x02,       //   FEATURE (Data,Var,Abs)

        0xC0,             // END_COLLECTION
    ];

    /// <summary>The digitizer's input report id. Mouse and keyboard use no report ids at all.</summary>
    public const byte DigitizerInputReportId = 1;

    /// <summary>The report id of the Contact Count Maximum feature report.</summary>
    public const byte DigitizerFeatureReportId = 2;

    /// <summary>Bytes in one mouse input report.</summary>
    public const int MouseReportLength = 7;

    /// <summary>Bytes in one keyboard input report.</summary>
    public const int KeyboardReportLength = 8;

    /// <summary>
    /// Bytes in one digitizer input report: report id, tip and range flags, contact id, X, Y,
    /// contact count, scan time. The tests recompute this from the descriptor itself rather than
    /// trusting the arithmetic here — which has now been wrong twice, at 7 and again at 9.
    /// </summary>
    public const int DigitizerReportLength = 10;

    /// <summary>Bytes in the Contact Count Maximum feature report: its report id, then the value.</summary>
    public const int DigitizerFeatureReportLength = 2;

    /// <summary>How many contacts the digitizer descriptor promises, answered in the feature report.</summary>
    public const byte MaxContacts = 1;

    /// <summary>The logical range of the digitizer's axes. Both axes span 0 to this value.</summary>
    public const int DigitizerLogicalMaximum = 32767;
}
