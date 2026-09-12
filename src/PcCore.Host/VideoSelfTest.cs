using System.Diagnostics;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;
using PcCore.Capabilities.Screen;
using PcCore.Windows;
using PcCore.Windows.Media;

namespace PcCore.Host;

/// <summary>
/// Measures the real video pipeline on this machine: capture, scale, convert, encode.
/// <para>
/// <b>Why this exists.</b> Every claim about a streaming pipeline is a number, and every number
/// depends on hardware nobody can substitute — which encoder the driver registered, how fast this
/// GPU is, how long Desktop Duplication takes to hand over a 1080p frame. A unit test can prove the
/// bitstream is well-formed and the backpressure rules are right; it cannot say whether this PC
/// encodes 1080p30 in real time. This can, and it prints the answer rather than asserting it.
/// </para>
/// <para>
/// It stops short of the network and the phone on purpose. Everything it reports is attributable to
/// the PC alone, so when the end-to-end figures are worse the difference is the link or the client
/// rather than a mystery spread across three machines.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class VideoSelfTest
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggers)
    {
        var seconds = Argument(args, "--seconds", 6);

        Console.WriteLine();
        Console.WriteLine("Video pipeline");
        Console.WriteLine("==============");
        Console.WriteLine();

        var factory = new MediaFoundationVideoEncoderFactory(
            loggers.CreateLogger<MediaFoundationVideoEncoderFactory>());

        Console.WriteLine($"  Encoder: {factory.Describe()}");
        Console.WriteLine();

        if (args.Contains("--list") || !factory.IsAvailable)
        {
            Console.WriteLine("  Registered H.264 transforms:");

            foreach (var entry in factory.Inventory())
            {
                Console.WriteLine($"    {entry}");
            }

            Console.WriteLine();
        }

        if (!factory.IsAvailable)
        {
            Console.WriteLine("  FAIL  this PC has no H.264 encoder; streaming would fall back to still frames.");
            return 1;
        }

        if (args.Contains("--convert-only"))
        {
            BenchmarkConversion();
            return 0;
        }

        var stream = new DxgiDesktopStream(loggers);
        var displays = stream.GetDisplays();

        if (displays.Count == 0)
        {
            Console.WriteLine("  FAIL  no display to capture.");
            return 1;
        }

        var display = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        Console.WriteLine($"  Display: {display}");
        Console.WriteLine();

        var requested = args.FirstOrDefault(a => a.StartsWith("--plan=", StringComparison.Ordinal));

        var plans = requested is null
            ? DefaultPlans(display)
            : [Parse(requested["--plan=".Length..], display)];

        var ok = true;

        Console.WriteLine(
            "  {0,-26} {1,7} {2,9} {3,9} {4,9} {5,8} {6,7}",
            "plan",
            "fps",
            "Mbit/s",
            "encode",
            "convert",
            "keyfrm",
            "drops");

        Console.WriteLine("  " + new string('-', 82));

        foreach (var plan in plans)
        {
            var result = await MeasureAsync(stream, display, factory, plan, seconds, loggers).ConfigureAwait(false);

            Console.WriteLine(
                "  {0,-26} {1,7:F1} {2,9:F2} {3,9} {4,9} {5,8} {6,7}",
                plan.Label,
                result.Fps,
                result.Megabits,
                $"{result.EncodeMs:F1} ms",
                $"{result.ConvertMs:F1} ms",
                result.Keyframes,
                result.Skipped);

            ok &= result.Frames > 0 && result.Keyframes > 0;
        }

        Console.WriteLine();

        if (!ok)
        {
            Console.WriteLine("  FAIL  at least one plan produced no frames or no keyframe.");
            return 1;
        }

        Console.WriteLine("  PASS  every plan produced a keyframed H.264 stream.");
        Console.WriteLine();
        Console.WriteLine("  'encode' is the time inside the encoder, 'convert' the scale and colour");
        Console.WriteLine("  conversion in front of it. Together they are this PC's share of the");
        Console.WriteLine("  capture-to-render latency; the rest is the link and the phone's decoder.");

        return 0;
    }

    private static List<Plan> DefaultPlans(DisplayInfo display) =>
    [
        new("360p 30fps 800kbps", 640, 360, 30, 800),
        new("720p 30fps 2500kbps", 1280, 720, 30, 2500),
        new("1080p 30fps 5000kbps", 1920, 1080, 30, 5000),
        new(
            $"native {display.Width}x{display.Height} 30fps",
            display.Width,
            display.Height,
            30,
            Math.Max(2000, display.Width * display.Height / 400)),
    ];

    private static Plan Parse(string text, DisplayInfo display) => text.ToLowerInvariant() switch
    {
        "360" or "360p" => new("360p", 640, 360, 30, 800),
        "720" or "720p" => new("720p", 1280, 720, 30, 2500),
        "1080" or "1080p" => new("1080p", 1920, 1080, 30, 5000),
        _ => new("native", display.Width, display.Height, 30, 8000),
    };

    private static async Task<Measurement> MeasureAsync(
        IDesktopStream stream,
        DisplayInfo display,
        IVideoEncoderFactory factory,
        Plan plan,
        int seconds,
        ILoggerFactory loggers)
    {
        var (width, height) = Fit(display.Width, display.Height, plan.Width, plan.Height);
        using var encoder = factory.Create(new VideoEncoderSettings(width, height, plan.Fps, plan.Kbps, 2));

        encoder.RequestKeyframe();

        using var source = stream.Open(display.Index);

        var packets = new List<VideoPacket>();
        var pacer = new FramePacer();
        var deadline = Stopwatch.StartNew();
        var interval = TimeSpan.FromMilliseconds(1000.0 / plan.Fps);

        long frames = 0;
        long bytes = 0;
        long keyframes = 0;
        long skipped = 0;
        double encodeMs = 0;
        double convertMs = 0;
        double captureMs = 0;
        long encoded = 0;

        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var tick = Stopwatch.GetTimestamp();
            var result = source.TryGetFrame(TimeSpan.FromMilliseconds(60));
            captureMs += Stopwatch.GetElapsedTime(tick).TotalMilliseconds;

            if (result.Status != CaptureStatus.Frame || result.Frame is null)
            {
                if (result.Status is CaptureStatus.Lost or CaptureStatus.Unavailable)
                {
                    skipped++;
                    break;
                }

                skipped++;
                await Task.Delay(1).ConfigureAwait(false);
                continue;
            }

            var frame = result.Frame;
            packets.Clear();

            var started = Stopwatch.GetTimestamp();

            encoder.Encode(
                frame.Pixels,
                frame.Width,
                frame.Height,
                frame.Stride,
                frame.CapturedAtUtc.ToUnixTimeMilliseconds(),
                packets);

            var elapsed = Stopwatch.GetElapsedTime(started);

            encoded++;
            encodeMs += elapsed.TotalMilliseconds;

            foreach (var packet in packets)
            {
                frames++;
                bytes += packet.Bytes.Length;

                if (packet.Flags.HasFlag(VideoPacketFlags.Keyframe))
                {
                    keyframes++;
                }
            }

            // Paced exactly as the real stream is, so the delivered rate here is the one the phone gets.
            await pacer.WaitAsync(interval, CancellationToken.None).ConfigureAwait(false);
        }

        if (encoder is MediaFoundationVideoEncoder detailed)
        {
            var (convert, wait, submit) = detailed.StageTimings();
            Console.WriteLine(
                $"    [{plan.Label}] capture wait {captureMs / Math.Max(1, encoded):F1} ms, convert {convert:F1} ms, "
                + $"encoder wait {wait:F1} ms, submit {submit:F1} ms");
        }

        // The converter is measured separately against the same frame size, because inside the
        // encode timing above the two are one number and the phone-side answer to "why is this
        // slow" depends on which of them it is.
        convertMs = MeasureConversion(display, width, height);

        var duration = deadline.Elapsed.TotalSeconds;

        return new Measurement(
            frames,
            duration <= 0 ? 0 : frames / duration,
            duration <= 0 ? 0 : bytes * 8 / duration / 1_000_000,
            encoded == 0 ? 0 : encodeMs / encoded,
            convertMs,
            keyframes,
            skipped);
    }

    /// <summary>
    /// The scaler and colour converter alone, with nothing else in the process.
    /// <para>
    /// Separate from the full run because the full run has a live Desktop Duplication and a hardware
    /// encoder in it, and when the total moves it has to be possible to say which half moved.
    /// </para>
    /// </summary>
    private static void BenchmarkConversion()
    {
        var sizes = new (int Width, int Height)[] { (640, 360), (960, 540), (1280, 720), (1920, 1080) };

        const int SourceWidth = 1920;
        const int SourceHeight = 1080;

        var source = new byte[SourceWidth * SourceHeight * 4];
        Random.Shared.NextBytes(source);

        Console.WriteLine("  1920x1080 BGRA to NV12, 40 runs each");
        Console.WriteLine();
        Console.WriteLine("  {0,-12} {1,10} {2,10} {3,12}", "target", "best", "median", "MB/s read");
        Console.WriteLine("  " + new string('-', 48));

        foreach (var (width, height) in sizes)
        {
            var converter = new Nv12Converter();
            var destination = new byte[Nv12Converter.FrameBytes(width, height)];
            var samples = new double[40];

            converter.Convert(source, SourceWidth, SourceHeight, SourceWidth * 4, destination, width, height);

            for (var i = 0; i < samples.Length; i++)
            {
                var started = Stopwatch.GetTimestamp();
                converter.Convert(source, SourceWidth, SourceHeight, SourceWidth * 4, destination, width, height);
                samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            Array.Sort(samples);

            Console.WriteLine(
                "  {0,-12} {1,10} {2,10} {3,12:F0}",
                $"{width}x{height}",
                $"{samples[0]:F2} ms",
                $"{samples[samples.Length / 2]:F2} ms",
                source.Length / 1024.0 / 1024.0 / (samples[0] / 1000));
        }

        Console.WriteLine();
    }

    private static double MeasureConversion(DisplayInfo display, int width, int height)
    {
        var converter = new Nv12Converter();
        var source = new byte[display.Width * display.Height * 4];
        var destination = new byte[Nv12Converter.FrameBytes(width, height)];

        // A gradient rather than zeros: a constant image is the one input where a compiler or a
        // cache makes the measurement meaningless.
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (byte)i;
        }

        converter.Convert(source, display.Width, display.Height, display.Width * 4, destination, width, height);

        // The best of twenty rather than the mean of ten. This is a developer machine with a browser
        // and two chat clients on it, and an average here measures how busy the machine was; the
        // minimum measures how long the work takes when it is allowed to run, which is the number
        // that predicts behaviour on an idle PC streaming a desktop.
        var best = double.MaxValue;

        for (var i = 0; i < 20; i++)
        {
            var started = Stopwatch.GetTimestamp();
            converter.Convert(source, display.Width, display.Height, display.Width * 4, destination, width, height);
            best = Math.Min(best, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return best;
    }

    /// <summary>Scales to fit the target box, keeping the aspect ratio and never enlarging.</summary>
    private static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);

        if (scale >= 1)
        {
            return VideoEncoderSettings.Even(width, height);
        }

        return VideoEncoderSettings.Even((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private static int Argument(string[] args, string name, int fallback)
    {
        var prefix = name + "=";
        var match = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));

        return match is not null && int.TryParse(match[prefix.Length..], out var value) && value > 0
            ? value
            : fallback;
    }

    private sealed record Plan(string Label, int Width, int Height, int Fps, int Kbps);

    private sealed record Measurement(
        long Frames,
        double Fps,
        double Megabits,
        double EncodeMs,
        double ConvertMs,
        long Keyframes,
        long Skipped);
}
