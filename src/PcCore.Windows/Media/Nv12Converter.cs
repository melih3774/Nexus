using System.Runtime.Versioning;

namespace PcCore.Windows.Media;

/// <summary>
/// Turns captured BGRA into the NV12 an H.264 encoder takes, scaling on the way.
/// <para>
/// <b>Why scaling and colour conversion are one pass.</b> The obvious build is two: downscale with
/// GDI+, then convert. That reads the source once, writes a full intermediate frame, reads it back
/// and writes again — four passes over several megabytes, thirty times a second, to produce a
/// result no better than this. Here every source pixel is read exactly once and accumulated
/// straight into the destination pixel it belongs to, so the intermediate frame never exists.
/// </para>
/// <para>
/// <b>Why area averaging rather than bilinear.</b> A desktop is mostly text, and text is the thing a
/// remote desktop is being read for. Bilinear downscaling samples two pixels out of the three or
/// four a destination pixel covers, which drops strokes: at 1920 to 1280 the thin side of a letter
/// vanishes on some columns and not others, and the result shimmers as the window scrolls. Averaging
/// the whole area a destination pixel covers keeps the stroke as grey instead of losing it.
/// </para>
/// <para>
/// <b>Why it is written with pointers.</b> Measured, not assumed. The first version indexed spans
/// and scattered each source pixel into an accumulator chosen by a lookup — correct, and 22 ms for
/// one 1080p frame, which is most of a 30 fps budget spent before the encoder sees anything. Reading
/// each destination pixel's source span in order instead, through pointers, is the same arithmetic
/// with no bounds checks and no dependent loads, and it costs a fraction of that. The encoder on
/// this machine takes about a millisecond; it would have been absurd to spend twenty feeding it.
/// </para>
/// <para>
/// <b>Why the matrix depends on the height.</b> Android's decoder picks its own conversion when the
/// stream does not insist otherwise, and it picks by size: BT.601 below 720 lines, BT.709 at or
/// above. Encoding with the other one leaves skin tones and greens visibly wrong. This follows the
/// same rule the decoder follows, and the encoder tags the stream to match.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class Nv12Converter
{
    // 16-bit fixed point, limited range (16-235 luma, 16-240 chroma), which is what H.264 signals
    // by default and what every phone decoder assumes.
    private const int Shift = 16;
    private const int Half = 1 << (Shift - 1);

    private static readonly int[] Bt601 =
    [
        16829, 33039, 6416,      // Y from R, G, B
        -9714, -19070, 28782,    // U
        28782, -24103, -4680,    // V
    ];

    private static readonly int[] Bt709 =
    [
        11966, 40254, 4064,
        -6595, -22186, 28782,
        28782, -26142, -2639,
    ];

    private int[] _sumB = [];
    private int[] _sumG = [];
    private int[] _sumR = [];
    private int[] _chromaB = [];
    private int[] _chromaG = [];
    private int[] _chromaR = [];
    private int[] _chromaRows = [];

    /// <summary>
    /// How many source pixels each destination column covers.
    /// <para>
    /// Only the counts, not the offsets: the ranges tile the source row exactly, each starting where
    /// the previous one ended, so a reader that walks forward already knows where it is.
    /// </para>
    /// </summary>
    private int[] _columnCount = [];

    /// <summary>
    /// Fixed-point reciprocals of the small divisors this uses, so averaging never divides.
    /// <para>
    /// This is where the time went. Averaging an area means one division per channel per pixel, and
    /// at 1080p that is six million integer divisions a frame — measured at thirteen milliseconds,
    /// most of the cost of the whole conversion and several times what the hardware encoder behind
    /// it takes. A multiply by a precomputed reciprocal is exact for every divisor that occurs here.
    /// </para>
    /// </summary>
    private static readonly int[] Reciprocal = BuildReciprocals();

    private const int ReciprocalShift = 24;

    /// <summary>True when every column covers exactly one pixel, which is the unscaled case.</summary>
    private bool _columnsAreOneToOne;
    private int _plannedSourceWidth = -1;
    private int _plannedWidth = -1;

    /// <summary>The matrix a stream of this height must use, and the encoder must advertise.</summary>
    public static bool UsesBt709(int height) => height >= 720;

    /// <summary>Bytes an NV12 frame occupies: a full luma plane and a half-size chroma plane.</summary>
    public static int FrameBytes(int width, int height) => (width * height) + (width * height / 2);

    /// <summary>Converts one frame.</summary>
    /// <param name="bgra">Source pixels. Row <c>y</c> starts at <c>y * stride</c>.</param>
    /// <param name="destination">At least <see cref="FrameBytes"/>. Y plane then interleaved UV.</param>
    public void Convert(
        ReadOnlySpan<byte> bgra,
        int sourceWidth,
        int sourceHeight,
        int stride,
        Span<byte> destination,
        int width,
        int height)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A frame needs positive dimensions.");
        }

        if ((width & 1) != 0 || (height & 1) != 0)
        {
            // NV12 has one chroma sample per 2x2 luma block, so an odd dimension has no
            // representation. The planner rounds before anything gets here; this asserts that it did.
            throw new ArgumentException("NV12 frames must have even dimensions.", nameof(width));
        }

        if (destination.Length < FrameBytes(width, height))
        {
            throw new ArgumentException("The destination is too small for an NV12 frame.", nameof(destination));
        }

        if ((long)sourceHeight * stride > bgra.Length)
        {
            throw new ArgumentException("The source is shorter than its stated height and stride.", nameof(bgra));
        }

        var matrix = UsesBt709(height) ? Bt709 : Bt601;

        if (width > sourceWidth || height > sourceHeight)
        {
            // Enlarging is not what this is for — the planner never asks for more pixels than were
            // captured — but a display can change size mid-stream, and a frame that arrives one tick
            // before the capture is reopened must still encode rather than throw.
            Replicate(bgra, sourceWidth, sourceHeight, stride, destination, width, height, matrix);
            return;
        }

        EnsureCapacity(width);
        PlanColumns(sourceWidth, width);

        fixed (byte* source = bgra)
        fixed (byte* target = destination)
        fixed (int* sumB = _sumB, sumG = _sumG, sumR = _sumR)
        fixed (int* chromaB = _chromaB, chromaG = _chromaG, chromaR = _chromaR, chromaRows = _chromaRows)
        fixed (int* columns = _columnCount)
        fixed (int* coefficients = matrix)
        fixed (int* reciprocals = Reciprocal)
        {
            if (sourceWidth == width && sourceHeight == height)
            {
                // Nothing to average: one source pixel per destination pixel. Worth its own loop
                // because it skips the accumulators entirely, and it is the native-resolution case.
                Direct(source, stride, target, width, height, coefficients);
                return;
            }

            Reduce(
                source,
                sourceHeight,
                stride,
                target,
                width,
                height,
                sumB,
                sumG,
                sumR,
                chromaB,
                chromaG,
                chromaR,
                chromaRows,
                _columnsAreOneToOne ? null : columns,
                coefficients,
                reciprocals);
        }
    }

    private static void Reduce(
        byte* source,
        int sourceHeight,
        int stride,
        byte* target,
        int width,
        int height,
        int* sumB,
        int* sumG,
        int* sumR,
        int* chromaB,
        int* chromaG,
        int* chromaR,
        int* chromaRows,
        int* columnCount,
        int* matrix,
        int* reciprocals)
    {
        var luma = target;
        var chroma = target + ((long)width * height);
        var chromaWidth = width / 2;

        Clear(sumB, width);
        Clear(sumG, width);
        Clear(sumR, width);
        Clear(chromaB, chromaWidth);
        Clear(chromaG, chromaWidth);
        Clear(chromaR, chromaWidth);
        Clear(chromaRows, chromaWidth);

        var currentRow = 0;
        var rowsAccumulated = 0;

        for (var sy = 0; sy < sourceHeight; sy++)
        {
            var row = (int)((long)sy * height / sourceHeight);

            if (row != currentRow)
            {
                FlushRow(
                    luma, chroma, width, chromaWidth, height, currentRow, rowsAccumulated,
                    sumB, sumG, sumR, chromaB, chromaG, chromaR, chromaRows, columnCount, matrix,
                    reciprocals);

                currentRow = row;
                rowsAccumulated = 0;
            }

            var run = source + ((long)sy * stride);

            if (columnCount is null)
            {
                // No horizontal scaling: one source pixel per destination pixel, read in order.
                // Worth its own loop — it is the native-resolution case, and skipping the per-pixel
                // width lookup is most of the cost of it.
                for (var dx = 0; dx < width; dx++)
                {
                    sumB[dx] += run[0];
                    sumG[dx] += run[1];
                    sumR[dx] += run[2];
                    run += 4;
                }
            }
            else
            {
                // Each destination column covers the source pixels immediately after the previous
                // one's — the ranges tile the row exactly — so the read pointer only ever moves
                // forward and never needs an index.
                for (var dx = 0; dx < width; dx++)
                {
                    var n = columnCount[dx];

                    int b = 0, g = 0, r = 0;

                    for (var k = 0; k < n; k++)
                    {
                        b += run[0];
                        g += run[1];
                        r += run[2];
                        run += 4;
                    }

                    sumB[dx] += b;
                    sumG[dx] += g;
                    sumR[dx] += r;
                }
            }

            rowsAccumulated++;
        }

        FlushRow(
            luma, chroma, width, chromaWidth, height, currentRow, rowsAccumulated,
            sumB, sumG, sumR, chromaB, chromaG, chromaR, chromaRows, columnCount, matrix, reciprocals);

        // A source shorter than expected — a torn capture, or a display that shrank between the
        // frame and the reopen — leaves the bottom rows never written. Repeating the last finished
        // row is invisible for one frame and far better than handing the encoder uninitialised
        // memory, which decodes as a band of green noise.
        for (var y = currentRow + 1; y < height; y++)
        {
            Buffer.MemoryCopy(luma + ((long)(y - 1) * width), luma + ((long)y * width), width, width);

            if ((y & 1) == 1)
            {
                var uv = y / 2;

                if (uv > 0)
                {
                    Buffer.MemoryCopy(chroma + ((long)(uv - 1) * width), chroma + ((long)uv * width), width, width);
                }
            }
        }
    }

    /// <summary>
    /// One source pixel per destination pixel: convert in place, with no averaging at all.
    /// <para>
    /// Chroma is taken from the top-left of each 2x2 block rather than its average. That is what
    /// 4:2:0 does to the picture anyway, and paying for a four-pixel average to feed a format which
    /// keeps one sample per four pixels buys nothing visible.
    /// </para>
    /// </summary>
    private static void Direct(byte* source, int stride, byte* target, int width, int height, int* matrix)
    {
        var luma = target;
        var chroma = target + ((long)width * height);

        var yr = matrix[0];
        var yg = matrix[1];
        var yb = matrix[2];
        var ur = matrix[3];
        var ug = matrix[4];
        var ub = matrix[5];
        var vr = matrix[6];
        var vg = matrix[7];
        var vb = matrix[8];

        for (var y = 0; y < height; y++)
        {
            var pixels = source + ((long)y * stride);
            var output = luma + ((long)y * width);
            var wantsChroma = (y & 1) == 0;
            var chromaOutput = chroma + ((long)(y / 2) * width);

            for (var x = 0; x < width; x++)
            {
                int b = pixels[0];
                int g = pixels[1];
                int r = pixels[2];

                output[x] = Clamp(16 + (((yr * r) + (yg * g) + (yb * b) + Half) >> Shift));

                if (wantsChroma && (x & 1) == 0)
                {
                    chromaOutput[x] = Clamp(128 + (((ur * r) + (ug * g) + (ub * b) + Half) >> Shift));
                    chromaOutput[x + 1] = Clamp(128 + (((vr * r) + (vg * g) + (vb * b) + Half) >> Shift));
                }

                pixels += 4;
            }
        }
    }

    /// <summary>Averages one destination row out of the accumulators and writes its luma and chroma.</summary>
    private static void FlushRow(
        byte* luma,
        byte* chroma,
        int width,
        int chromaWidth,
        int height,
        int row,
        int rowsAccumulated,
        int* sumB,
        int* sumG,
        int* sumR,
        int* chromaB,
        int* chromaG,
        int* chromaR,
        int* chromaRows,
        int* columnCount,
        int* matrix,
        int* reciprocals)
    {
        if (row < 0 || row >= height || rowsAccumulated <= 0)
        {
            return;
        }

        var output = luma + ((long)row * width);

        var yr = matrix[0];
        var yg = matrix[1];
        var yb = matrix[2];

        for (var x = 0; x < width; x++)
        {
            var n = (columnCount is null ? 1 : columnCount[x]) * rowsAccumulated;

            if (n <= 0)
            {
                output[x] = x > 0 ? output[x - 1] : (byte)16;
                continue;
            }

            var b = Average(sumB[x], n, reciprocals);
            var g = Average(sumG[x], n, reciprocals);
            var r = Average(sumR[x], n, reciprocals);

            output[x] = Clamp(16 + (((yr * r) + (yg * g) + (yb * b) + Half) >> Shift));

            var cx = x >> 1;
            chromaB[cx] += b;
            chromaG[cx] += g;
            chromaR[cx] += r;
            chromaRows[cx]++;

            sumB[x] = 0;
            sumG[x] = 0;
            sumR[x] = 0;
        }

        // Chroma is one sample per 2x2 luma block, so it is written on the odd row of each pair.
        if ((row & 1) == 0 && row != height - 1)
        {
            return;
        }

        var chromaOutput = chroma + ((long)(row / 2) * width);

        var ur = matrix[3];
        var ug = matrix[4];
        var ub = matrix[5];
        var vr = matrix[6];
        var vg = matrix[7];
        var vb = matrix[8];

        for (var cx = 0; cx < chromaWidth; cx++)
        {
            var n = chromaRows[cx];

            if (n <= 0)
            {
                chromaOutput[cx * 2] = 128;
                chromaOutput[(cx * 2) + 1] = 128;
                continue;
            }

            var b = Average(chromaB[cx], n, reciprocals);
            var g = Average(chromaG[cx], n, reciprocals);
            var r = Average(chromaR[cx], n, reciprocals);

            chromaOutput[cx * 2] = Clamp(128 + (((ur * r) + (ug * g) + (ub * b) + Half) >> Shift));
            chromaOutput[(cx * 2) + 1] = Clamp(128 + (((vr * r) + (vg * g) + (vb * b) + Half) >> Shift));

            chromaB[cx] = 0;
            chromaG[cx] = 0;
            chromaR[cx] = 0;
            chromaRows[cx] = 0;
        }
    }

    /// <summary>Nearest-neighbour, for the rare frame that is smaller than the size being encoded.</summary>
    private static void Replicate(
        ReadOnlySpan<byte> bgra,
        int sourceWidth,
        int sourceHeight,
        int stride,
        Span<byte> destination,
        int width,
        int height,
        int[] matrix)
    {
        var luma = destination[..(width * height)];
        var chroma = destination.Slice(width * height, width * height / 2);

        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / height));
            var offset = sy * stride;
            var lumaRow = luma.Slice(y * width, width);
            var chromaRow = (y & 1) == 0 ? chroma.Slice(y / 2 * width, width) : default;

            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / width));
                var at = offset + (sx * 4);

                int b = bgra[at];
                int g = bgra[at + 1];
                int r = bgra[at + 2];

                lumaRow[x] = Clamp(16 + (((matrix[0] * r) + (matrix[1] * g) + (matrix[2] * b) + Half) >> Shift));

                if (!chromaRow.IsEmpty && (x & 1) == 0)
                {
                    chromaRow[x] =
                        Clamp(128 + (((matrix[3] * r) + (matrix[4] * g) + (matrix[5] * b) + Half) >> Shift));
                    chromaRow[x + 1] =
                        Clamp(128 + (((matrix[6] * r) + (matrix[7] * g) + (matrix[8] * b) + Half) >> Shift));
                }
            }
        }
    }

    /// <summary>
    /// Which source pixels each destination column covers.
    /// <para>
    /// Cached across frames: the answer only changes when the capture or the target size does, and
    /// recomputing it is a couple of thousand divisions a frame to get the same two arrays back.
    /// </para>
    /// </summary>
    private void PlanColumns(int sourceWidth, int width)
    {
        if (_plannedSourceWidth == sourceWidth && _plannedWidth == width)
        {
            return;
        }

        _columnCount = new int[width];

        for (var dx = 0; dx < width; dx++)
        {
            var start = (int)((long)dx * sourceWidth / width);
            var end = (int)((long)(dx + 1) * sourceWidth / width);

            _columnCount[dx] = Math.Max(1, end - start);
        }

        _columnsAreOneToOne = sourceWidth == width;
        _plannedSourceWidth = sourceWidth;
        _plannedWidth = width;
    }

    private void EnsureCapacity(int width)
    {
        if (_sumB.Length >= width)
        {
            return;
        }

        _sumB = new int[width];
        _sumG = new int[width];
        _sumR = new int[width];

        var chroma = width / 2;
        _chromaB = new int[chroma];
        _chromaG = new int[chroma];
        _chromaR = new int[chroma];
        _chromaRows = new int[chroma];
    }

    private static void Clear(int* values, int count)
    {
        new Span<int>(values, count).Clear();
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    /// <summary>
    /// Averages a channel sum without dividing. Falls back to a real division for a divisor beyond
    /// the table, which needs a downscale of more than sixteen in both axes at once.
    /// </summary>
    private static int Average(int sum, int n, int* reciprocals) =>
        n == 1 ? sum
        : n < 256 ? (int)(((long)sum * reciprocals[n]) >> ReciprocalShift)
        : sum / n;

    private static int[] BuildReciprocals()
    {
        var table = new int[256];

        for (var n = 1; n < table.Length; n++)
        {
            // Rounded up, so the largest possible sum still averages to 255 rather than 254. A whole
            // frame one shade dark is the kind of error nobody ever finds.
            table[n] = (int)(((1L << ReciprocalShift) + n - 1) / n);
        }

        return table;
    }
}
