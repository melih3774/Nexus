using PcCore.Windows.Media;

using Xunit;

namespace PcCore.Tests.Media;

/// <summary>
/// The scaler and colour converter, against an implementation too slow to ship and too simple to be
/// wrong.
/// <para>
/// The real converter has three things in it that each produce plausible pictures when broken: area
/// averaging that walks source pointers forward, a multiply-by-reciprocal in place of division, and a
/// split into bands that run on separate threads. A mistake in any of them does not crash — it shifts
/// a column, leaves one band a row short, or darkens a frame by a shade — so the output is compared
/// pixel by pixel with the reference, at the sizes the planner actually asks for.
/// </para>
/// </summary>
public sealed class Nv12ConverterTests
{
    private static byte[] Pattern(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);

        // Some hard edges and flat areas too, which is what text on a desktop is made of.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x += 7)
            {
                var at = ((y * width) + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = (byte)((y / 3 % 2) * 255);
            }
        }

        return pixels;
    }

    /// <summary>Area-average downscale then BT.601/709 limited range, the long way.</summary>
    private static byte[] Reference(byte[] bgra, int sw, int sh, int w, int h)
    {
        var bt709 = h >= 720;
        var output = new byte[(w * h) + (w * h / 2)];
        var avg = new double[w * h * 3];

        for (var dy = 0; dy < h; dy++)
        {
            var y0 = (int)((long)dy * sh / h);
            var y1 = Math.Max(y0 + 1, (int)((long)(dy + 1) * sh / h));

            for (var dx = 0; dx < w; dx++)
            {
                var x0 = (int)((long)dx * sw / w);
                var x1 = Math.Max(x0 + 1, (int)((long)(dx + 1) * sw / w));
                long b = 0, g = 0, r = 0, n = 0;

                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var at = ((y * sw) + x) * 4;
                        b += bgra[at];
                        g += bgra[at + 1];
                        r += bgra[at + 2];
                        n++;
                    }
                }

                var i = ((dy * w) + dx) * 3;
                avg[i] = b / n;
                avg[i + 1] = g / n;
                avg[i + 2] = r / n;
            }
        }

        double[] k = bt709
            ? [0.18258, 0.61423, 0.06201, -0.10064, -0.33857, 0.43922, 0.43922, -0.39894, -0.04027]
            : [0.25679, 0.50413, 0.09791, -0.14822, -0.29099, 0.43922, 0.43922, -0.36779, -0.07142];

        for (var dy = 0; dy < h; dy++)
        {
            for (var dx = 0; dx < w; dx++)
            {
                var i = ((dy * w) + dx) * 3;
                var (b, g, r) = (avg[i], avg[i + 1], avg[i + 2]);
                output[(dy * w) + dx] = Clamp(16 + (k[0] * r) + (k[1] * g) + (k[2] * b));
            }
        }

        for (var cy = 0; cy < h / 2; cy++)
        {
            for (var cx = 0; cx < w / 2; cx++)
            {
                double b = 0, g = 0, r = 0;

                for (var oy = 0; oy < 2; oy++)
                {
                    for (var ox = 0; ox < 2; ox++)
                    {
                        var i = ((((cy * 2) + oy) * w) + (cx * 2) + ox) * 3;
                        b += avg[i];
                        g += avg[i + 1];
                        r += avg[i + 2];
                    }
                }

                (b, g, r) = (Math.Floor(b / 4), Math.Floor(g / 4), Math.Floor(r / 4));

                var at = (w * h) + (cy * w) + (cx * 2);
                output[at] = Clamp(128 + (k[3] * r) + (k[4] * g) + (k[5] * b));
                output[at + 1] = Clamp(128 + (k[6] * r) + (k[7] * g) + (k[8] * b));
            }
        }

        return output;
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1920, 1080, 640, 360)]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1280, 720, 640, 360)]
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(1366, 768, 1280, 720)]
    public void MatchesTheReferenceWithinRounding(int sw, int sh, int w, int h)
    {
        var source = Pattern(sw, sh, seed: sw + h);
        var converted = new byte[Nv12Converter.FrameBytes(w, h)];

        new Nv12Converter().Convert(source, sw, sh, sw * 4, converted, w, h);

        var expected = Reference(source, sw, sh, w, h);
        var worst = 0;
        var worstAt = -1;

        for (var i = 0; i < expected.Length; i++)
        {
            var difference = Math.Abs(expected[i] - converted[i]);

            if (difference > worst)
            {
                worst = difference;
                worstAt = i;
            }
        }

        // Two levels: fixed-point rounding against floating point, compounded once through the
        // chroma average. A misplaced row or column shows up as tens of levels, not two.
        Assert.True(worst <= 2, $"byte {worstAt} of {expected.Length} differs by {worst}");
    }

    [Fact]
    public void RepeatedConversionsOfTheSameFrameAreIdentical()
    {
        // The accumulators are reused between frames. Anything left in them from the last one shows
        // up here as a second frame that differs from the first.
        var source = Pattern(1920, 1080, seed: 7);
        var converter = new Nv12Converter();
        var first = new byte[Nv12Converter.FrameBytes(1280, 720)];
        var second = new byte[first.Length];

        converter.Convert(source, 1920, 1080, 1920 * 4, first, 1280, 720);
        converter.Convert(source, 1920, 1080, 1920 * 4, second, 1280, 720);

        Assert.Equal(first, second);
    }

    [Fact]
    public void WhiteStaysWhiteAndBlackStaysBlack()
    {
        // The limited-range end points, exactly. A reciprocal rounded the wrong way makes white 234.
        foreach (var (value, luma) in new[] { ((byte)255, (byte)235), ((byte)0, (byte)16) })
        {
            var source = Enumerable.Repeat(value, 1920 * 1080 * 4).ToArray();
            var converted = new byte[Nv12Converter.FrameBytes(1280, 720)];

            new Nv12Converter().Convert(source, 1920, 1080, 1920 * 4, converted, 1280, 720);

            Assert.All(converted.Take(1280 * 720), y => Assert.Equal(luma, y));
            Assert.All(converted.Skip(1280 * 720), c => Assert.InRange(c, (byte)127, (byte)129));
        }
    }

    [Fact]
    public void OddDimensionsAreRefusedRatherThanMisconverted()
    {
        var source = new byte[64 * 64 * 4];
        var destination = new byte[Nv12Converter.FrameBytes(32, 32) + 64];

        Assert.Throws<ArgumentException>(() =>
            new Nv12Converter().Convert(source, 64, 64, 64 * 4, destination, 31, 32));
    }
}
