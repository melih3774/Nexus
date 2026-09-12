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
/// <b>Why it is split into bands.</b> Measured on a busy desktop rather than an idle one: the
/// conversion that takes three milliseconds on a quiet machine took twenty to thirty while the owner
/// had other work running, and the hardware encoder behind it was waiting on it for almost none of
/// that time. The work is independent row by row, so the frame is cut into a few horizontal bands on
/// even row boundaries — chroma pairs never straddle two — and they run in parallel.
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

    /// <summary>
    /// Accumulators for one horizontal band of the frame. Each band has its own so bands can run on
    /// different threads without sharing anything they write.
    /// </summary>
    private sealed class Band
    {
        public Band(int width)
        {
            SumB = new int[width];
            SumG = new int[width];
            SumR = new int[width];
            ChromaB = new int[width / 2];
            ChromaG = new int[width / 2];
            ChromaR = new int[width / 2];
            ChromaRows = new int[width / 2];
        }

        public int Width => SumB.Length;

        public int[] SumB { get; }
        public int[] SumG { get; }
        public int[] SumR { get; }
        public int[] ChromaB { get; }
        public int[] ChromaG { get; }
        public int[] ChromaR { get; }
        public int[] ChromaRows { get; }
    }

    private Band[] _bands = [];

    /// <summary>
    /// Rows per band below which splitting costs more than it saves.
    /// </summary>
    private const int MinimumBandRows = 96;

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

    /// <summary>
    /// Which destination row each source row belongs to.
    /// <para>
    /// Built with exactly the same rule as the columns — destination row <c>d</c> covers source rows
    /// from <c>d * sourceHeight / height</c> up to <c>(d + 1) * sourceHeight / height</c>. The first
    /// version computed rows the other way round, from the source row, which is also an area average
    /// but a different partition: at 1080 to 720 it gave each destination row a different pair of
    /// source rows than its columns assumed, and at 1440 to 1080 some rows came from a source row one
    /// further down. Nothing crashed; text was simply a little softer vertically than horizontally.
    /// </para>
    /// </summary>
    private int[] _rowOf = [];
    private int _plannedSourceHeight = -1;
    private int _plannedHeight = -1;

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

        PlanColumns(sourceWidth, width);
        PlanRows(sourceHeight, height);

        var bandCount = BandCount(height);
        EnsureBands(bandCount, width);

        var direct = sourceWidth == width && sourceHeight == height;

        fixed (byte* source = bgra)
        fixed (byte* target = destination)
        fixed (int* columns = _columnCount)
        fixed (int* rows = _rowOf)
        fixed (int* coefficients = matrix)
        fixed (int* reciprocals = Reciprocal)
        {
            // Addresses rather than pointers, so they can cross into the band delegates. The arrays
            // stay pinned for as long as this block runs, and the block does not return until every
            // band has finished.
            var src = (nint)source;
            var dst = (nint)target;
            var cols = _columnsAreOneToOne ? 0 : (nint)columns;
            var coef = (nint)coefficients;
            var rec = (nint)reciprocals;
            var rowTable = (nint)rows;

            void RunBand(int band)
            {
                var first = RowStart(band, bandCount, height);
                var last = RowStart(band + 1, bandCount, height);

                if (direct)
                {
                    // Nothing to average: one source pixel per destination pixel. Worth its own loop
                    // because it skips the accumulators entirely, and it is the native-resolution case.
                    Direct((byte*)src, stride, (byte*)dst, width, height, (int*)coef, first, last);
                    return;
                }

                var accumulators = _bands[band];

                fixed (int* sumB = accumulators.SumB, sumG = accumulators.SumG, sumR = accumulators.SumR)
                fixed (int* chromaB = accumulators.ChromaB, chromaG = accumulators.ChromaG)
                fixed (int* chromaR = accumulators.ChromaR, chromaRows = accumulators.ChromaRows)
                {
                    Reduce(
                        (byte*)src,
                        sourceHeight,
                        stride,
                        (byte*)dst,
                        width,
                        height,
                        sumB,
                        sumG,
                        sumR,
                        chromaB,
                        chromaG,
                        chromaR,
                        chromaRows,
                        (int*)cols,
                        (int*)coef,
                        (int*)rec,
                        (int*)rowTable,
                        first,
                        last);
                }
            }

            if (bandCount == 1)
            {
                RunBand(0);
            }
            else
            {
                Parallel.For(0, bandCount, RunBand);
            }
        }
    }

    private void PlanRows(int sourceHeight, int height)
    {
        if (_plannedSourceHeight == sourceHeight && _plannedHeight == height)
        {
            return;
        }

        var rows = new int[sourceHeight];

        for (var dy = 0; dy < height; dy++)
        {
            var start = (int)((long)dy * sourceHeight / height);
            var end = dy == height - 1 ? sourceHeight : (int)((long)(dy + 1) * sourceHeight / height);

            for (var sy = start; sy < end; sy++)
            {
                rows[sy] = dy;
            }
        }

        _rowOf = rows;
        _plannedSourceHeight = sourceHeight;
        _plannedHeight = height;
    }

    /// <summary>How many bands a frame of this height is worth splitting into on this machine.</summary>
    private static int BandCount(int height) =>
        Math.Max(1, Math.Min(Math.Clamp(Environment.ProcessorCount / 2, 1, 4), height / MinimumBandRows));

    /// <summary>The first destination row of a band. Always even, so no chroma pair is split.</summary>
    private static int RowStart(int band, int bands, int height) =>
        band >= bands ? height : (int)((long)height * band / bands) & ~1;

    private void EnsureBands(int count, int width)
    {
        if (_bands.Length == count && _bands.All(b => b.Width >= width))
        {
            return;
        }

        _bands = [.. Enumerable.Range(0, count).Select(_ => new Band(width))];
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
        int* reciprocals,
        int* rowOf,
        int firstRow,
        int endRow)
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

        var currentRow = firstRow;
        var rowsAccumulated = 0;

        // The source rows that land in this band: the first whose destination row is at or after
        // firstRow, up to the first whose destination row is at or after endRow.
        var firstSource = (int)((long)firstRow * sourceHeight / height);
        var endSource = endRow >= height ? sourceHeight : (int)((long)endRow * sourceHeight / height);

        for (var sy = firstSource; sy < endSource; sy++)
        {
            var row = rowOf[sy];

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
        for (var y = Math.Max(firstRow + 1, currentRow + 1); y < endRow; y++)
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
    /// Chroma is the average of each 2x2 block, for the reason given where it is computed.
    /// </para>
    /// </summary>
    private static void Direct(
        byte* source,
        int stride,
        byte* target,
        int width,
        int height,
        int* matrix,
        int firstRow,
        int endRow)
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

        for (var y = firstRow; y < endRow; y++)
        {
            var pixels = source + ((long)y * stride);
            var output = luma + ((long)y * width);
            var wantsChroma = (y & 1) == 0;
            var chromaOutput = chroma + ((long)(y / 2) * width);

            // The row below, for the chroma average. Always present: heights are even.
            var below = source + ((long)(y + 1) * stride);

            for (var x = 0; x < width; x++)
            {
                int b = pixels[0];
                int g = pixels[1];
                int r = pixels[2];

                output[x] = Clamp(16 + (((yr * r) + (yg * g) + (yb * b) + Half) >> Shift));

                if (wantsChroma && (x & 1) == 0)
                {
                    // Averaged over the 2x2 block rather than sampled from one corner. On a desktop
                    // the difference is coloured text: ClearType fringes are one pixel wide, and a
                    // corner sample keeps some and drops others, which reads as colour noise along
                    // every edge.
                    var cb = (b + pixels[4] + below[0] + below[4]) >> 2;
                    var cg = (g + pixels[5] + below[1] + below[5]) >> 2;
                    var cr = (r + pixels[6] + below[2] + below[6]) >> 2;

                    chromaOutput[x] = Clamp(128 + (((ur * cr) + (ug * cg) + (ub * cb) + Half) >> Shift));
                    chromaOutput[x + 1] = Clamp(128 + (((vr * cr) + (vg * cg) + (vb * cb) + Half) >> Shift));
                }

                pixels += 4;
                below += 4;
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
