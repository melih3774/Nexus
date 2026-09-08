using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcCore.NativeInput;

/// <summary>
/// Whether a person standing at this PC could touch the screen.
/// <para>
/// <b>Why this is not <c>GetSystemMetrics(SM_DIGITIZER)</c>.</b> That metric is system-wide and
/// includes injection: measured on this machine it read <c>0x0</c>, then <c>0xC1</c> after the first
/// <c>InitializeTouchInjection</c> call — and, worse, it read <c>0xC1</c> in a freshly started
/// process simply because the Nexus Input Broker was already running in another one. A metric that
/// says "yes" because Nexus itself is the touchscreen is exactly the wrong answer to give the
/// security subsystem.
/// </para>
/// <para>
/// So this enumerates the actual raw input devices instead. Two exclusions make the answer right,
/// and both were found by enumerating this machine rather than by reasoning:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A precision touchpad is a HID digitizer too. This laptop reports one at usage page <c>0x0D</c>,
/// usage <c>0x05</c> (Touch Pad) — and a touchpad is not something a person touches the screen with.
/// Only a touch screen, a pen or a generic digitizer counts.
/// </description></item>
/// <item><description>
/// Touch injection creates a device that <em>is</em> enumerable. Measured: before the first
/// <c>InitializeTouchInjection</c> call the only <c>0x0D</c> device was the touchpad; afterwards a
/// new one appeared at usage <c>0x04</c> (Touch Screen), with vendor and product id both zero, named
/// <c>VIRTUAL_DIGITIZER</c>. Counting that would mean answering "yes, someone could touch this
/// screen" because Nexus had made itself into one.
/// </description></item>
/// </list>
/// <para>
/// The answer matters to attribution rather than to input. On a machine with a real touchscreen, a
/// person touching it produces the same promoted mouse messages Nexus's own touch does, so the
/// detector has to treat that signature as possibly-human. On a machine without one, it cannot be.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PhysicalDigitizer
{
    /// <summary>HID usage page 0x0D: Digitizers.</summary>
    private const ushort DigitizerUsagePage = 0x0D;

    private const ushort UsageDigitizer = 0x01;
    private const ushort UsagePen = 0x02;
    private const ushort UsageTouchScreen = 0x04;

    /// <summary>
    /// True when a real pen or touch-screen digitizer is attached.
    /// <para>
    /// Enumerated on each call rather than cached: a tablet or a pen display can be plugged in after
    /// Nexus starts, and answering from a stale snapshot would mean answering "no touchscreen" about
    /// a machine that has one.
    /// </para>
    /// </summary>
    public static bool IsPresent()
    {
        var size = (uint)Marshal.SizeOf<RawInputDeviceList>();

        if (GetRawInputDeviceList(nint.Zero, out var count, size) != 0 || count == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)(size * count));

        try
        {
            var written = GetRawInputDeviceList(buffer, out count, size);

            if (written == unchecked((uint)-1))
            {
                return false;
            }

            for (var i = 0; i < written; i++)
            {
                var entry = Marshal.PtrToStructure<RawInputDeviceList>(buffer + (int)(i * size));

                if (entry.Type != RimTypeHid || !IsDigitizer(entry.Device) || IsInjected(entry.Device))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsDigitizer(nint device)
    {
        var info = new RawInputDeviceInfo { Size = Marshal.SizeOf<RawInputDeviceInfo>() };
        var size = (uint)info.Size;

        if (GetRawInputDeviceInfoW(device, RidiDeviceInfo, ref info, ref size) == unchecked((uint)-1))
        {
            return false;
        }

        return info.Type == RimTypeHid
            && info.Hid.UsagePage == DigitizerUsagePage
            && info.Hid.Usage is UsageDigitizer or UsagePen or UsageTouchScreen;
    }

    /// <summary>
    /// Whether this device is the one touch injection creates rather than a real digitizer.
    /// <para>
    /// Matched on the device name, which Windows sets to a fixed string for the injection device. A
    /// name that cannot be read is treated as injected — the conservative direction here is to
    /// under-report a digitizer, because over-reporting one would mean Nexus telling the owner their
    /// touchscreen-less laptop has a touchscreen.
    /// </para>
    /// </summary>
    private static bool IsInjected(nint device)
    {
        uint length = 0;

        if (GetRawInputDeviceInfoW(device, RidiDeviceName, null, ref length) != 0 || length == 0)
        {
            return true;
        }

        var name = new StringBuilder((int)length + 2);

        return GetRawInputDeviceInfoW(device, RidiDeviceName, name, ref length) == unchecked((uint)-1)
            || name.ToString().Contains(InjectedDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name Windows gives the device that <c>InitializeTouchInjection</c> creates.</summary>
    private const string InjectedDeviceName = "VIRTUAL_DIGITIZER";

    private const uint RimTypeHid = 2;
    private const uint RidiDeviceInfo = 0x2000000b;
    private const uint RidiDeviceName = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHidInfo
    {
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseInfo
    {
        public uint Id;
        public uint NumberOfButtons;
        public uint SampleRate;
        [MarshalAs(UnmanagedType.Bool)] public bool HasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboardInfo
    {
        public uint Type;
        public uint SubType;
        public uint KeyboardMode;
        public uint NumberOfFunctionKeys;
        public uint NumberOfIndicators;
        public uint NumberOfKeysTotal;
    }

    /// <summary>
    /// RID_DEVICE_INFO. The three device kinds share a union, so the explicit layout overlays them
    /// at the same offset and the struct keeps the size Windows checks against.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct RawInputDeviceInfo
    {
        [FieldOffset(0)] public int Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public RawMouseInfo Mouse;
        [FieldOffset(8)] public RawKeyboardInfo Keyboard;
        [FieldOffset(8)] public RawHidInfo Hid;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(nint list, out uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(
        nint device,
        uint command,
        ref RawInputDeviceInfo info,
        ref uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(
        nint device,
        uint command,
        StringBuilder? name,
        ref uint size);
}
