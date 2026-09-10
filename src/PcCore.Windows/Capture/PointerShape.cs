using System.Runtime.Versioning;

namespace PcCore.Windows.Capture;

/// <summary>How Desktop Duplication described a pointer image.</summary>
internal enum PointerShapeType
{
    /// <summary>
    /// 1 bit per pixel, twice as tall as it looks: an AND mask above an XOR mask.
    /// <para>
    /// This is the shape the I-beam arrives as, and it is why forcing every cursor through
    /// <c>DrawIconEx</c> was never going to be right. The four AND/XOR combinations mean black,
    /// white, transparent and <em>invert what is underneath</em> — the last of which is exactly how
    /// a text cursor stays visible over both black text and a white page.
    /// </para>
    /// </summary>
    Monochrome = 1,

    /// <summary>32bpp BGRA with a straight alpha channel. Modern themed cursors.</summary>
    Color = 2,

    /// <summary>
    /// 32bpp BGRA where the alpha byte is a mask rather than a blend: 0 means "paint this colour",
    /// anything else means "XOR this colour into the desktop".
    /// </summary>
    MaskedColor = 4,
}

/// <summary>
/// One pointer image, as Desktop Duplication reported it, and the code that paints it onto a frame.
/// <para>
/// <b>Why the pointer is composited here rather than drawn by Windows.</b> Desktop Duplication
/// deliberately leaves the pointer out of the frame and reports its shape separately, so something
/// has to put it back. The previous code asked Windows for the current cursor and let
/// <c>DrawIconEx</c> draw it, which works for an arrow and fails for the shapes that are not simple
/// images — the I-beam above all, which is a monochrome invert mask with no colour of its own.
/// Handling the three documented shape types directly is both correct and the only version that can
/// also run on the Secure Desktop, where there is no cursor to ask about.
/// </para>
/// <para>
/// Immutable once built, so the capture loop can keep the last one and re-composite it on a
/// pointer-only update without re-reading anything.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PointerShape
{
    private readonly byte[] _bytes;

    public PointerShape(
        PointerShapeType type,
        int width,
        int height,
        int pitch,
        int hotspotX,
        int hotspotY,
        byte[] bytes)
    {
        Type = type;
        Width = width;
        Height = height;
        Pitch = pitch;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        _bytes = bytes;
    }

    public PointerShapeType Type { get; }

    public int Width { get; }

    /// <summary>Rows in the buffer. For <see cref="PointerShapeType.Monochrome"/> that is twice what is drawn.</summary>
    public int Height { get; }

    public int Pitch { get; }

    public int HotspotX { get; }

    public int HotspotY { get; }

    /// <summary>Rows actually painted.</summary>
    public int VisibleHeight => Type == PointerShapeType.Monochrome ? Height / 2 : Height;

    /// <summary>
    /// True when the buffer is big enough for what the header claims. A shape that fails this is
    /// dropped rather than drawn: a frame with no pointer is a small cosmetic loss, and reading past
    /// the end of a buffer is not.
    /// </summary>
    public bool IsUsable =>
        Width > 0
        && Height > 0
        && Pitch > 0
        && VisibleHeight > 0
        && (long)Pitch * Height <= _bytes.Length
        && (Type == PointerShapeType.Monochrome ? Pitch * 8 >= Width : Pitch >= Width * 4);

    /// <summary>
    /// Paints this shape onto a BGRA frame, in place.
    /// </summary>
    /// <param name="left">Where the shape's own top-left corner goes, in frame pixels.</param>
    public void DrawInto(byte[] pixels, int frameWidth, int frameHeight, int stride, int left, int top)
    {
        if (!IsUsable)
        {
            return;
        }

        switch (Type)
        {
            case PointerShapeType.Monochrome:
                DrawMonochrome(pixels, frameWidth, frameHeight, stride, left, top);
                break;

            case PointerShapeType.MaskedColor:
                DrawColor(pixels, frameWidth, frameHeight, stride, left, top, masked: true);
                break;

            default:
                DrawColor(pixels, frameWidth, frameHeight, stride, left, top, masked: false);
                break;
        }
    }

    private void DrawMonochrome(byte[] pixels, int frameWidth, int frameHeight, int stride, int left, int top)
    {
        var rows = VisibleHeight;
        var xorOffset = rows * Pitch;

        for (var y = 0; y < rows; y++)
        {
            var destY = top + y;
            if (destY < 0 || destY >= frameHeight)
            {
                continue;
            }

            for (var x = 0; x < Width; x++)
            {
                var destX = left + x;
                if (destX < 0 || destX >= frameWidth)
                {
                    continue;
                }

                var bit = 0x80 >> (x & 7);
                var byteIndex = (y * Pitch) + (x >> 3);

                var and = (_bytes[byteIndex] & bit) != 0;
                var xor = (_bytes[xorOffset + byteIndex] & bit) != 0;

                var p = (destY * stride) + (destX * 4);

                if (and)
                {
                    if (!xor)
                    {
                        // Transparent: the desktop shows through untouched.
                        continue;
                    }

                    // Invert. This is the half of the I-beam that keeps it readable over anything.
                    pixels[p] = (byte)~pixels[p];
                    pixels[p + 1] = (byte)~pixels[p + 1];
                    pixels[p + 2] = (byte)~pixels[p + 2];
                }
                else
                {
                    var value = xor ? (byte)0xFF : (byte)0x00;
                    pixels[p] = value;
                    pixels[p + 1] = value;
                    pixels[p + 2] = value;
                }

                // The frame is opaque BGRA. Nothing downstream reads the alpha byte, but keeping it
                // solid means an encoder that does look at it sees a complete image.
                pixels[p + 3] = 0xFF;
            }
        }
    }

    private void DrawColor(byte[] pixels, int frameWidth, int frameHeight, int stride, int left, int top, bool masked)
    {
        for (var y = 0; y < Height; y++)
        {
            var destY = top + y;
            if (destY < 0 || destY >= frameHeight)
            {
                continue;
            }

            for (var x = 0; x < Width; x++)
            {
                var destX = left + x;
                if (destX < 0 || destX >= frameWidth)
                {
                    continue;
                }

                var s = (y * Pitch) + (x * 4);
                var p = (destY * stride) + (destX * 4);

                var alpha = _bytes[s + 3];

                if (masked)
                {
                    // The alpha byte is a two-valued mask here, not a blend factor.
                    if (alpha == 0)
                    {
                        pixels[p] = _bytes[s];
                        pixels[p + 1] = _bytes[s + 1];
                        pixels[p + 2] = _bytes[s + 2];
                    }
                    else
                    {
                        pixels[p] ^= _bytes[s];
                        pixels[p + 1] ^= _bytes[s + 1];
                        pixels[p + 2] ^= _bytes[s + 2];
                    }
                }
                else if (alpha == 0xFF)
                {
                    pixels[p] = _bytes[s];
                    pixels[p + 1] = _bytes[s + 1];
                    pixels[p + 2] = _bytes[s + 2];
                }
                else if (alpha != 0)
                {
                    // Straight alpha, not premultiplied: DXGI hands over the cursor's own colours
                    // and the mixing is ours to do.
                    pixels[p] = Blend(_bytes[s], pixels[p], alpha);
                    pixels[p + 1] = Blend(_bytes[s + 1], pixels[p + 1], alpha);
                    pixels[p + 2] = Blend(_bytes[s + 2], pixels[p + 2], alpha);
                }
                else
                {
                    continue;
                }

                pixels[p + 3] = 0xFF;
            }
        }
    }

    private static byte Blend(byte source, byte destination, byte alpha) =>
        (byte)(((source * alpha) + (destination * (255 - alpha))) / 255);
}
