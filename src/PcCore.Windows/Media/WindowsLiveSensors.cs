using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;

using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;

using WinRT;

namespace PcCore.Windows.Media;

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
