using System.Buffers.Binary;

namespace PcCore.Protocol;

/// <summary>Which sensor a live frame came from.</summary>
public enum LiveSensorKind : byte
{
    None = 0,

    /// <summary>The webcam. The payload's encoding is stated separately.</summary>
    Camera = 1,

    /// <summary>Signed 16-bit little-endian PCM from the microphone.</summary>
    Microphone = 2,
}

/// <summary>
/// The fixed 32-byte header in front of every live camera or microphone frame.
/// <para>
/// <b>Separate from <see cref="StreamFrameHeader"/> on purpose.</b> A desktop frame carries display
/// bounds and a cursor position; a microphone frame carries a sample rate. Widening the desktop
/// header to hold both would mean every 60 KB screen frame paid for audio fields it never uses, and
/// a client would have to read a discriminator before it could trust any of it. Two small headers
/// with two magics is cheaper and impossible to confuse.
/// </para>
/// <para>
/// <b>Why live sensor data is not evidence.</b> Nothing here is written to disk by PC Core. These
/// frames exist for as long as it takes to put them on a socket, which is the difference between
/// monitoring and recording, and it is a difference the design keeps rather than a policy that could
/// be flipped.
/// </para>
/// <para>
/// All fields are little-endian, matching <see cref="StreamFrameHeader"/>.
/// </para>
/// </summary>
public readonly record struct LiveSensorFrameHeader
{
    /// <summary>Bytes 0-3. A different magic from the desktop stream, so the two cannot be mixed up.</summary>
    public static ReadOnlySpan<byte> Magic => "NXL1"u8;

    public const int Size = 32;

    public const byte CurrentVersion = 1;

    public required byte Version { get; init; }

    public required LiveSensorKind Kind { get; init; }

    /// <summary>
    /// How the payload is encoded, in two bytes that were previously reserved zeroes.
    /// <para>
    /// Widening the header rather than adding a sensor kind for each codec. "Which device" and "in
    /// what format" are two different questions, and folding them into one enum means every future
    /// codec doubles the kinds — and means a client switching on the kind to decide where to route a
    /// frame has to know about codecs to do it.
    /// </para>
    /// <para>
    /// Zero keeps its old meaning exactly: JPEG for the camera, signed 16-bit PCM for the
    /// microphone — and zero is also what a default-constructed header has, so an omission reads as
    /// the old behaviour rather than as a broken frame.
    /// </para>
    /// </summary>
    public FrameEncoding Encoding { get; init; }

    /// <summary>Keyframe and codec-config flags, meaningful only for a video-encoded payload.</summary>
    public FrameFlags Flags { get; init; }

    /// <summary>Monotonic per sensor, so a client can notice a gap.</summary>
    public required ulong Sequence { get; init; }

    public required long TimestampUnixMs { get; init; }

    /// <summary>Camera: pixel width. Microphone: samples per second.</summary>
    public required int WidthOrSampleRate { get; init; }

    /// <summary>Camera: pixel height. Microphone: channel count.</summary>
    public required int HeightOrChannels { get; init; }

    public required int PayloadLength { get; init; }

    /// <summary>Header and payload as one binary WebSocket message.</summary>
    public byte[] ToMessage(ReadOnlySpan<byte> payload)
    {
        var message = new byte[Size + payload.Length];
        var span = message.AsSpan();

        Magic.CopyTo(span);
        span[4] = Version;
        span[5] = (byte)Kind;
        span[6] = (byte)Encoding;
        span[7] = (byte)Flags;

        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], TimestampUnixMs);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], WidthOrSampleRate);

        // Height and channels share a field; the payload length is derivable from the message but
        // written anyway so a reader can validate rather than assume.
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], (short)HeightOrChannels);
        BinaryPrimitives.WriteInt16LittleEndian(span[30..], (short)Math.Min(payload.Length, short.MaxValue));

        payload.CopyTo(span[Size..]);
        return message;
    }

    /// <summary>Reads a header, or null when the bytes are not a live sensor frame.</summary>
    public static LiveSensorFrameHeader? TryRead(ReadOnlySpan<byte> message)
    {
        if (message.Length < Size || !message[..4].SequenceEqual(Magic))
        {
            return null;
        }

        var version = message[4];

        if (version != CurrentVersion)
        {
            return null;
        }

        var kind = (LiveSensorKind)message[5];

        if (kind is not (LiveSensorKind.Camera or LiveSensorKind.Microphone))
        {
            return null;
        }

        var encoding = (FrameEncoding)message[6];

        if (encoding > FrameEncoding.H264)
        {
            return null;
        }

        return new LiveSensorFrameHeader
        {
            Version = version,
            Kind = kind,
            Encoding = encoding,
            Flags = (FrameFlags)message[7],
            Sequence = BinaryPrimitives.ReadUInt64LittleEndian(message[8..]),
            TimestampUnixMs = BinaryPrimitives.ReadInt64LittleEndian(message[16..]),
            WidthOrSampleRate = BinaryPrimitives.ReadInt32LittleEndian(message[24..]),
            HeightOrChannels = BinaryPrimitives.ReadInt16LittleEndian(message[28..]),
            PayloadLength = message.Length - Size,
        };
    }
}
