using PcCore.Protocol;
using Xunit;

namespace PcCore.Tests.Screen;

/// <summary>
/// The binary frame header, which two languages have to agree on byte for byte.
/// <para>
/// The JSON envelope has a parser on each side that will complain when something is wrong. A fixed
/// binary layout has no such thing: a field written at the wrong offset produces a frame that parses
/// perfectly and means something else. These tests pin the layout itself, not just the round trip.
/// </para>
/// </summary>
public sealed class StreamFrameHeaderTests
{
    private static StreamFrameHeader Sample(int payloadLength = 3) => new()
    {
        Version = StreamFrameHeader.CurrentVersion,
        Encoding = FrameEncoding.Jpeg,
        Flags = FrameFlags.Complete | FrameFlags.CursorBakedIn,
        DisplayIndex = 2,
        StreamId = 0x11223344,
        Sequence = 0x0102030405060708,
        TimestampUnixMs = 1_788_000_000_123,
        Width = 1280,
        Height = 720,
        DesktopLeft = -1920,
        DesktopTop = -100,
        DesktopWidth = 1920,
        DesktopHeight = 1080,
        CursorX = 640,
        CursorY = 360,
        CursorVisible = true,
        PayloadLength = payloadLength,
    };

    [Fact]
    public void AHeaderRoundTrips()
    {
        var header = Sample();
        var buffer = new byte[StreamFrameHeader.Size];

        header.WriteTo(buffer);

        Assert.True(StreamFrameHeader.TryParse(buffer, out var parsed));
        Assert.Equal(header, parsed);
    }

    [Fact]
    public void TheLayoutIsExactlyWhereTheOtherSideExpectsIt()
    {
        // Field offsets, asserted individually. A single-field shift is the failure mode that a
        // round-trip test cannot see, because both halves shift together.
        var buffer = new byte[StreamFrameHeader.Size];
        Sample().WriteTo(buffer);

        Assert.Equal((byte)'N', buffer[0]);
        Assert.Equal((byte)'X', buffer[1]);
        Assert.Equal((byte)'S', buffer[2]);
        Assert.Equal((byte)'1', buffer[3]);

        Assert.Equal(1, buffer[4]);                                        // version
        Assert.Equal((byte)FrameEncoding.Jpeg, buffer[5]);                 // encoding
        Assert.Equal((byte)(FrameFlags.Complete | FrameFlags.CursorBakedIn), buffer[6]);
        Assert.Equal(2, buffer[7]);                                        // display index

        Assert.Equal(0x11223344u, BitConverter.ToUInt32(buffer, 8));
        Assert.Equal(0x0102030405060708ul, BitConverter.ToUInt64(buffer, 12));
        Assert.Equal(1_788_000_000_123L, BitConverter.ToInt64(buffer, 20));
        Assert.Equal((ushort)1280, BitConverter.ToUInt16(buffer, 28));
        Assert.Equal((ushort)720, BitConverter.ToUInt16(buffer, 30));
        Assert.Equal(-1920, BitConverter.ToInt32(buffer, 32));
        Assert.Equal(-100, BitConverter.ToInt32(buffer, 36));
        Assert.Equal(1920, BitConverter.ToInt32(buffer, 40));
        Assert.Equal(1080, BitConverter.ToInt32(buffer, 44));
        Assert.Equal(640, BitConverter.ToInt32(buffer, 48));
        Assert.Equal(360, BitConverter.ToInt32(buffer, 52));
        Assert.Equal(1, buffer[56]);
        Assert.Equal(3, BitConverter.ToInt32(buffer, 60));

        Assert.Equal(64, StreamFrameHeader.Size);
    }

    [Fact]
    public void NegativeDesktopOriginsSurvive()
    {
        // A second monitor to the left of the primary has a negative origin, and getting that wrong
        // would send every click on that display to the wrong place.
        var header = Sample() with { DesktopLeft = -3840, DesktopTop = -2160, CursorX = -1, CursorY = -1 };
        var buffer = new byte[StreamFrameHeader.Size];

        header.WriteTo(buffer);
        Assert.True(StreamFrameHeader.TryParse(buffer, out var parsed));

        Assert.Equal(-3840, parsed.DesktopLeft);
        Assert.Equal(-2160, parsed.DesktopTop);
        Assert.Equal(-1, parsed.CursorX);
    }

    [Fact]
    public void ReservedBytesAreZeroed()
    {
        // Written into a dirty buffer on purpose: the header must clear what it does not use, or a
        // future field would start life holding whatever was there before.
        var buffer = new byte[StreamFrameHeader.Size];
        Array.Fill(buffer, (byte)0xEE);

        Sample().WriteTo(buffer);

        Assert.Equal(0, buffer[57]);
        Assert.Equal(0, buffer[58]);
        Assert.Equal(0, buffer[59]);
    }

    [Fact]
    public void AMessageCarriesTheHeaderThenThePayload()
    {
        var message = Sample(4).ToMessage([9, 8, 7, 6]);

        Assert.Equal(StreamFrameHeader.Size + 4, message.Length);
        Assert.True(StreamFrameHeader.TryParse(message, out var parsed));
        Assert.Equal(4, parsed.PayloadLength);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, message[StreamFrameHeader.Size..]);
    }

    [Fact]
    public void APayloadThatDisagreesWithTheHeaderIsRefused() =>
        Assert.Throws<ArgumentException>(() => Sample(4).ToMessage([1, 2, 3]));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    public void ATruncatedHeaderIsRefused(int length) =>
        Assert.False(StreamFrameHeader.TryParse(new byte[length], out _));

    [Fact]
    public void TheWrongMagicIsRefused()
    {
        var buffer = new byte[StreamFrameHeader.Size];
        Sample().WriteTo(buffer);
        buffer[1] = (byte)'Y';

        Assert.False(StreamFrameHeader.TryParse(buffer, out _));
    }

    [Fact]
    public void AVersionThisBuildDoesNotKnowIsRefused()
    {
        // Refused rather than parsed hopefully: a later version may move fields, and a client that
        // guessed would render garbage instead of saying it cannot.
        var buffer = new byte[StreamFrameHeader.Size];
        Sample().WriteTo(buffer);
        buffer[4] = 2;

        Assert.False(StreamFrameHeader.TryParse(buffer, out _));
    }

    [Fact]
    public void AnUnknownEncodingIsRefused()
    {
        var buffer = new byte[StreamFrameHeader.Size];
        Sample().WriteTo(buffer);
        buffer[5] = 99;

        Assert.False(StreamFrameHeader.TryParse(buffer, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(StreamFrameHeader.MaxPayloadBytes + 1)]
    public void AnImpossiblePayloadLengthIsRefused(int length)
    {
        // The value a reader would allocate from. A negative or absurd length must never reach an
        // allocation, on either side of the wire.
        var buffer = new byte[StreamFrameHeader.Size];
        Sample().WriteTo(buffer);
        BitConverter.GetBytes(length).CopyTo(buffer, 60);

        Assert.False(StreamFrameHeader.TryParse(buffer, out _));
    }

    [Fact]
    public void WritingIntoTooSmallABufferThrows() =>
        Assert.Throws<ArgumentException>(() => Sample().WriteTo(new byte[StreamFrameHeader.Size - 1]));
}
