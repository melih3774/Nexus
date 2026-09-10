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
    /// Frames per second. Eight is a deliberate compromise: enough to see someone move through a
    /// room, low enough that a JPEG per frame over a tunnel stays well inside a home uplink.
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
                PhotoCaptureSource = PhotoCaptureSource.Auto,
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

        try
        {
            // 640x480 is what a monitoring view needs. Asking for more would cost bandwidth to show
            // the same room.
            var encoding = ImageEncodingProperties.CreateJpeg();
            encoding.Width = 640;
            encoding.Height = 480;

            while (!cancellationToken.IsCancellationRequested)
            {
                var next = Task.Delay(FrameInterval, cancellationToken);
                LiveFrame? frame = null;

                try
                {
                    using var stream = new InMemoryRandomAccessStream();

                    await capture.CapturePhotoToStreamAsync(encoding, stream)
                        .AsTask(cancellationToken).ConfigureAwait(false);

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
            // Disposed promptly so the camera light goes out when the owner stops the stream rather
            // than whenever a finaliser runs.
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
            var node = output;

            graph.QuantumStarted += (_, _) =>
            {
                try
                {
                    var pcm = ReadQuantum(node, out var samples);

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
    /// </summary>
    private static unsafe byte[] ReadQuantum(AudioFrameOutputNode node, out int samples)
    {
        samples = 0;

        using var frame = node.GetFrame();
        using var buffer = frame.LockBuffer(global::Windows.Media.AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out var capacity);

        if (capacity == 0)
        {
            return [];
        }

        var floats = (float*)data;
        var count = (int)(capacity / sizeof(float));
        var pcm = new byte[count * sizeof(short)];

        for (var i = 0; i < count; i++)
        {
            // Clamped before scaling: a graph can hand back values outside -1..1 and wrapping them
            // is what turns a loud noise into a burst of static.
            var sample = Math.Clamp(floats[i], -1f, 1f);
            var value = (short)(sample * short.MaxValue);

            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        samples = count;
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
