using PcCore.Windows.Capture;

using Xunit;

namespace PcCore.Tests.Screen;

/// <summary>
/// How Desktop Duplication's three pointer shapes get painted onto a frame.
/// <para>
/// The reported symptom was "the pointer is visible, but over text it disappears". That is one
/// specific shape failing: the I-beam is a <see cref="PointerShapeType.Monochrome"/> AND/XOR mask
/// with no colour of its own, and the previous code drew every cursor with <c>DrawIconEx</c>, which
/// cannot express "invert what is underneath". These tests pin the four monochrome combinations
/// directly, because that is where the bug lived and inspection is not evidence.
/// </para>
/// </summary>
public class PointerShapeTests
{
    private const int Stride = 4 * 4; // four BGRA pixels

    /// <summary>A four-pixel frame, every channel set to <paramref name="value"/>.</summary>
    private static byte[] Frame(byte value)
    {
        var pixels = new byte[Stride];
        Array.Fill(pixels, value);
        return pixels;
    }

    /// <summary>
    /// A one-row monochrome shape four pixels wide, exercising all four AND/XOR combinations:
    /// x=0 black, x=1 white, x=2 transparent, x=3 invert.
    /// </summary>
    private static PointerShape Monochrome() => new(
        PointerShapeType.Monochrome,
        width: 4,
        height: 2, // one AND row above one XOR row
        pitch: 1,
        hotspotX: 0,
        hotspotY: 0,
        bytes: [0b0011_0000, 0b0101_0000]);

    [Fact]
    public void MonochromeAppliesAllFourAndXorCombinations()
    {
        var pixels = Frame(0x80);

        Monochrome().DrawInto(pixels, frameWidth: 4, frameHeight: 1, stride: Stride, left: 0, top: 0);

        Assert.Equal(0x00, pixels[0]);  // AND 0, XOR 0 -> black
        Assert.Equal(0xFF, pixels[4]);  // AND 0, XOR 1 -> white
        Assert.Equal(0x80, pixels[8]);  // AND 1, XOR 0 -> transparent, untouched
        Assert.Equal(0x7F, pixels[12]); // AND 1, XOR 1 -> inverted
    }

    /// <summary>
    /// The actual regression, stated as the two cases that matter: an inverting cursor must be
    /// visible over a white page <em>and</em> over black text. A shape that only ever painted its
    /// own colour would vanish into one of them.
    /// </summary>
    [Theory]
    [InlineData(0xFF, 0x00)] // white page -> the I-beam goes black
    [InlineData(0x00, 0xFF)] // black text -> the same I-beam goes white
    public void AnInvertingPointerStaysVisibleOverAnyBackground(byte background, byte expected)
    {
        var pixels = Frame(background);

        Monochrome().DrawInto(pixels, frameWidth: 4, frameHeight: 1, stride: Stride, left: 0, top: 0);

        // x=3 is the inverting pixel.
        Assert.Equal(expected, pixels[12]);
        Assert.Equal(expected, pixels[13]);
        Assert.Equal(expected, pixels[14]);

        // And it is opaque, so an encoder that reads alpha sees a complete image.
        Assert.Equal(0xFF, pixels[15]);
    }

    [Fact]
    public void ColourShapesBlendOnStraightAlpha()
    {
        // One pixel: pure blue at half alpha over a black frame.
        var shape = new PointerShape(
            PointerShapeType.Color,
            width: 1,
            height: 1,
            pitch: 4,
            hotspotX: 0,
            hotspotY: 0,
            bytes: [0xFF, 0x00, 0x00, 0x80]);

        var pixels = Frame(0x00);

        shape.DrawInto(pixels, frameWidth: 4, frameHeight: 1, stride: Stride, left: 0, top: 0);

        // (0xFF * 0x80 + 0 * 0x7F) / 255
        Assert.Equal(128, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(0, pixels[2]);
    }

    [Fact]
    public void ColourShapesAreFullyOpaqueWhereAlphaIsMaximum()
    {
        var shape = new PointerShape(
            PointerShapeType.Color, 1, 1, 4, 0, 0, [0x11, 0x22, 0x33, 0xFF]);

        var pixels = Frame(0x00);

        shape.DrawInto(pixels, 4, 1, Stride, 0, 0);

        Assert.Equal(0x11, pixels[0]);
        Assert.Equal(0x22, pixels[1]);
        Assert.Equal(0x33, pixels[2]);
    }

    /// <summary>
    /// In a masked-colour shape the alpha byte is a two-valued mask rather than a blend factor:
    /// zero means paint, anything else means XOR into the desktop.
    /// </summary>
    [Fact]
    public void MaskedColourPaintsWhereTheMaskIsZeroAndXorsWhereItIsNot()
    {
        var paint = new PointerShape(
            PointerShapeType.MaskedColor, 1, 1, 4, 0, 0, [0x10, 0x20, 0x30, 0x00]);

        var xor = new PointerShape(
            PointerShapeType.MaskedColor, 1, 1, 4, 0, 0, [0x0F, 0x0F, 0x0F, 0xFF]);

        var painted = Frame(0xAA);
        paint.DrawInto(painted, 4, 1, Stride, 0, 0);

        Assert.Equal(0x10, painted[0]);
        Assert.Equal(0x20, painted[1]);
        Assert.Equal(0x30, painted[2]);

        var xored = Frame(0xAA);
        xor.DrawInto(xored, 4, 1, Stride, 0, 0);

        Assert.Equal(0xAA ^ 0x0F, xored[0]);
    }

    /// <summary>
    /// A pointer near an edge is clipped rather than wrapped or thrown. A cursor at the bottom-right
    /// of the screen is the ordinary case, not an exotic one.
    /// </summary>
    [Fact]
    public void ShapesAreClippedToTheFrameRatherThanWrapping()
    {
        var pixels = Frame(0x80);

        // Two pixels off the left edge and one row above the top.
        Monochrome().DrawInto(pixels, frameWidth: 4, frameHeight: 1, stride: Stride, left: -2, top: -1);

        // Nothing was drawn: the only visible row is above the frame.
        Assert.All(pixels, b => Assert.Equal(0x80, b));

        // And a shape hanging off the right edge touches only what is inside.
        var clipped = Frame(0x80);
        Monochrome().DrawInto(clipped, frameWidth: 4, frameHeight: 1, stride: Stride, left: 3, top: 0);

        // x=3 receives the shape's x=0, which is black.
        Assert.Equal(0x00, clipped[12]);
    }

    /// <summary>
    /// A shape whose header claims more than its buffer holds is dropped. A frame without a pointer
    /// is a cosmetic loss; reading past the end of a buffer is not.
    /// </summary>
    // The shape type travels as its numeric value because PointerShapeType is internal and a
    // public test method cannot name it: 2 is Color, 1 is Monochrome.
    [Theory]
    [InlineData(2, 8, 8, 4)]   // pitch far too small for 8 pixels
    [InlineData(1, 64, 2, 1)]  // pitch*8 < width
    [InlineData(2, 0, 1, 4)]   // no width
    public void ImplausibleShapesAreRefused(int type, int width, int height, int pitch)
    {
        var shape = new PointerShape(
            (PointerShapeType)type, width, height, pitch, 0, 0, [0, 0, 0, 0]);

        Assert.False(shape.IsUsable);

        // And drawing one changes nothing rather than throwing.
        var pixels = Frame(0x80);
        shape.DrawInto(pixels, 4, 1, Stride, 0, 0);
        Assert.All(pixels, b => Assert.Equal(0x80, b));
    }

    [Fact]
    public void AMonochromeShapeDrawsHalfItsBufferHeight()
    {
        Assert.Equal(1, Monochrome().VisibleHeight);
        Assert.Equal(2, Monochrome().Height);
    }
}
