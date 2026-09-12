namespace PcCore.Capabilities.Media;

/// <summary>What an encoder has been asked to produce.</summary>
/// <param name="Width">Encoded width in pixels. Always even; H.264 has no odd dimensions in NV12.</param>
/// <param name="Height">Encoded height in pixels. Always even.</param>
/// <param name="Fps">Frames per second the capture loop paces to, and the rate the encoder is told.</param>
/// <param name="BitrateKbps">Target average bitrate. The rate controller aims at this, not a cap.</param>
/// <param name="GopSeconds">
/// How often an IDR appears without anybody asking. Short enough that a client joining or recovering
/// never waits long, long enough that keyframes are not most of the bitrate.
/// </param>
public sealed record VideoEncoderSettings(int Width, int Height, int Fps, int BitrateKbps, int GopSeconds)
{
    /// <summary>Rounds a requested size to something an H.264 encoder will accept.</summary>
    public static (int Width, int Height) Even(int width, int height) =>
        (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
}

[Flags]
public enum VideoPacketFlags : byte
{
    None = 0,

    /// <summary>An IDR. A client may start decoding here and nothing earlier is needed.</summary>
    Keyframe = 1,

    /// <summary>
    /// Parameter sets only — SPS and PPS, no picture. Sent once when a stream starts or its shape
    /// changes, so a decoder can be configured before the first real frame arrives.
    /// </summary>
    CodecConfig = 2,
}

/// <summary>One encoded access unit on its way to the wire.</summary>
/// <param name="Bytes">Annex-B: NAL units separated by start codes, exactly as a decoder wants them.</param>
/// <param name="CaptureUnixMs">
/// When the pixels were captured, not when they finished encoding. This is what makes the
/// capture-to-render latency the client reports a real measurement rather than a transport timing.
/// </param>
public sealed record VideoPacket(byte[] Bytes, VideoPacketFlags Flags, long CaptureUnixMs);

/// <summary>
/// A live video encoder.
/// <para>
/// <b>Why this exists as an interface and JPEG did not need one.</b> A still encoder is a function:
/// bytes in, bytes out, no memory between calls. A video encoder is a state machine — the
/// difference between this frame and the last is the whole point — so it owns a lifetime, it has to
/// be told when to restart with an IDR, and it can answer with no packet at all while its pipeline
/// fills. Every one of those is a thing the capture loop has to cope with, and a thing a test has to
/// be able to simulate without a GPU.
/// </para>
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Which encoder this actually is. Logged and reported, so "hardware" is never a guess.</summary>
    string BackendName { get; }

    bool IsHardware { get; }

    VideoEncoderSettings Settings { get; }

    /// <summary>
    /// Annex-B SPS and PPS, once the encoder has produced them. Null until the first frame is
    /// encoded on encoders that only publish parameter sets with their first output.
    /// </summary>
    byte[]? CodecConfig { get; }

    /// <summary>
    /// Asks for an IDR on the next frame.
    /// <para>
    /// Called when a client connects, when it reports a gap it cannot decode past, and whenever a
    /// packet had to be dropped — because after a dropped P-frame every later frame decodes into
    /// garbage, and only an IDR ends that.
    /// </para>
    /// </summary>
    void RequestKeyframe();

    /// <summary>
    /// Changes the target bitrate of the running encoder, without a new keyframe or a gap.
    /// <para>
    /// The adaptive controller's first step, and most of its later ones, change only the bitrate.
    /// Rebuilding the encoder for that costs up to a second with no pictures and then the largest
    /// frame there is — a stall that looks exactly like the congestion being reacted to, and was
    /// read as such. Returns false when the encoder cannot change it in place, and the caller
    /// rebuilds instead.
    /// </para>
    /// </summary>
    bool TryChangeBitrate(int bitrateKbps);

    /// <summary>
    /// Encodes one captured frame, appending whatever packets came out.
    /// <para>
    /// Zero packets is normal and not an error: a hardware encoder has a pipeline, and the first
    /// frame or two go in before anything comes out.
    /// </para>
    /// </summary>
    /// <param name="bgra">Source pixels, <paramref name="stride"/> bytes per row.</param>
    void Encode(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        long captureUnixMs,
        List<VideoPacket> output);
}

/// <summary>
/// Makes encoders, and knows when it cannot.
/// <para>
/// Separate from the encoder so the capture loop can ask "is there a video path on this machine"
/// before it commits to one, and fall back to still frames without having to catch an exception
/// from a half-built pipeline.
/// </para>
/// </summary>
public interface IVideoEncoderFactory
{
    /// <summary>True when this machine has an H.264 encoder at all.</summary>
    bool IsAvailable { get; }

    /// <summary>A one-line description of what would be used, for logs and for the owner's UI.</summary>
    string Describe();

    /// <exception cref="NotSupportedException">When no encoder could be created.</exception>
    IVideoEncoder Create(VideoEncoderSettings settings);
}

/// <summary>A factory on a machine with no encoder. Streaming falls back to still frames.</summary>
public sealed class UnavailableVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly string _reason;

    public UnavailableVideoEncoderFactory(string reason) => _reason = reason;

    public bool IsAvailable => false;

    public string Describe() => _reason;

    public IVideoEncoder Create(VideoEncoderSettings settings) => throw new NotSupportedException(_reason);
}

/// <summary>
/// Splits an Annex-B byte stream into NAL units.
/// <para>
/// Needed in two places for two different reasons — pulling SPS and PPS out of an encoder's first
/// output, and deciding whether an IDR already carries its own parameter sets — so it lives here
/// rather than inside the Windows encoder, where no test could reach it. The rules are small and
/// exact, and getting them wrong produces a stream that decodes on one phone and not another.
/// </para>
/// </summary>
public static class AnnexB
{
    /// <summary>NAL unit type 7.</summary>
    public const int SequenceParameterSet = 7;

    /// <summary>NAL unit type 8.</summary>
    public const int PictureParameterSet = 8;

    /// <summary>NAL unit type 5.</summary>
    public const int IdrSlice = 5;

    /// <summary>
    /// Walks the NAL units in an Annex-B buffer, reporting each one's type and its full extent
    /// including the start code. Malformed input yields nothing rather than throwing.
    /// </summary>
    public static IEnumerable<(int Type, int Offset, int Length)> Units(byte[] stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var from = 0;

        while (true)
        {
            var start = NextStartCode(stream, from, out var headerLength);

            if (start < 0)
            {
                yield break;
            }

            var payload = start + headerLength;

            if (payload >= stream.Length)
            {
                yield break;
            }

            var next = NextStartCode(stream, payload, out _);
            var end = next < 0 ? stream.Length : next;

            yield return (stream[payload] & 0x1F, start, end - start);

            if (next < 0)
            {
                yield break;
            }

            from = next;
        }
    }

    /// <summary>True when the buffer opens with parameter sets, so nothing needs prepending.</summary>
    public static bool CarriesParameterSets(byte[] stream)
    {
        foreach (var (type, _, _) in Units(stream))
        {
            if (type == SequenceParameterSet)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>SPS and PPS only, concatenated in Annex-B form, or null when the stream has none.</summary>
    public static byte[]? ExtractParameterSets(byte[] stream)
    {
        var kept = new List<(int Offset, int Length)>();

        foreach (var (type, offset, length) in Units(stream))
        {
            if (type is SequenceParameterSet or PictureParameterSet)
            {
                kept.Add((offset, length));
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        var total = kept.Sum(k => k.Length);
        var result = new byte[total];
        var at = 0;

        foreach (var (offset, length) in kept)
        {
            Array.Copy(stream, offset, result, at, length);
            at += length;
        }

        return result;
    }

    private static int NextStartCode(byte[] stream, int from, out int headerLength)
    {
        headerLength = 0;

        for (var i = Math.Max(0, from); i + 2 < stream.Length; i++)
        {
            if (stream[i] != 0 || stream[i + 1] != 0)
            {
                continue;
            }

            if (stream[i + 2] == 1)
            {
                headerLength = 3;
                return i;
            }

            if (i + 3 < stream.Length && stream[i + 2] == 0 && stream[i + 3] == 1)
            {
                headerLength = 4;
                return i;
            }
        }

        return -1;
    }
}
