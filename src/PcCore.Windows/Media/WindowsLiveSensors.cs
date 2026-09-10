using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;

using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage.Streams;

using WinRT;

namespace PcCore.Windows.Media;

/// <summary>
/// The webcam, as a live JPEG stream.
/// <para>
/// <b>Why repeated stills rather than a video encode.</b> The desktop stream already established
/// that a sequence of JPEGs is the right shape for this system — no codec state, no keyframe
/// dependency, every frame independently decodable, and a dropped frame costs one frame. A camera
/// view for monitoring a room needs to be current far more than it needs to be smooth, and
/// <c>MediaCapture</c> gives a JPEG directly without a second encoder in the process.
/// </para>
/// <para>
/// <b>Windows' own privacy machinery is untouched and relied upon.</b> This goes through
/// <c>MediaCapture</c> like any other application, so the camera privacy setting still applies, the
/// hardware indicator light still comes on, and the camera appears in Windows' "recently used"
/// list. Nexus adds its own indicator on top of that rather than in place of it — if Windows says
/// no, this fails, and that is correct.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsLiveCamera : ILiveCameraSource
{
    /// <summary>
    /// The ceiling on how often a frame is requested, not a promise of eight a second.
    /// <para>
    /// The loop is capture-bound rather than delay-bound on real hardware: this machine's webcam
    /// takes about 800 ms per still through the photo pipeline even with the capture prepared once
    /// and taken from the preview pin, so the measured rate is closer to 1.2 frames a second. The
    /// interval still matters on a camera that is faster, and it keeps a quick camera from spending
    /// a home uplink on frames nobody asked for.
    /// </para>
    /// </summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(125);

    private readonly ILogger<WindowsLiveCamera> _logger;

    public WindowsLiveCamera(ILogger<WindowsLiveCamera> logger) => _logger = logger;

    public string Name => "camera";

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await DeviceInformation
                .FindAllAsync(DeviceClass.VideoCapture)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enumerating cameras failed.");
            return false;
        }
    }

    public async IAsyncEnumerable<LiveFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        MediaCapture? capture = null;

        try
        {
            capture = new MediaCapture();

            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,

                // VideoPreview rather than Auto: Auto lets Windows use the camera's dedicated photo
                // pin, which means a capture-mode switch per shot. Measured on this machine that was
                // about 850 ms a frame - roughly one frame a second against the eight this asks for.
                // The preview pin is already streaming, so a frame is just the next one.
                PhotoCaptureSource = PhotoCaptureSource.VideoPreview,
            }).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The usual causes are another application holding the camera and the Windows camera
            // privacy setting being off. Both are the system working; neither is worth a stack trace.
            _logger.LogWarning("The camera could not be opened for live monitoring: {Reason}", ex.Message);
            capture?.Dispose();
            yield break;
        }

        // 640x480 is what a monitoring view needs. Asking for more would cost bandwidth to show the
        // same room.
        var encoding = ImageEncodingProperties.CreateJpeg();
        encoding.Width = 640;
        encoding.Height = 480;

        LowLagPhotoCapture? photo = null;

        try
        {
            // LowLagPhotoCapture rather than CapturePhotoToStreamAsync, measured rather than assumed:
            // the one-shot call tears the capture pipeline down and rebuilds it every time, which on
            // this machine's webcam cost about 850 ms a frame - roughly one frame a second, where the
            // interval below asks for eight. Preparing once and firing repeatedly keeps the pipeline
            // warm, which is exactly what this API exists for.
            photo = await capture.PrepareLowLagPhotoCaptureAsync(encoding)
                .AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The camera could not be prepared for live capture: {Reason}", ex.Message);
            capture.Dispose();
            yield break;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = Task.Delay(FrameInterval, cancellationToken);
                LiveFrame? frame = null;

                try
                {
                    var captured = await photo.CaptureAsync().AsTask(cancellationToken).ConfigureAwait(false);

                    // CapturedFrame is itself a random-access stream, so it is read directly rather
                    // than copied into another one first.
                    using var stream = captured.Frame;
                    var size = (uint)stream.Size;

                    if (size > 0)
                    {
                        var bytes = new byte[size];

                        // DataReader rather than the buffer extensions: WindowsRuntimeBufferExtensions
                        // is not available on modern .NET, and this is the same path the evidence
                        // capture already uses to get bytes out of a WinRT stream.
                        using var reader = new DataReader(stream.GetInputStreamAt(0));
                        await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(false);
                        reader.ReadBytes(bytes);

                        frame = new LiveFrame(bytes, 640, 480);
                    }
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (Exception ex)
                {
                    // One bad frame is not a reason to end a live view; the camera may be
                    // re-focusing or another application may have taken it briefly.
                    _logger.LogDebug(ex, "A live camera frame was dropped.");
                }

                if (frame is not null)
                {
                    yield return frame;
                }

                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
        finally
        {
            // Finished and disposed promptly, so the camera light goes out when the owner stops the
            // stream rather than whenever a finaliser runs.
            try
            {
                await photo.FinishAsync().AsTask(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The low-lag photo capture did not finish cleanly.");
            }

            capture.Dispose();
            _logger.LogInformation("The live camera was released.");
        }
    }
}

/// <summary>
/// The microphone, as a live PCM stream.
/// <para>
/// <b>Why <c>AudioGraph</c> rather than the recording API used for evidence.</b> Evidence records a
/// bounded clip to a finished MP4 file, which is exactly wrong here: a container is only playable
/// once it is finalised, so a "live" stream built that way would arrive in complete clips seconds
/// behind. <c>AudioGraph</c> with a frame output node hands over raw samples as they are captured,
/// which is what live means.
/// </para>
/// <para>
/// Sent as signed 16-bit mono at the graph's own rate. Mono because this is a room monitor and
/// stereo would double the bytes to carry the same information; 16-bit PCM because it is what
/// Android's <c>AudioTrack</c> plays without a decoder.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsLiveMicrophone : ILiveAudioSource
{
    private readonly ILogger<WindowsLiveMicrophone> _logger;

    public WindowsLiveMicrophone(ILogger<WindowsLiveMicrophone> logger) => _logger = logger;

    public string Name => "microphone";

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await DeviceInformation
                .FindAllAsync(DeviceClass.AudioCapture)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enumerating microphones failed.");
            return false;
        }
    }

    public async IAsyncEnumerable<LiveFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AudioGraph? graph = null;
        AudioDeviceInputNode? input = null;
        AudioFrameOutputNode? output = null;

        // Bounded and dropping the oldest: audio that has fallen behind is worse than absent, and a
        // growing buffer turns a live listen into a delayed one.
        var frames = Channel.CreateBounded<LiveFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Other)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault,
            };

            var created = await AudioGraph.CreateAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);

            if (created.Status != AudioGraphCreationStatus.Success)
            {
                _logger.LogWarning("The audio graph could not be created: {Status}", created.Status);
                yield break;
            }

            graph = created.Graph;

            var inputResult = await graph.CreateDeviceInputNodeAsync(MediaCategory.Other)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Most often the microphone privacy setting, which is Windows doing its job.
                _logger.LogWarning(
                    "The microphone could not be opened for live monitoring: {Status}", inputResult.Status);
                yield break;
            }

            input = inputResult.DeviceInputNode;
            output = graph.CreateFrameOutputNode();
            input.AddOutgoingConnection(output);

            var sampleRate = (int)graph.EncodingProperties.SampleRate;

            // The device decides this, not the graph's nominal shape: a laptop array microphone
            // reports several channels, and reading its quantum as mono sent seven times the samples
            // at seven times the pitch. Measured here at 15360 bytes a quantum where mono would be
            // 1920.
            var channels = (int)Math.Max(1, input.EncodingProperties.ChannelCount);
            var node = output;

            if (channels > 1)
            {
                _logger.LogInformation(
                    "The microphone reports {Channels} channels; they are mixed down to mono.", channels);
            }

            graph.QuantumStarted += (_, _) =>
            {
                try
                {
                    var pcm = ReadQuantum(node, channels, out var samples);

                    if (samples > 0)
                    {
                        frames.Writer.TryWrite(new LiveFrame(pcm, sampleRate, 1));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "A live audio quantum was dropped.");
                }
            };

            graph.Start();
            _logger.LogInformation("Live microphone started at {Rate} Hz, mono.", sampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live microphone capture could not start.");
            output?.Dispose();
            input?.Dispose();
            graph?.Dispose();
            yield break;
        }

        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            try
            {
                graph.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping the audio graph failed.");
            }

            output?.Dispose();
            input?.Dispose();
            graph.Dispose();

            _logger.LogInformation("The live microphone was released.");
        }
    }

    /// <summary>
    /// Takes one quantum from the graph and converts it to signed 16-bit mono.
    /// <para>
    /// <c>AudioGraph</c> works in 32-bit float; Android's <c>AudioTrack</c> takes 16-bit PCM without
    /// a decoder. Converting here rather than on the phone means the conversion happens once, on the
    /// machine that has the samples already, and halves what goes over the link.
    /// </para>
    /// <para>
    /// <b>Two wrong ways got here first, and both failed silently.</b> Casting the buffer reference
    /// to <c>IMemoryBufferByteAccess</c> the way every UWP sample does throws
    /// <c>InvalidCastException</c> under CsWinRT, because the object is a <c>WinRT.IInspectable</c>
    /// projection rather than a raw COM pointer. Copying it with
    /// <c>Buffer.CreateCopyFromMemoryBuffer</c> instead returns a zero-length buffer, because an
    /// <c>AudioBuffer</c> from a capture quantum reports <c>Length</c> 0 with a real
    /// <c>Capacity</c> — measured here at 15360 bytes against a copy of 0. Both produce a graph that
    /// runs, a microphone light that comes on, and not one sample delivered.
    /// </para>
    /// <para>
    /// <c>WinRT.CastExtensions.As&lt;T&gt;</c> is the supported route: a genuine
    /// <c>QueryInterface</c> through the projection, which is what the cast was only pretending to
    /// be.
    /// </para>
    /// </summary>
    private static unsafe byte[] ReadQuantum(AudioFrameOutputNode node, int channels, out int samples)
    {
        samples = 0;

        using var frame = node.GetFrame();
        using var buffer = frame.LockBuffer(global::Windows.Media.AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out var capacity);

        if (capacity == 0 || data is null)
        {
            return [];
        }

        var floats = (float*)data;
        var total = (int)(capacity / sizeof(float));
        var frames = total / channels;
        var pcm = new byte[frames * sizeof(short)];

        for (var i = 0; i < frames; i++)
        {
            // Averaged rather than taking the first channel: on an array microphone the channels are
            // separate elements, and picking one throws away most of the room.
            var sum = 0f;

            for (var c = 0; c < channels; c++)
            {
                sum += floats[(i * channels) + c];
            }

            // Clamped before scaling: a graph can hand back values outside -1..1 and wrapping them
            // is what turns a loud noise into a burst of static.
            var sample = Math.Clamp(sum / channels, -1f, 1f);
            var value = (short)(sample * short.MaxValue);

            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        samples = frames;
        return pcm;
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}
