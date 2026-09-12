using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;
using PcCore.Windows.Interop;

using static PcCore.Windows.Interop.MediaFoundationInterop;

namespace PcCore.Windows.Media;

/// <summary>
/// Starts and stops the Media Foundation platform once for the whole process.
/// <para>
/// <c>MFStartup</c> is reference counted by Windows, but calling it from three places and shutting
/// it down from two is how a screen stream ends up killing a camera stream. One owner, counted here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MediaFoundationPlatform
{
    private static readonly Lock Gate = new();
    private static int _users;

    public static void Acquire()
    {
        lock (Gate)
        {
            if (_users++ == 0)
            {
                // COM first. MFStartup will do it implicitly, but implicitly means "in whatever
                // apartment this thread happens to be", and an STA thread makes every later call
                // marshal. RPC_E_CHANGED_MODE means somebody already chose, which is fine.
                var com = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);

                if (com < 0 && com != RpcEChangedMode)
                {
                    _users--;
                    Check(com, "CoInitializeEx");
                }

                Check(MFStartup(MfVersion, MfStartupNoSocket), "MFStartup");
            }
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_users > 0 && --_users == 0)
            {
                MFShutdown();
            }
        }
    }
}

/// <summary>
/// An H.264 encoder built on whichever Media Foundation transform this machine has, hardware first.
/// <para>
/// <b>Why an MFT and not a library.</b> The encoder that matters is already installed: every GPU
/// driver on Windows registers its hardware encoder as a Media Foundation transform, and on this
/// machine that is NVENC on the 4060 with Microsoft's software encoder behind it. Reaching them
/// through MFT enumeration means one code path picks up whatever the machine has, including a
/// machine that has neither — where <see cref="MediaFoundationVideoEncoderFactory.IsAvailable"/> is
/// false and the stream falls back to still frames rather than failing.
/// </para>
/// <para>
/// <b>Hardware encoders are asynchronous, and that is the whole shape of this class.</b> A software
/// MFT is a function call: push a frame, pull a frame. A hardware MFT is a queue with an event
/// generator — it tells you when it wants input and when it has output, and calling
/// <c>ProcessInput</c> before it asked is an error rather than a wait. So a pump thread reads its
/// events and does the output side, while <see cref="Encode"/> waits for permission to push. Both
/// paths are implemented here because a machine with no hardware encoder still has to work.
/// </para>
/// <para>
/// <b>Configured for a pointer, not a film.</b> No B-frames, so nothing is ever held back waiting
/// for a later picture; low-latency mode, so the rate controller does not buffer to smooth its
/// output; and a two-second GOP with keyframes on demand, so a client that joins or loses a frame
/// recovers in well under a second instead of waiting for the next scheduled one.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed unsafe class MediaFoundationVideoEncoder : IVideoEncoder
{
    /// <summary>How long a frame waits for the encoder to ask for input before being abandoned.</summary>
    private static readonly TimeSpan InputWait = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Transforms that refused to activate, and when.
    /// <para>
    /// On a hybrid-graphics laptop the discrete GPU's encoder is enumerated but fails
    /// <c>ActivateObject</c> every time, and each attempt costs about half a second. Every encoder
    /// rebuild paid that before reaching the one that works, which put a half-second hole in the
    /// stream on every resolution change. Remembered for a while rather than forever, because
    /// plugging in a monitor can wake that GPU and make it usable.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RefusedActivation = new();

    private static readonly TimeSpan RefusalMemory = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger;
    private readonly Nv12Converter _converter = new();
    private readonly ConcurrentQueue<VideoPacket> _ready = new();
    private readonly ConcurrentQueue<long> _pendingCaptureTimes = new();
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly Lock _gate = new();
    private readonly List<string> _notes = [];

    private IntPtr _transform;
    private IntPtr _codecApi;
    private IntPtr _events;
    private Thread? _pump;
    private volatile bool _stopping;
    private volatile bool _faulted;

    private byte[] _nv12 = [];
    private long _events_seen;
    private long _needInputEvents;
    private long _haveOutputEvents;
    private long _submitted;
    private long _convertTicks;
    private long _waitTicks;
    private long _submitTicks;
    private long _timedFrames;
    private long _startedUnixMs = -1;
    private int _forceKeyframe;
    private bool _disposed;

    private MediaFoundationVideoEncoder(
        IntPtr transform,
        IntPtr codecApi,
        IntPtr events,
        bool isHardware,
        string name,
        VideoEncoderSettings settings,
        ILogger logger)
    {
        _transform = transform;
        _codecApi = codecApi;
        _events = events;
        _logger = logger;

        IsHardware = isHardware;
        BackendName = name;
        Settings = settings;
    }

    public string BackendName { get; }

    public bool IsHardware { get; }

    public VideoEncoderSettings Settings { get; private set; }

    public byte[]? CodecConfig { get; private set; }

    /// <summary>True once the transform has failed in a way that cannot be recovered in place.</summary>
    public bool Faulted => _faulted;

    /// <summary>
    /// What the transform has actually done, for the self-test and the log.
    /// <para>
    /// Counted rather than inferred because the failure this catches is silent: an asynchronous
    /// encoder that never asks for input produces no frames, no error and no exception, and looks
    /// exactly like a desktop nobody is changing.
    /// </para>
    /// </summary>
    public string Diagnostics() =>
        $"events {Interlocked.Read(ref _events_seen)}, need-input {Interlocked.Read(ref _needInputEvents)}, "
        + $"have-output {Interlocked.Read(ref _haveOutputEvents)}, submitted {Interlocked.Read(ref _submitted)}"
        + (_faulted ? ", faulted" : "")
        + Timings()
        + Notes();

    /// <summary>Where an encode call's time goes, averaged: conversion, waiting for the encoder, handing over.</summary>
    public (double ConvertMs, double WaitMs, double SubmitMs) StageTimings()
    {
        var frames = Math.Max(1, Interlocked.Read(ref _timedFrames));
        var toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;

        return (Interlocked.Read(ref _convertTicks) * toMs,
            Interlocked.Read(ref _waitTicks) * toMs,
            Interlocked.Read(ref _submitTicks) * toMs);
    }

    private string Timings()
    {
        var (convert, wait, submit) = StageTimings();
        return $", convert {convert:F1} ms, wait {wait:F1} ms, submit {submit:F1} ms";
    }

    private string Notes()
    {
        lock (_gate)
        {
            return _notes.Count == 0 ? "" : " | " + string.Join("; ", _notes);
        }
    }

    /// <summary>Records something unexpected once. Twenty is plenty to see a pattern.</summary>
    private void Note(string note)
    {
        lock (_gate)
        {
            if (_notes.Count < 20 && !_notes.Contains(note))
            {
                _notes.Add(note);
            }
        }
    }

    public void RequestKeyframe() => Interlocked.Exchange(ref _forceKeyframe, 1);

    /// <summary>
    /// Sets <c>CODECAPI_AVEncCommonMeanBitRate</c> and the ceiling above it on the running transform.
    /// <para>
    /// Only honoured in the rate-control mode <see cref="Configure"/> chooses, which was picked for
    /// exactly this reason — see there. Only the mean is required to succeed: an encoder that refuses
    /// the new ceiling keeps the old one.
    /// </para>
    /// </summary>
    public bool TryChangeBitrate(int bitrateKbps)
    {
        if (_disposed || _faulted || _codecApi == IntPtr.Zero || bitrateKbps <= 0)
        {
            return false;
        }

        var meanKey = CodecApiMeanBitRate;
        var maxKey = CodecApiMaxBitRate;

        if (IsSupported(_codecApi, ref meanKey) != SOk)
        {
            return false;
        }

        var mean = Variant.Ui4((uint)bitrateKbps * 1000);
        var max = Variant.Ui4((uint)bitrateKbps * 1500);
        var hasCeiling = IsSupported(_codecApi, ref maxKey) == SOk;

        // The ceiling moves first when going up and last when going down, so that at no point is the
        // mean asked for above the ceiling in force — which an encoder may refuse, or clamp silently.
        var raising = bitrateKbps > Settings.BitrateKbps;

        if (raising && hasCeiling)
        {
            SetCodecValue(_codecApi, ref maxKey, ref max);
        }

        if (SetCodecValue(_codecApi, ref meanKey, ref mean) < 0)
        {
            return false;
        }

        if (!raising && hasCeiling)
        {
            SetCodecValue(_codecApi, ref maxKey, ref max);
        }

        Settings = Settings with { BitrateKbps = bitrateKbps };
        return true;
    }

    public void Encode(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        long captureUnixMs,
        List<VideoPacket> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_disposed || _faulted)
        {
            return;
        }

        var needed = Nv12Converter.FrameBytes(Settings.Width, Settings.Height);

        if (_nv12.Length < needed)
        {
            _nv12 = new byte[needed];
        }

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _converter.Convert(bgra, width, height, stride, _nv12, Settings.Width, Settings.Height);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Add(ref _convertTicks, t1 - t0);
        Interlocked.Increment(ref _timedFrames);

        if (_startedUnixMs < 0)
        {
            _startedUnixMs = captureUnixMs;
        }

        // Presentation time in 100-nanosecond units, measured from the first frame. Derived from the
        // capture clock rather than a frame counter, so a capture that stalls for half a second
        // produces a gap the encoder's rate control can see rather than a lie about smoothness.
        var pts = Math.Max(0, captureUnixMs - _startedUnixMs) * 10_000L;
        var duration = 10_000_000L / Math.Max(1, Settings.Fps);

        if (Interlocked.Exchange(ref _forceKeyframe, 0) == 1)
        {
            ForceKeyframeNow();
        }

        if (_events != IntPtr.Zero)
        {
            // An asynchronous transform accepts input only when it has asked for it. Waiting a
            // bounded time and then giving up is deliberate: a frame the encoder is too busy to take
            // is a frame that would have been stale by the time it came out.
            var waited = _needInput.Wait(InputWait);
            Interlocked.Add(ref _waitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t1);

            if (!waited)
            {
                Drain(output);
                return;
            }
        }

        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        var submitted = Submit(_nv12.AsSpan(0, needed), pts, duration, captureUnixMs);
        Interlocked.Add(ref _submitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t2);

        if (!submitted)
        {
            return;
        }

        if (_events == IntPtr.Zero)
        {
            DrainSynchronous();
        }

        Drain(output);
    }

    private void Drain(List<VideoPacket> output)
    {
        while (_ready.TryDequeue(out var packet))
        {
            output.Add(packet);
        }
    }

    private bool Submit(ReadOnlySpan<byte> nv12, long pts, long duration, long captureUnixMs)
    {
        var sample = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            // A fresh sample per frame rather than one reused: an asynchronous transform keeps the
            // sample until it has finished with it, and overwriting the buffer underneath it is a
            // torn frame that looks like a decoder bug.
            Check(MFCreateMemoryBuffer((uint)nv12.Length, out buffer), "MFCreateMemoryBuffer");
            Check(LockBuffer(buffer, out var data, out var max, out _), "IMFMediaBuffer::Lock");

            try
            {
                if (max < nv12.Length)
                {
                    return false;
                }

                nv12.CopyTo(new Span<byte>(data, nv12.Length));
            }
            finally
            {
                UnlockBuffer(buffer);
            }

            Check(SetCurrentLength(buffer, (uint)nv12.Length), "IMFMediaBuffer::SetCurrentLength");
            Check(MFCreateSample(out sample), "MFCreateSample");
            Check(AddBuffer(sample, buffer), "IMFSample::AddBuffer");
            Check(SetSampleTime(sample, pts), "IMFSample::SetSampleTime");
            Check(SetSampleDuration(sample, duration), "IMFSample::SetSampleDuration");

            // Queued before the input, not after: with no B-frames the transform emits in the order
            // it was fed, so the output pump can pair each packet with the moment its pixels were
            // taken — which is what makes the latency the phone reports a measurement.
            _pendingCaptureTimes.Enqueue(captureUnixMs);

            var hr = ProcessInput(_transform, 0, sample, 0);

            Interlocked.Increment(ref _submitted);

            if (hr < 0)
            {
                _pendingCaptureTimes.TryDequeue(out _);
                Fault($"ProcessInput returned 0x{hr:X8}");
                return false;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            Fault(ex.Message);
            return false;
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer);
        }
    }

    /// <summary>Pulls everything a synchronous transform is holding. Never called for hardware.</summary>
    private void DrainSynchronous()
    {
        while (true)
        {
            var hr = TryTakeOutput();

            if (hr == MfETransformNeedMoreInput || hr < 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One <c>ProcessOutput</c>. Returns the HRESULT so the two callers can tell "nothing yet" from
    /// "the pipeline is broken" without an exception in the ordinary case.
    /// </summary>
    private int TryTakeOutput()
    {
        MftOutputStreamInfo info;

        var infoHr = GetOutputStreamInfo(_transform, 0, out info);

        if (infoHr < 0)
        {
            Note($"GetOutputStreamInfo 0x{infoHr:X8}");
            return -1;
        }

        Note($"stream info flags=0x{info.Flags:X} size={info.Size} align={info.Alignment}");

        var buffers = stackalloc MftOutputDataBuffer[1];
        buffers[0] = default;

        var provided = IntPtr.Zero;
        var mine = (info.Flags & MftOutputStreamProvidesSamples) == 0;

        if (mine)
        {
            // A transform that does not allocate needs somewhere to put the bitstream. Twice the
            // stated size, because the stated size is the steady-state estimate and a keyframe is
            // several times an average frame.
            var size = Math.Max(info.Size, 128u * 1024u) * 2;

            var made = MFCreateMemoryBuffer(size, out var buffer);

            if (made < 0)
            {
                Note($"MFCreateMemoryBuffer({size}) 0x{made:X8}");
                return -1;
            }

            var madeSample = MFCreateSample(out provided);

            if (madeSample < 0)
            {
                Note($"MFCreateSample 0x{madeSample:X8}");
                Release(ref buffer);
                return -1;
            }

            AddBuffer(provided, buffer);
            Release(ref buffer);
            buffers[0].Sample = provided;
        }

        var hr = ProcessOutput(_transform, 0, 1, buffers, out _);

        try
        {
            if (hr == MfETransformStreamChange)
            {
                // Not an error and not rare: a hardware encoder commonly restates its output type
                // once, after it has seen a frame and filled in its parameter sets. The contract is
                // that the client accepts the new type before asking for output again — and a
                // client that only re-reads it, as the first version of this did, gets one event
                // and then silence for the rest of the stream.
                Note("the encoder restated its output type");
                Renegotiate();
                RefreshCodecConfig();
                return hr;
            }

            if (hr < 0)
            {
                Note($"ProcessOutput 0x{hr:X8}");

                if (hr != MfETransformNeedMoreInput)
                {
                    Fault($"ProcessOutput returned 0x{hr:X8}");
                }

                return hr;
            }

            var sample = buffers[0].Sample;

            if (sample == IntPtr.Zero)
            {
                Note("ProcessOutput produced no sample");
                return hr;
            }

            Note($"ProcessOutput ok hr=0x{hr:X8} status={buffers[0].Status:X}");
            Publish(sample);
            return hr;
        }
        finally
        {
            // When this class supplied the sample, the field still holds it and releasing the field
            // releases it exactly once. When the transform supplied one, the field holds that
            // instead. The only case where both could be live is a failed call that left the field
            // empty, and then the locally created one is the one to let go of.
            var sample = buffers[0].Sample;

            if (sample != IntPtr.Zero)
            {
                Release(ref sample);
            }
            else if (provided != IntPtr.Zero)
            {
                Release(ref provided);
            }

            buffers[0].Sample = IntPtr.Zero;

            var events = buffers[0].Events;
            Release(ref events);
            buffers[0].Events = IntPtr.Zero;
        }
    }

    private void Publish(IntPtr sample)
    {
        var buffer = IntPtr.Zero;

        try
        {
            var contiguous = ConvertToContiguousBuffer(sample, out buffer);

            if (contiguous < 0)
            {
                Note($"ConvertToContiguousBuffer 0x{contiguous:X8}");
                return;
            }

            var locked = LockBuffer(buffer, out var data, out _, out var length);

            if (locked < 0)
            {
                Note($"IMFMediaBuffer::Lock 0x{locked:X8}");
                return;
            }

            byte[] bytes;

            try
            {
                bytes = new Span<byte>(data, (int)length).ToArray();
            }
            finally
            {
                UnlockBuffer(buffer);
            }

            if (bytes.Length == 0)
            {
                Note("an output sample was empty");
                return;
            }

            var cleanPoint = MfSampleExtensionCleanPoint;
            var keyframe = GetUInt32(sample, ref cleanPoint, out var value) >= 0 && value != 0;

            if (CodecConfig is null || keyframe)
            {
                // Some encoders publish the parameter sets on the output type and some only inline
                // them in the first IDR. Taking whichever arrives first means the client is
                // configured either way, and re-reading on each keyframe means a renegotiation that
                // changed them is picked up rather than assumed away.
                var sets = AnnexB.ExtractParameterSets(bytes);

                if (sets is not null)
                {
                    CodecConfig = sets;
                }
                else if (CodecConfig is null)
                {
                    RefreshCodecConfig();
                }
            }

            if (keyframe && CodecConfig is not null && !AnnexB.CarriesParameterSets(bytes))
            {
                // An IDR that does not carry its own SPS and PPS cannot be decoded by a client that
                // joined after the last configuration frame. Prepending them costs a few dozen bytes
                // twice a second and makes every keyframe a valid entry point.
                var merged = new byte[CodecConfig.Length + bytes.Length];
                CodecConfig.CopyTo(merged, 0);
                bytes.CopyTo(merged, CodecConfig.Length);
                bytes = merged;
            }

            if (!_pendingCaptureTimes.TryDequeue(out var captured))
            {
                captured = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            Note($"published {bytes.Length} bytes, key {keyframe}");

            _ready.Enqueue(new VideoPacket(
                bytes,
                keyframe ? VideoPacketFlags.Keyframe : VideoPacketFlags.None,
                captured));
        }
        finally
        {
            Release(ref buffer);
        }
    }

    /// <summary>Accepts whatever output type the transform now wants, so it will carry on.</summary>
    private void Renegotiate()
    {
        var type = IntPtr.Zero;

        try
        {
            var hr = GetOutputAvailableType(_transform, 0, 0, out type);

            if (hr < 0 || type == IntPtr.Zero)
            {
                Note($"GetOutputAvailableType 0x{hr:X8}");
                return;
            }

            var set = SetOutputType(_transform, 0, type, 0);

            if (set < 0)
            {
                Note($"SetOutputType on renegotiation 0x{set:X8}");
            }
        }
        finally
        {
            Release(ref type);
        }
    }

    private void RefreshCodecConfig()
    {
        var type = IntPtr.Zero;

        try
        {
            if (GetOutputCurrentType(_transform, 0, out type) < 0)
            {
                return;
            }

            var key = MfMtMpegSequenceHeader;

            if (GetBlobSize(type, ref key, out var size) < 0 || size == 0 || size > 4096)
            {
                return;
            }

            var blob = new byte[size];

            fixed (byte* target = blob)
            {
                if (GetBlob(type, ref key, target, size, out var written) < 0 || written == 0)
                {
                    return;
                }
            }

            CodecConfig = AnnexB.ExtractParameterSets(blob) ?? blob;
        }
        finally
        {
            Release(ref type);
        }
    }

    private void ForceKeyframeNow()
    {
        if (_codecApi == IntPtr.Zero)
        {
            return;
        }

        var key = CodecApiForceKeyFrame;
        var value = Variant.Ui4(1);

        // Best effort by design. An encoder that refuses still produces the scheduled keyframe at
        // the end of the GOP, so a refusal is a delay of at most a GOP rather than a broken stream.
        SetCodecValue(_codecApi, ref key, ref value);
    }

    /// <summary>
    /// Reads the transform's event queue for as long as the encoder lives.
    /// <para>
    /// Only an asynchronous transform has one. Both sides of the conversation happen here: a request
    /// for input releases the semaphore <see cref="Encode"/> waits on, and an announcement of output
    /// is collected immediately so the packet is queued the moment the hardware has it.
    /// </para>
    /// </summary>
    private void PumpEvents()
    {
        CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);

        while (!_stopping)
        {
            var mediaEvent = IntPtr.Zero;

            try
            {
                var hr = GetEvent(_events, 0, out mediaEvent);

                if (hr < 0)
                {
                    if (!_stopping && hr != MfEShutdown)
                    {
                        Fault($"the encoder event queue ended (0x{hr:X8})");
                    }

                    return;
                }

                if (GetEventType(mediaEvent, out var type) < 0)
                {
                    continue;
                }

                Interlocked.Increment(ref _events_seen);
                Note($"event {type}");

                switch (type)
                {
                    case MeTransformNeedInput:
                        Interlocked.Increment(ref _needInputEvents);
                        _needInput.Release();
                        break;

                    case MeTransformHaveOutput:
                        Interlocked.Increment(ref _haveOutputEvents);
                        TryTakeOutput();
                        break;

                    case MeError:
                        Fault("the encoder reported an error");
                        return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or SEHException)
            {
                Fault(ex.Message);
                return;
            }
            finally
            {
                Release(ref mediaEvent);
            }
        }
    }

    private void Fault(string reason)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        _logger.LogWarning("The H.264 encoder stopped working ({Reason}); the stream will fall back.", reason);
    }

    // --- Construction -------------------------------------------------------------------------------

    /// <summary>
    /// Builds an encoder on the best transform this machine offers, or returns null.
    /// </summary>
    internal static MediaFoundationVideoEncoder? TryCreate(VideoEncoderSettings settings, ILogger logger)
    {
        MediaFoundationPlatform.Acquire();

        var attempts = new (uint Flags, string What)[]
        {
            (MftEnumFlagHardware | MftEnumFlagAsyncMft | MftEnumFlagSortAndFilter, "hardware"),
            (MftEnumFlagSyncMft | MftEnumFlagSortAndFilter, "software"),
        };

        foreach (var (flags, what) in attempts)
        {
            var encoder = TryCreate(settings, flags, what == "hardware", logger);

            if (encoder is not null)
            {
                return encoder;
            }
        }

        MediaFoundationPlatform.Release();
        return null;
    }

    /// <summary>
    /// Every H.264 transform this machine registers, with the enumeration flags that found it.
    /// <para>
    /// Purely diagnostic, and worth its own method: "no encoder was found" is the same message
    /// whether the machine genuinely has none, the enumeration filter was too narrow, or Media
    /// Foundation failed to start — and those need three different fixes.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Inventory()
    {
        var found = new List<string>();

        try
        {
            MediaFoundationPlatform.Acquire();
        }
        catch (InvalidOperationException ex)
        {
            return [$"Media Foundation did not start: {ex.Message}"];
        }

        try
        {
            var combinations = new (string Label, uint Flags, bool WithInputFilter)[]
            {
                ("hardware, NV12 in", MftEnumFlagHardware | MftEnumFlagAsyncMft | MftEnumFlagSortAndFilter, true),
                ("hardware, any in", MftEnumFlagHardware | MftEnumFlagAsyncMft | MftEnumFlagSortAndFilter, false),
                ("software, NV12 in", MftEnumFlagSyncMft | MftEnumFlagSortAndFilter, true),
                ("software, any in", MftEnumFlagSyncMft | MftEnumFlagSortAndFilter, false),
                ("everything", MftEnumFlagSyncMft | MftEnumFlagAsyncMft | MftEnumFlagHardware, false),
            };

            foreach (var (label, flags, withInput) in combinations)
            {
                var output = new MftRegisterTypeInfo { MajorType = MfMediaTypeVideo, Subtype = MfVideoFormatH264 };
                var input = new MftRegisterTypeInfo { MajorType = MfMediaTypeVideo, Subtype = MfVideoFormatNv12 };

                IntPtr* activates = null;
                uint count = 0;

                var hr = MFTEnumEx(
                    MftCategoryVideoEncoder, flags, withInput ? &input : null, &output, out activates, out count);

                if (hr < 0)
                {
                    found.Add($"{label}: MFTEnumEx returned 0x{hr:X8}");
                    continue;
                }

                if (count == 0)
                {
                    found.Add($"{label}: none");
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        found.Add($"{label}: {FriendlyName(activates[i])}");
                        var activate = activates[i];
                        Release(ref activate);
                    }
                }

                if (activates is not null)
                {
                    CoTaskMemFree((IntPtr)activates);
                }
            }
        }
        finally
        {
            MediaFoundationPlatform.Release();
        }

        return found;
    }

    private static MediaFoundationVideoEncoder? TryCreate(
        VideoEncoderSettings settings,
        uint flags,
        bool hardware,
        ILogger logger)
    {
        IntPtr* activates = null;
        uint count = 0;

        var output = new MftRegisterTypeInfo { MajorType = MfMediaTypeVideo, Subtype = MfVideoFormatH264 };
        var input = new MftRegisterTypeInfo { MajorType = MfMediaTypeVideo, Subtype = MfVideoFormatNv12 };

        var hr = MFTEnumEx(MftCategoryVideoEncoder, flags, &input, &output, out activates, out count);

        if (hr < 0 || count == 0)
        {
            if (activates is not null)
            {
                CoTaskMemFree((IntPtr)activates);
            }

            return null;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var encoder = TryActivate(activates[i], settings, hardware, logger);

                if (encoder is not null)
                {
                    return encoder;
                }
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                var activate = activates[i];
                Release(ref activate);
            }

            CoTaskMemFree((IntPtr)activates);
        }

        return null;
    }

    private static MediaFoundationVideoEncoder? TryActivate(
        IntPtr activate,
        VideoEncoderSettings settings,
        bool hardware,
        ILogger logger)
    {
        var transform = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var codecApi = IntPtr.Zero;
        var events = IntPtr.Zero;
        var outputType = IntPtr.Zero;
        var inputType = IntPtr.Zero;

        try
        {
            var name = FriendlyName(activate);

            if (RefusedActivation.TryGetValue(name, out var refusedAt)
                && DateTimeOffset.UtcNow - refusedAt < RefusalMemory)
            {
                return null;
            }

            var iid = IidMfTransform;

            var activated = ActivateObject(activate, ref iid, out transform);

            if (activated < 0 || transform == IntPtr.Zero)
            {
                RefusedActivation[name] = DateTimeOffset.UtcNow;
                logger.LogInformation(
                    "{Name}: ActivateObject failed (0x{Hr:X8}); not trying it again for ten minutes.", name, activated);
                return null;
            }

            var isAsync = false;

            if (GetTransformAttributes(transform, out attributes) >= 0 && attributes != IntPtr.Zero)
            {
                var asyncKey = MfTransformAsync;

                if (GetUInt32(attributes, ref asyncKey, out var async) >= 0 && async != 0)
                {
                    isAsync = true;

                    // A hardware transform starts locked and refuses everything until this is set.
                    // The lock exists so that a caller has to say it understands the asynchronous
                    // model before it gets one, which this class does.
                    var unlock = MfTransformAsyncUnlock;

                    var unlocked = SetUInt32(attributes, ref unlock, 1);

                    if (unlocked < 0)
                    {
                        logger.LogInformation("{Name}: MF_TRANSFORM_ASYNC_UNLOCK failed (0x{Hr:X8}).", name, unlocked);
                        return null;
                    }
                }

                var lowLatency = MfLowLatency;
                SetUInt32(attributes, ref lowLatency, 1);
            }

            // Output before input, and not the other way round: an encoder derives what it will
            // accept from what it has been told to produce, so setting the input first is refused.
            outputType = BuildOutputType(settings);
            var outputHr = SetOutputType(transform, 0, outputType, 0);

            if (outputHr < 0)
            {
                logger.LogInformation("{Name}: SetOutputType failed (0x{Hr:X8}).", name, outputHr);
                return null;
            }

            inputType = BuildInputType(settings);
            var inputHr = SetInputType(transform, 0, inputType, 0);

            if (inputHr < 0)
            {
                logger.LogInformation("{Name}: SetInputType failed (0x{Hr:X8}).", name, inputHr);
                return null;
            }

            var codecIid = IidCodecApi;

            if (QueryInterface(transform, ref codecIid, out codecApi) < 0)
            {
                codecApi = IntPtr.Zero;
            }

            Configure(codecApi, settings);

            if (isAsync)
            {
                var generatorIid = IidMfMediaEventGenerator;

                var generator = QueryInterface(transform, ref generatorIid, out events);

                if (generator < 0 || events == IntPtr.Zero)
                {
                    logger.LogInformation("{Name}: no event generator (0x{Hr:X8}).", name, generator);
                    return null;
                }
            }

            var begin = ProcessMessage(transform, MftMessageNotifyBeginStreaming, IntPtr.Zero);
            var start = ProcessMessage(transform, MftMessageNotifyStartOfStream, IntPtr.Zero);

            if (begin < 0 || start < 0)
            {
                logger.LogInformation(
                    "{Name}: streaming messages refused (begin 0x{Begin:X8}, start 0x{Start:X8}).", name, begin, start);
                return null;
            }

            var encoder = new MediaFoundationVideoEncoder(
                transform, codecApi, events, hardware, name, settings, logger);

            encoder.RefreshCodecConfig();

            if (isAsync)
            {
                encoder._pump = new Thread(encoder.PumpEvents)
                {
                    IsBackground = true,
                    Name = "nexus-h264-events",
                };

                encoder._pump.Start();
            }

            // Ownership has moved to the encoder; the local handles must not be released below.
            transform = IntPtr.Zero;
            codecApi = IntPtr.Zero;
            events = IntPtr.Zero;

            logger.LogInformation(
                "H.264 encoder: {Name} ({Kind}, {Mode}) at {Width}x{Height} {Fps} fps, {Bitrate} kbit/s.",
                name,
                hardware ? "hardware" : "software",
                isAsync ? "asynchronous" : "synchronous",
                settings.Width,
                settings.Height,
                settings.Fps,
                settings.BitrateKbps);

            return encoder;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SEHException)
        {
            logger.LogDebug("An H.264 transform could not be prepared: {Reason}", ex.Message);
            return null;
        }
        finally
        {
            Release(ref outputType);
            Release(ref inputType);
            Release(ref attributes);
            Release(ref events);
            Release(ref codecApi);
            Release(ref transform);
        }
    }

    private static IntPtr BuildOutputType(VideoEncoderSettings settings)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");

        var major = MfMtMajorType;
        var video = MfMediaTypeVideo;
        var subtypeKey = MfMtSubtype;
        var h264 = MfVideoFormatH264;

        Check(SetGuid(type, ref major, ref video), "MF_MT_MAJOR_TYPE");
        Check(SetGuid(type, ref subtypeKey, ref h264), "MF_MT_SUBTYPE");

        var bitrate = MfMtAvgBitrate;
        Check(SetUInt32(type, ref bitrate, (uint)Math.Max(1, settings.BitrateKbps) * 1000), "MF_MT_AVG_BITRATE");

        var size = MfMtFrameSize;
        Check(SetUInt64(type, ref size, Pack((uint)settings.Width, (uint)settings.Height)), "MF_MT_FRAME_SIZE");

        var rate = MfMtFrameRate;
        Check(SetUInt64(type, ref rate, Pack((uint)Math.Max(1, settings.Fps), 1)), "MF_MT_FRAME_RATE");

        var interlace = MfMtInterlaceMode;
        Check(SetUInt32(type, ref interlace, InterlaceModeProgressive), "MF_MT_INTERLACE_MODE");

        var aspect = MfMtPixelAspectRatio;
        Check(SetUInt64(type, ref aspect, Pack(1, 1)), "MF_MT_PIXEL_ASPECT_RATIO");

        var profile = MfMtMpeg2Profile;
        SetUInt32(type, ref profile, AvcProfileMain);

        var matrix = MfMtYuvMatrix;
        SetUInt32(type, ref matrix, Nv12Converter.UsesBt709(settings.Height) ? YuvMatrixBt709 : YuvMatrixBt601);

        var range = MfMtVideoNominalRange;
        SetUInt32(type, ref range, NominalRange16To235);

        return type;
    }

    private static IntPtr BuildInputType(VideoEncoderSettings settings)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");

        var major = MfMtMajorType;
        var video = MfMediaTypeVideo;
        var subtypeKey = MfMtSubtype;
        var nv12 = MfVideoFormatNv12;

        Check(SetGuid(type, ref major, ref video), "MF_MT_MAJOR_TYPE");
        Check(SetGuid(type, ref subtypeKey, ref nv12), "MF_MT_SUBTYPE");

        var size = MfMtFrameSize;
        Check(SetUInt64(type, ref size, Pack((uint)settings.Width, (uint)settings.Height)), "MF_MT_FRAME_SIZE");

        var rate = MfMtFrameRate;
        Check(SetUInt64(type, ref rate, Pack((uint)Math.Max(1, settings.Fps), 1)), "MF_MT_FRAME_RATE");

        var interlace = MfMtInterlaceMode;
        Check(SetUInt32(type, ref interlace, InterlaceModeProgressive), "MF_MT_INTERLACE_MODE");

        var aspect = MfMtPixelAspectRatio;
        Check(SetUInt64(type, ref aspect, Pack(1, 1)), "MF_MT_PIXEL_ASPECT_RATIO");

        // Stated rather than inferred: the converter writes tightly packed top-down rows, and an
        // encoder that guessed bottom-up would produce a stream that is upside down and otherwise
        // perfect, which is the kind of bug that survives a long way.
        var stride = MfMtDefaultStride;
        SetUInt32(type, ref stride, (uint)settings.Width);

        var matrix = MfMtYuvMatrix;
        SetUInt32(type, ref matrix, Nv12Converter.UsesBt709(settings.Height) ? YuvMatrixBt709 : YuvMatrixBt601);

        var range = MfMtVideoNominalRange;
        SetUInt32(type, ref range, NominalRange16To235);

        return type;
    }

    /// <summary>
    /// The low-latency settings, every one of them best effort.
    /// <para>
    /// Encoders disagree about which of these they implement, and a refusal is not a failure: the
    /// defaults are workable and the alternative is refusing to stream at all on a machine whose
    /// encoder happens not to expose a GOP setting.
    /// </para>
    /// </summary>
    private static void Configure(IntPtr codecApi, VideoEncoderSettings settings)
    {
        if (codecApi == IntPtr.Zero)
        {
            return;
        }

        // Peak-constrained VBR rather than CBR, and chosen by measurement rather than by name. The
        // adaptive controller changes the bitrate of a running encoder, and Intel's Quick Sync
        // transform accepts a new mean in CBR — and low-delay VBR — and then ignores it: built at four
        // megabits and changed to one, it went on producing 3.8 while a new encoder built at one
        // produced 0.94. In peak-constrained VBR the same change is followed within a second. The
        // ceiling below keeps a busy frame from spending far past the link's budget, which is what
        // CBR was for; `--selftest-video --bitrate-change` repeats the measurement on any machine.
        Set(CodecApiRateControlMode, Variant.Ui4(RateControlPeakConstrainedVbr));
        Set(CodecApiMeanBitRate, Variant.Ui4((uint)Math.Max(1, settings.BitrateKbps) * 1000));

        // A ceiling a little above the average, so a busy frame can spend more without the next
        // second being starved to pay for it.
        Set(CodecApiMaxBitRate, Variant.Ui4((uint)Math.Max(1, settings.BitrateKbps) * 1500));

        // No B-frames. A B-frame is coded from a picture that has not been sent yet, so the encoder
        // has to hold a frame back — which is exactly the latency this whole pass exists to remove.
        Set(CodecApiBPictureCount, Variant.Ui4(0));

        Set(CodecApiGopSize, Variant.Ui4((uint)Math.Max(1, settings.Fps * settings.GopSeconds)));
        Set(CodecApiLowLatency, Variant.Bool(true));
        Set(CodecApiRealTime, Variant.Bool(true));

        // Halfway between speed and quality: at the top the software encoder cannot keep up with
        // 1080p30, and at the bottom text edges visibly smear.
        Set(CodecApiQualityVsSpeed, Variant.Ui4(50));

        void Set(Guid api, Variant value)
        {
            var key = api;

            if (IsSupported(codecApi, ref key) == SOk)
            {
                SetCodecValue(codecApi, ref key, ref value);
            }
        }
    }

    private static string FriendlyName(IntPtr activate)
    {
        var key = MftFriendlyNameAttribute;

        if (GetAllocatedString(activate, ref key, out var pointer, out var length) < 0 || pointer == IntPtr.Zero)
        {
            return "unnamed encoder";
        }

        try
        {
            return Marshal.PtrToStringUni(pointer, (int)length) ?? "unnamed encoder";
        }
        finally
        {
            CoTaskMemFree(pointer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stopping = true;

        // The pump is parked in a blocking GetEvent. Posting a harmless event of our own wakes it
        // while every pointer it might still touch is valid — releasing the generator underneath it
        // would wake it too, into a use-after-free.
        if (_events != IntPtr.Zero && _pump is not null)
        {
            var nothing = Guid.Empty;
            QueueEvent(_events, 0, ref nothing, SOk, IntPtr.Zero);
        }

        var pumpFinished = _pump is null || _pump.Join(TimeSpan.FromSeconds(2));
        _pump = null;

        if (!pumpFinished)
        {
            // The event thread is wedged inside the driver. Releasing the transform now would pull
            // memory out from under it, so the handles are deliberately leaked instead: a few
            // kilobytes held by a process that is usually on its way out, against an access
            // violation that would take the whole of PC Core with it.
            _logger.LogWarning("The H.264 encoder's event thread did not stop; its handles are being left alone.");
            _needInput.Dispose();
            _ready.Clear();
            _pendingCaptureTimes.Clear();
            return;
        }

        if (_transform != IntPtr.Zero)
        {
            // Flush rather than drain: a drain asks the encoder to finish frames nobody is going to
            // look at, and the stream this belonged to has already stopped.
            ProcessMessage(_transform, MftMessageCommandFlush, IntPtr.Zero);
            ProcessMessage(_transform, MftMessageNotifyEndOfStream, IntPtr.Zero);
            ProcessMessage(_transform, MftMessageNotifyEndStreaming, IntPtr.Zero);
        }

        Release(ref _events);
        Release(ref _codecApi);
        Release(ref _transform);

        _needInput.Dispose();
        _ready.Clear();
        _pendingCaptureTimes.Clear();

        MediaFoundationPlatform.Release();
    }
}

/// <summary>Finds out once whether this machine can encode H.264, and makes encoders when it can.</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class MediaFoundationVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly ILogger<MediaFoundationVideoEncoderFactory> _logger;
    private readonly Lock _gate = new();
    private readonly List<string> _notes = [];

    private string? _description;
    private bool? _available;

    public MediaFoundationVideoEncoderFactory(ILogger<MediaFoundationVideoEncoderFactory> logger) => _logger = logger;

    public bool IsAvailable
    {
        get
        {
            Probe();
            return _available == true;
        }
    }

    public string Describe()
    {
        Probe();
        return _description ?? "No H.264 encoder was found.";
    }

    /// <summary>What Media Foundation reports, verbatim. For the self-test and for a bug report.</summary>
    public IReadOnlyList<string> Inventory() => MediaFoundationVideoEncoder.Inventory();

    public IVideoEncoder Create(VideoEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (width, height) = VideoEncoderSettings.Even(settings.Width, settings.Height);
        var normalised = settings with { Width = width, Height = height };

        var encoder = MediaFoundationVideoEncoder.TryCreate(normalised, _logger)
            ?? throw new NotSupportedException("This PC has no usable H.264 encoder.");

        lock (_gate)
        {
            _available = true;
            _description = $"{encoder.BackendName} ({(encoder.IsHardware ? "hardware" : "software")})";
        }

        return encoder;
    }

    /// <summary>
    /// Builds and throws away a small encoder to find out what is here.
    /// <para>
    /// Done once, at the smallest size anything will ever be asked for, because the alternative is
    /// telling the owner "video streaming is available" and finding out otherwise when they press
    /// the button.
    /// </para>
    /// </summary>
    private void Probe()
    {
        lock (_gate)
        {
            if (_available is not null)
            {
                return;
            }

            try
            {
                using var encoder = MediaFoundationVideoEncoder.TryCreate(
                    new VideoEncoderSettings(640, 360, 30, 800, 2), _logger);

                _available = encoder is not null;
                _description = encoder is null
                    ? "No H.264 encoder was found on this PC."
                    : $"{encoder.BackendName} ({(encoder.IsHardware ? "hardware" : "software")})";
            }
            catch (Exception ex) when (ex is InvalidOperationException or SEHException or DllNotFoundException)
            {
                _available = false;
                _description = $"H.264 encoding is unavailable: {ex.Message}";
                _logger.LogWarning("Probing for an H.264 encoder failed: {Reason}", ex.Message);
            }

            _logger.LogInformation("Video encoding: {Description}", _description);
        }
    }
}
