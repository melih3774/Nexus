using System.Buffers.Binary;

namespace PcCore.Protocol;

/// <summary>How the payload bytes are encoded.</summary>
public enum FrameEncoding : byte
{
    /// <summary>Reserved: a header with no image, used to signal stream state.</summary>
    None = 0,

    /// <summary>Baseline JPEG. See SPEC.md 51 for why this rather than a video codec.</summary>
    Jpeg = 1,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,

    /// <summary>
    /// The cursor is already drawn into the payload. The client must not draw its own, or the
    /// pointer appears twice a few pixels apart.
    /// </summary>
    CursorBakedIn = 1,

    /// <summary>A complete image rather than a partial update. Every v0.6 frame is one.</summary>
    Complete = 2,
}

/// <summary>
/// The fixed 64-byte header in front of every streamed desktop frame.
/// <para>
/// <b>Why binary at all.</b> The typed JSON envelope carries commands, responses and events, and it
/// stays exactly as it is. Frames are different: a 60 KB JPEG becomes 80 KB of base64 inside a JSON
/// string, has to be escaped, parsed and unescaped, and arrives fifteen times a second. That is a
/// steady tax on both sides for no benefit, so frames travel as binary WebSocket messages on the
/// same authenticated connection and nothing else changes.
/// </para>
/// <para>
/// <b>Why a fixed header rather than a media container.</b> Everything a client needs to interpret
/// one frame — which stream, which display, how big, where on the desktop, when, and where the
/// cursor was — fits in 64 bytes. A container would add framing rules, timestamps in another clock
/// domain, and a parser worth attacking, to carry the same facts.
/// </para>
/// <para>
/// All fields are little-endian, matching both x86 Windows and every Android device in practice.
/// The layout is pinned by shared vectors in <c>shared/protocol/protocol-vectors.json</c>.
/// </para>
/// </summary>
public readonly record struct StreamFrameHeader
{
    /// <summary>Bytes 0-3. Lets a client reject anything that is not a Nexus frame immediately.</summary>
    public static ReadOnlySpan<byte> Magic => "NXS1"u8;

    public const int Size = 64;

    /// <summary>Bumped only for an incompatible layout change; a client refuses versions it does not know.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>
    /// An upper bound on one encoded frame. A 4K JPEG at high quality is well under this; anything
    /// larger is a bug or a hostile peer, and either way is refused before allocation.
    /// </summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public required byte Version { get; init; }
    public required FrameEncoding Encoding { get; init; }
    public required FrameFlags Flags { get; init; }

    /// <summary>Which display this frame is of. Matches <c>DisplayInfo.Index</c>.</summary>
    public required byte DisplayIndex { get; init; }

    /// <summary>Identifies the stream, so frames from a previous stream are recognisably stale.</summary>
    public required uint StreamId { get; init; }

    /// <summary>Monotonic within a stream. Gaps are normal — stale frames are dropped by design.</summary>
    public required ulong Sequence { get; init; }

    /// <summary>Capture time, Unix milliseconds UTC. For latency diagnostics, not for ordering.</summary>
    public required long TimestampUnixMs { get; init; }

    /// <summary>Size of the encoded image, which may be smaller than the display when downscaled.</summary>
    public required ushort Width { get; init; }

    public required ushort Height { get; init; }

    /// <summary>The display's rectangle in Windows virtual-desktop coordinates.</summary>
    public required int DesktopLeft { get; init; }

    public required int DesktopTop { get; init; }
    public required int DesktopWidth { get; init; }
    public required int DesktopHeight { get; init; }

    /// <summary>Cursor position relative to this display's top-left, in desktop pixels.</summary>
    public required int CursorX { get; init; }

    public required int CursorY { get; init; }

    public required bool CursorVisible { get; init; }

    public required int PayloadLength { get; init; }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"header needs {Size} bytes", nameof(destination));
        }

        destination[..Size].Clear();

        Magic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = (byte)Encoding;
        destination[6] = (byte)Flags;
        destination[7] = DisplayIndex;

        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], StreamId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[20..], TimestampUnixMs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[28..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[30..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..], DesktopLeft);
        BinaryPrimitives.WriteInt32LittleEndian(destination[36..], DesktopTop);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..], DesktopWidth);
        BinaryPrimitives.WriteInt32LittleEndian(destination[44..], DesktopHeight);
        BinaryPrimitives.WriteInt32LittleEndian(destination[48..], CursorX);
        BinaryPrimitives.WriteInt32LittleEndian(destination[52..], CursorY);

        destination[56] = CursorVisible ? (byte)1 : (byte)0;

        // 57-59 reserved, already zeroed above.
        BinaryPrimitives.WriteInt32LittleEndian(destination[60..], PayloadLength);
    }

    /// <summary>
    /// Parses a header. Returns false for anything malformed rather than throwing: this runs on
    /// bytes from the network, and a bad frame is an ordinary event.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out StreamFrameHeader header)
    {
        header = default;

        if (source.Length < Size || !source[..4].SequenceEqual(Magic))
        {
            return false;
        }

        var version = source[4];
        if (version != CurrentVersion)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[60..]);
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            return false;
        }

        var encoding = source[5];
        if (encoding > (byte)FrameEncoding.Jpeg)
        {
            return false;
        }

        header = new StreamFrameHeader
        {
            Version = version,
            Encoding = (FrameEncoding)encoding,
            Flags = (FrameFlags)source[6],
            DisplayIndex = source[7],
            StreamId = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            Sequence = BinaryPrimitives.ReadUInt64LittleEndian(source[12..]),
            TimestampUnixMs = BinaryPrimitives.ReadInt64LittleEndian(source[20..]),
            Width = BinaryPrimitives.ReadUInt16LittleEndian(source[28..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(source[30..]),
            DesktopLeft = BinaryPrimitives.ReadInt32LittleEndian(source[32..]),
            DesktopTop = BinaryPrimitives.ReadInt32LittleEndian(source[36..]),
            DesktopWidth = BinaryPrimitives.ReadInt32LittleEndian(source[40..]),
            DesktopHeight = BinaryPrimitives.ReadInt32LittleEndian(source[44..]),
            CursorX = BinaryPrimitives.ReadInt32LittleEndian(source[48..]),
            CursorY = BinaryPrimitives.ReadInt32LittleEndian(source[52..]),
            CursorVisible = source[56] != 0,
            PayloadLength = payloadLength,
        };

        return true;
    }

    /// <summary>Header plus payload in one buffer, ready to hand to the transport.</summary>
    public byte[] ToMessage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new ArgumentException(
                $"payload is {payload.Length} bytes but the header states {PayloadLength}", nameof(payload));
        }

        var message = new byte[Size + payload.Length];
        WriteTo(message);
        payload.CopyTo(message.AsSpan(Size));

        return message;
    }
}
