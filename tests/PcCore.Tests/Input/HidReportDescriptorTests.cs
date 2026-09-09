using PcCore.NativeInput.Hid;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// Walks the report descriptors as Windows would and checks that they say what the constants claim.
/// <para>
/// <b>Why a parser in a test rather than a hand-checked comment.</b> The report length is arithmetic
/// over the descriptor — sum of REPORT_SIZE times REPORT_COUNT across every INPUT item — and it is
/// arithmetic nobody does correctly by eye. The digitizer proves it twice over: the first draft was
/// annotated and coded as a 7-byte report and was really 9, and adding the report id Windows
/// requires made it 10. Both times the comment and the constant were wrong together, and both times
/// only recomputing from the bytes caught it.
/// </para>
/// </summary>
public sealed class HidReportDescriptorTests
{
    [Fact]
    public void MouseReportIsSevenBytes()
    {
        var sizes = HidDescriptorWalker.ReportSizes(HidReportDescriptors.Mouse);

        Assert.Equal(HidReportDescriptors.MouseReportLength, sizes.InputBytes);
    }

    [Fact]
    public void KeyboardReportIsEightBytes()
    {
        var sizes = HidDescriptorWalker.ReportSizes(HidReportDescriptors.Keyboard);

        Assert.Equal(HidReportDescriptors.KeyboardReportLength, sizes.InputBytes);
    }

    [Fact]
    public void DigitizerReportIsTenBytes()
    {
        var sizes = HidDescriptorWalker.ReportSizes(HidReportDescriptors.Digitizer);

        Assert.Equal(HidReportDescriptors.DigitizerReportLength, sizes.InputBytes);
    }

    [Fact]
    public void DigitizerDeclaresATwoByteContactCountMaximumFeatureReport()
    {
        var sizes = HidDescriptorWalker.ReportSizes(HidReportDescriptors.Digitizer);

        Assert.Equal(HidReportDescriptors.DigitizerFeatureReportLength, sizes.FeatureBytes);
    }

    /// <summary>
    /// Windows sends an LED output report to any keyboard it enumerates. A descriptor that declares
    /// no output collection gets one anyway and logs an error for every Caps Lock.
    /// </summary>
    [Fact]
    public void KeyboardDeclaresAnLedOutputReport()
    {
        var sizes = HidDescriptorWalker.ReportSizes(HidReportDescriptors.Keyboard);

        Assert.Equal(1, sizes.OutputBytes);
    }

    [Theory]
    [InlineData(nameof(HidReportDescriptors.Mouse))]
    [InlineData(nameof(HidReportDescriptors.Keyboard))]
    [InlineData(nameof(HidReportDescriptors.Digitizer))]
    public void EveryCollectionIsClosed(string which)
    {
        var descriptor = which switch
        {
            nameof(HidReportDescriptors.Mouse) => HidReportDescriptors.Mouse,
            nameof(HidReportDescriptors.Keyboard) => HidReportDescriptors.Keyboard,
            _ => HidReportDescriptors.Digitizer,
        };

        Assert.Equal(0, HidDescriptorWalker.CollectionBalance(descriptor));
    }

    /// <summary>
    /// Every item has to be consumed exactly. A descriptor that runs off its own end parses on this
    /// machine and is rejected by the HID class driver, which is the worst place to find out.
    /// </summary>
    [Theory]
    [InlineData(nameof(HidReportDescriptors.Mouse))]
    [InlineData(nameof(HidReportDescriptors.Keyboard))]
    [InlineData(nameof(HidReportDescriptors.Digitizer))]
    public void DescriptorIsWellFormed(string which)
    {
        var descriptor = which switch
        {
            nameof(HidReportDescriptors.Mouse) => HidReportDescriptors.Mouse,
            nameof(HidReportDescriptors.Keyboard) => HidReportDescriptors.Keyboard,
            _ => HidReportDescriptors.Digitizer,
        };

        // Throws on a truncated item; the assertion is that it does not.
        _ = HidDescriptorWalker.ReportSizes(descriptor);
    }
}

/// <summary>
/// The smallest HID report-descriptor parser that can answer "how big is a report".
/// <para>
/// Deliberately not a general HID parser. It tracks the two globals that determine report size and
/// sums the main items, which is exactly enough to check the descriptors against their constants and
/// no more than that.
/// </para>
/// </summary>
internal static class HidDescriptorWalker
{
    internal readonly record struct Sizes(int InputBytes, int OutputBytes, int FeatureBytes);

    private const byte TagMask = 0xFC;

    private const byte Input = 0x80;
    private const byte Output = 0x90;
    private const byte Feature = 0xB0;
    private const byte Collection = 0xA0;
    private const byte EndCollection = 0xC0;
    private const byte ReportSize = 0x74;
    private const byte ReportCount = 0x94;
    private const byte ReportId = 0x84;

    internal static Sizes ReportSizes(ReadOnlySpan<byte> descriptor)
    {
        int reportSize = 0, reportCount = 0;
        int inputBits = 0, outputBits = 0, featureBits = 0;
        var hasReportId = false;

        foreach (var item in Items(descriptor))
        {
            switch (item.Tag)
            {
                case ReportSize:
                    reportSize = (int)item.Value;
                    break;
                case ReportCount:
                    reportCount = (int)item.Value;
                    break;
                case ReportId:
                    hasReportId = true;
                    break;
                case Input:
                    inputBits += reportSize * reportCount;
                    break;
                case Output:
                    outputBits += reportSize * reportCount;
                    break;
                case Feature:
                    featureBits += reportSize * reportCount;
                    break;
            }
        }

        // A descriptor declaring report ids spends the first byte of every report on the id.
        var idByte = hasReportId ? 1 : 0;

        return new Sizes(
            Bytes(inputBits) + idByte,
            outputBits == 0 ? 0 : Bytes(outputBits) + idByte,
            featureBits == 0 ? 0 : Bytes(featureBits) + idByte);

        static int Bytes(int bits) => (bits + 7) / 8;
    }

    internal static int CollectionBalance(ReadOnlySpan<byte> descriptor)
    {
        var depth = 0;

        foreach (var item in Items(descriptor))
        {
            if (item.Tag == Collection)
            {
                depth++;
            }
            else if (item.Tag == EndCollection)
            {
                depth--;
            }
        }

        return depth;
    }

    private readonly record struct Item(byte Tag, uint Value);

    private static List<Item> Items(ReadOnlySpan<byte> descriptor)
    {
        var items = new List<Item>();
        var index = 0;

        while (index < descriptor.Length)
        {
            var prefix = descriptor[index++];

            // The two low bits encode the payload length, and 3 means four bytes rather than three.
            var length = (prefix & 0x03) switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                _ => 4,
            };

            if (index + length > descriptor.Length)
            {
                throw new InvalidOperationException(
                    $"descriptor is truncated: item at {index - 1} claims {length} payload bytes");
            }

            uint value = 0;
            for (var i = 0; i < length; i++)
            {
                value |= (uint)descriptor[index + i] << (8 * i);
            }

            index += length;
            items.Add(new Item((byte)(prefix & TagMask), value));
        }

        return items;
    }
}
