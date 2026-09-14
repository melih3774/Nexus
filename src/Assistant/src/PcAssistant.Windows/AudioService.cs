using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PcAssistant.Windows;

/// <summary>Master output state, as Windows reports it rather than as it was last set.</summary>
public sealed record AudioState(string DeviceName, string DeviceId, int VolumePercent, bool Muted, string Note);

/// <summary>
/// Master volume through Core Audio — the same endpoint the volume flyout drives, so what
/// PC Assistant reports is what the owner sees, and what it sets is what they hear.
///
/// Deliberately only the default playback endpoint and only master volume. Per-application
/// mixer control is a much larger surface (session enumeration, per-session lifetime, process
/// identity) for a request nobody actually makes of an assistant.
///
/// Every value returned is read back from the device after any change: setting a volume and
/// reporting the number that was asked for would be exactly the kind of unverified claim this
/// pass exists to remove.
/// </summary>
public sealed class AudioService : IDisposable
{
    // Core Audio objects are apartment-bound; one MTA worker keeps every call on one thread.
    private readonly ApartmentWorker worker = new(ApartmentState.MTA);

    public Task<AudioState> GetAsync(CancellationToken cancellationToken = default) =>
        worker.RunAsync(_ => Read(null, null), cancellationToken);

    /// <summary>Sets master volume to <paramref name="percent"/> and reports the level actually applied.</summary>
    public Task<AudioState> SetVolumeAsync(int percent, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "Volume is a percentage between 0 and 100.");
        return worker.RunAsync(_ => Read(percent, null), cancellationToken);
    }

    public Task<AudioState> SetMuteAsync(bool muted, CancellationToken cancellationToken = default) =>
        worker.RunAsync(_ => Read(null, muted), cancellationToken);

    private static AudioState Read(int? setPercent, bool? setMute)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!)!;
        try
        {
            // eRender / eMultimedia: the endpoint the owner's own volume control acts on.
            var hr = enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
            if (hr < 0 || device is null)
                throw new InvalidOperationException(
                    "Windows reports no default playback device. Nothing is playing anywhere, and there is no master " +
                    "volume to read or set until an output device is connected and enabled.");

            try
            {
                var volumeIid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref volumeIid, 1 /* CLSCTX_INPROC_SERVER */, 0, out var raw));
                var volume = (IAudioEndpointVolume)raw;
                try
                {
                    if (setPercent is { } percent)
                        Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(percent / 100f, Guid.Empty));
                    if (setMute is { } mute)
                        Marshal.ThrowExceptionForHR(volume.SetMute(mute, Guid.Empty));

                    Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var scalar));
                    Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));

                    var applied = (int)Math.Round(scalar * 100);
                    var note = "Read back from the audio endpoint after the change, not assumed.";
                    if (setPercent is { } asked && Math.Abs(asked - applied) > 1)
                        note = $"Asked for {asked}% and the device reports {applied}%; some endpoints quantize volume steps.";
                    else if (muted && setPercent is not null)
                        note += " The device is muted, so this level is not audible until it is unmuted.";

                    return new AudioState(FriendlyName(device), DeviceId(device), applied, muted, note);
                }
                finally { Marshal.ReleaseComObject(volume); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static string DeviceId(IMMDevice device)
    {
        if (device.GetId(out var pointer) < 0 || pointer == 0) return "unknown";
        try { return Marshal.PtrToStringUni(pointer) ?? "unknown"; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    /// <summary>The name the owner sees in the volume flyout, or a usable fallback.</summary>
    private static string FriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) < 0 || store is null) return "Default playback device";
            try
            {
                var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
                if (store.GetValue(ref key, out var value) < 0) return "Default playback device";
                try { return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) ?? "Default playback device" : "Default playback device"; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return "Default playback device";
        }
    }

    public void Dispose() => worker.Dispose();

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    /// <summary>Only large enough to carry a string pointer out; every other type is ignored.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public nint Second;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int classContext, nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object @interface);
        [PreserveSig] int OpenPropertyStore(int access, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? properties);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    /// <summary>
    /// Declared in full vtable order even though only four methods are used: COM dispatch is by
    /// slot, so an omitted method silently calls the wrong one.
    /// </summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float decibels, Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out int step, out int stepCount);
        [PreserveSig] int VolumeStepUp(Guid eventContext);
        [PreserveSig] int VolumeStepDown(Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out int mask);
        [PreserveSig] int GetVolumeRange(out float minimumDecibels, out float maximumDecibels, out float incrementDecibels);
    }
}
