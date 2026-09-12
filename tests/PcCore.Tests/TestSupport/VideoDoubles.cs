using PcCore.Capabilities.Media;
using PcCore.Capabilities.Storage;

namespace PcCore.Tests.TestSupport;

/// <summary>
/// An encoder that behaves like H.264 without being it.
/// <para>
/// The properties that matter to everything above the codec are structural, not visual: a keyframe
/// carries its own parameter sets and can be decoded alone, a predicted frame cannot, the encoder
/// has a pipeline so an input need not produce an output, and parameter sets appear once and then
/// stay the same. All of that is reproducible in a few lines, and reproducing it is what lets the
/// stream service, the backpressure policy and the transport be tested on a machine with no GPU —
/// including on a build agent, where the real encoder does not exist at all.
/// </para>
/// <para>
/// The bytes are real Annex-B framing with plausible NAL headers, so <see cref="AnnexB"/> and the
/// client-side parser can be exercised against them.
/// </para>
/// </summary>
public sealed class FakeVideoEncoder : IVideoEncoder
{
    private readonly int _pipelineDelay;
    private readonly Queue<long> _pending = new();

    private bool _wantsKeyframe = true;
    private int _frames;

    public FakeVideoEncoder(VideoEncoderSettings settings, int pipelineDelay = 0, bool acceptsBitrateChanges = true)
    {
        Settings = settings;
        _pipelineDelay = pipelineDelay;
        AcceptsBitrateChanges = acceptsBitrateChanges;
    }

    public string BackendName => "fake H.264";

    public bool IsHardware => false;

    public VideoEncoderSettings Settings { get; private set; }

    public byte[]? CodecConfig { get; private set; }

    public int Disposals { get; private set; }

    public int KeyframeRequests { get; private set; }

    /// <summary>False models an encoder whose rate cannot be changed while it runs.</summary>
    public bool AcceptsBitrateChanges { get; }

    /// <summary>Every bitrate this encoder was switched to in place.</summary>
    public List<int> BitrateChanges { get; } = [];

    public void RequestKeyframe()
    {
        KeyframeRequests++;
        _wantsKeyframe = true;
    }

    public bool TryChangeBitrate(int bitrateKbps)
    {
        if (!AcceptsBitrateChanges)
        {
            return false;
        }

        BitrateChanges.Add(bitrateKbps);
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

        _pending.Enqueue(captureUnixMs);

        // A hardware encoder holds the first frames while its pipeline fills. Modelled so that
        // callers cannot quietly assume one frame in means one frame out.
        if (_pending.Count <= _pipelineDelay)
        {
            return;
        }

        var captured = _pending.Dequeue();
        var keyframe = _wantsKeyframe;
        _wantsKeyframe = false;
        _frames++;

        CodecConfig ??= [0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80];

        var slice = new byte[]
        {
            0, 0, 0, 1,
            keyframe ? (byte)0x65 : (byte)0x41,
            (byte)(_frames & 0xFF),
            (byte)(width & 0xFF),
            (byte)(height & 0xFF),
        };

        var bytes = keyframe ? [.. CodecConfig, .. slice] : slice;

        output.Add(new VideoPacket(
            bytes,
            keyframe ? VideoPacketFlags.Keyframe : VideoPacketFlags.None,
            captured));
    }

    public void Dispose() => Disposals++;
}

/// <summary>A factory that can be told whether this "machine" has a video encoder.</summary>
public sealed class FakeVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly int _pipelineDelay;
    private readonly bool _acceptsBitrateChanges;

    public FakeVideoEncoderFactory(bool available = true, int pipelineDelay = 0, bool acceptsBitrateChanges = true)
    {
        IsAvailable = available;
        _pipelineDelay = pipelineDelay;
        _acceptsBitrateChanges = acceptsBitrateChanges;
    }

    /// <summary>
    /// When set, creation waits for it — standing in for a hardware transform's start-up, for exactly as
    /// long as a test needs rather than for a fixed time a loaded machine may not honour.
    /// </summary>
    public ManualResetEventSlim? CreationGate { get; set; }

    public bool IsAvailable { get; set; }

    /// <summary>Every encoder this has made, so a test can assert one was replaced rather than leaked.</summary>
    public List<FakeVideoEncoder> Created { get; } = [];

    public string Describe() => IsAvailable ? "fake H.264 (software)" : "no encoder in this test";

    public IVideoEncoder Create(VideoEncoderSettings settings)
    {
        if (!IsAvailable)
        {
            throw new NotSupportedException("This test has no video encoder.");
        }

        CreationGate?.Wait(TimeSpan.FromSeconds(30));

        var encoder = new FakeVideoEncoder(settings, _pipelineDelay, _acceptsBitrateChanges);

        lock (Created)
        {
            Created.Add(encoder);
        }

        return encoder;
    }
}

/// <summary>Source sizes a test can state outright, standing in for a display and a webcam.</summary>
public sealed class FakeMediaSourceSizes : IMediaSourceSizes
{
    public (int Width, int Height) Screen { get; set; } = (1920, 1080);

    public (int Width, int Height) Camera { get; set; } = (1280, 720);

    public (int Width, int Height) ScreenSize() => Screen;

    public (int Width, int Height) CameraSize() => Camera;
}

/// <summary>A quality store on a throwaway directory, so a test never reads the owner's real settings.</summary>
public static class TestMediaQuality
{
    public static MediaQualityStore Store(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "nexus-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        return new MediaQualityStore(new StateDirectory(directory), TestLog.For<MediaQualityStore>());
    }

    public static MediaQualityStore Store() => Store(out _);
}
