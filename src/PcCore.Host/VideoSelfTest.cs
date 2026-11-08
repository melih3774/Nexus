using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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

        if (args.Contains("--bitrate-change"))
        {
            return BitrateChange(factory);
        }

        if (args.Contains("--still-refresh"))
        {
            return StillRefresh(factory, args);
        }

        if (args.Contains("--gpu-scale"))
        {
            return GpuScale(args);
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
            var (convert, wait, submit, output, late) = detailed.StageTimings();
            Console.WriteLine(
                $"    [{plan.Label}] capture wait {captureMs / Math.Max(1, encoded):F1} ms, convert {convert:F1} ms, "
                + $"encoder wait {wait:F1} ms, submit {submit:F1} ms, output {output:F1} ms, {late} late");
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
    /// Whether this machine's encoder takes a new bitrate while running, and actually honours it.
    /// <para>
    /// The adaptive controller's cheapest step depends on it, and "the call returned S_OK" is not the
    /// same as "the rate controller listened". So the answer is measured: a detailed moving picture
    /// that no encoder can squeeze below its target, five seconds at one bitrate, a change in place,
    /// five seconds at the other, and the bits that came out of each.
    /// </para>
    /// </summary>
    private static int BitrateChange(IVideoEncoderFactory factory)
    {
        const int Width = 1280;
        const int Height = 720;
        const int Fps = 30;
        const int HighKbps = 4000;
        const int LowKbps = 1000;
        const int FramesPerRun = Fps * 5;
        const int WarmUp = Fps;

        // Low-contrast grain that is different in every frame, cycled from a few prepared pictures.
        // Hard enough that the encoder has to decide how many bits to spend on every frame, and soft
        // enough that it can meet either target. Noise at full contrast turned out to be beyond any
        // target - a new encoder built for one megabit produced nine - which says nothing about
        // whether a change was honoured.
        var random = new Random(7);
        var grain = new byte[8][];

        for (var g = 0; g < grain.Length; g++)
        {
            grain[g] = new byte[Width * Height * 4];
            random.NextBytes(grain[g]);

            for (var b = 0; b < grain[g].Length; b++)
            {
                grain[g][b] = (byte)(112 + (grain[g][b] >> 5));
            }
        }

        var frame = new byte[Width * Height * 4];
        var packets = new List<VideoPacket>();
        var origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var index = 0;

        void Paint() => grain[index % grain.Length].CopyTo(frame, 0);

        double Run(IVideoEncoder encoder)
        {
            long bytes = 0;
            var measured = 0;

            for (var i = 0; i < FramesPerRun; i++, index++)
            {
                Paint();
                packets.Clear();
                encoder.Encode(frame, Width, Height, Width * 4, origin + (index * 1000L / Fps), packets);

                foreach (var packet in packets)
                {
                    if (i >= WarmUp)
                    {
                        bytes += packet.Bytes.Length;
                        measured++;
                    }
                }
            }

            return measured == 0 ? 0 : bytes * 8.0 * Fps / measured / 1000;
        }

        double high;
        double down;
        double up;
        bool accepted;
        string name;

        using (var encoder = factory.Create(new VideoEncoderSettings(Width, Height, Fps, HighKbps, 2)))
        {
            name = encoder.BackendName;
            encoder.RequestKeyframe();
            high = Run(encoder);

            accepted = encoder.TryChangeBitrate(LowKbps);
            down = Run(encoder);

            accepted &= encoder.TryChangeBitrate(HighKbps);
            up = Run(encoder);
        }

        // The control: a new encoder built at the lower rate, on the same kind of picture. If this one
        // cannot get down to the target either, the content is the limit rather than the change.
        double freshLow;

        using (var encoder = factory.Create(new VideoEncoderSettings(Width, Height, Fps, LowKbps, 2)))
        {
            encoder.RequestKeyframe();
            freshLow = Run(encoder);
        }

        Console.WriteLine($"  {name}, 1280x720 at 30 fps, changing grain, first second of each run excluded:");
        Console.WriteLine($"    built at {HighKbps} kbit/s:               {high,5:F0} kbit/s produced");
        Console.WriteLine($"    changed in place to {LowKbps} kbit/s:     {down,5:F0} kbit/s produced");
        Console.WriteLine($"    changed in place back to {HighKbps} kbit/s: {up,5:F0} kbit/s produced");
        Console.WriteLine($"    control, a new encoder at {LowKbps} kbit/s: {freshLow,5:F0} kbit/s produced");
        Console.WriteLine();

        if (!accepted)
        {
            Console.WriteLine("  FAIL  the encoder refused the change; adaptive steps will rebuild it instead.");
            return 1;
        }

        if (high <= 0 || freshLow > high * 0.6)
        {
            Console.WriteLine("  INCONCLUSIVE  even a new encoder could not reach the lower rate on this content.");
            return 1;
        }

        var midpoint = (high + freshLow) / 2;

        if (down > midpoint || up < midpoint)
        {
            Console.WriteLine("  FAIL  the change was accepted but the output bitrate did not follow it.");
            return 1;
        }

        Console.WriteLine("  PASS  the running encoder follows a new bitrate, down and up, without being rebuilt.");
        return 0;
    }

    /// <summary>
    /// What this machine's encoder does with the same picture, submitted again and again.
    /// <para>
    /// Whether a still desktop gets sharper by re-encoding it is a property of the encoder's rate
    /// control and cannot be read off a data sheet. So: a second of scrolling text at the plan's
    /// bitrate, which leaves the rate controller at the coarse quantiser a real scroll does, then one
    /// picture held still, and how many bytes every repeat of it costs. Bytes say that the encoder is
    /// still refining; <c>--dump=directory</c> writes the bitstream and the reference picture, so that
    /// a decoder can say whether what it spent them on is visible.
    /// </para>
    /// <para>
    /// The pictures are drawn rather than captured, so the numbers can be compared between runs and a
    /// dump never contains what happened to be on the owner's screen.
    /// </para>
    /// </summary>
    private static int StillRefresh(IVideoEncoderFactory factory, string[] args)
    {
        const int SourceWidth = 2560;
        const int SourceHeight = 1440;
        const int Fps = 30;
        const int ScrollFrames = Fps;
        const int Repeats = 150;

        var dump = args.FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];
        var requested = args.FirstOrDefault(a => a.StartsWith("--plan=", StringComparison.Ordinal));

        var plans = requested is null
            ? new List<Plan> { new("720p", 1280, 720, Fps, 2500), new("1080p", 1920, 1080, Fps, 5000) }
            : [Parse(requested["--plan=".Length..], new DisplayInfo(0, "drawn", 0, 0, SourceWidth, SourceHeight, true))];

        var held = DrawTextPage(SourceWidth, SourceHeight, 0, 1);
        var gopSeconds = Argument(args, "--gop", MediaPlanner.GopSeconds);
        var ok = true;

        foreach (var plan in plans)
        {
            var (width, height) = Fit(SourceWidth, SourceHeight, plan.Width, plan.Height);
            using var encoder = factory.Create(new VideoEncoderSettings(width, height, Fps, plan.Kbps, gopSeconds));

            var packets = new List<VideoPacket>();
            var stream = new List<byte[]>();
            var sizes = new List<(int Bytes, bool Keyframe)>();
            var origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var clock = 0L;

            void Submit(byte[] picture, bool still)
            {
                packets.Clear();
                encoder.Encode(picture, SourceWidth, SourceHeight, SourceWidth * 4, origin + (clock++ * 1000 / Fps), packets);

                foreach (var packet in packets)
                {
                    stream.Add(packet.Bytes);

                    if (packet.Flags.HasFlag(VideoPacketFlags.Keyframe))
                    {
                        Console.WriteLine($"    keyframe at output #{stream.Count - 1}");
                    }

                    if (still)
                    {
                        sizes.Add((packet.Bytes.Length, packet.Flags.HasFlag(VideoPacketFlags.Keyframe)));
                    }
                }
            }

            encoder.RequestKeyframe();

            for (var i = 0; i < ScrollFrames; i++)
            {
                Submit(DrawTextPage(SourceWidth, SourceHeight, i * 18, 2), still: false);
            }

            var beforeStill = stream.Count;

            for (var i = 0; i <= Repeats; i++)
            {
                Submit(held, still: true);
            }

            Console.WriteLine($"  {plan.Label} {width}x{height} at {plan.Kbps} kbit/s: {encoder.BackendName}");
            Console.WriteLine("    repeat   bytes  keyframe");

            for (var i = 0; i < sizes.Count; i++)
            {
                if (i < 12 || i % 10 == 0 || sizes[i].Keyframe)
                {
                    Console.WriteLine($"    {i,6} {sizes[i].Bytes,7}  {(sizes[i].Keyframe ? "yes" : "")}");
                }
            }

            Console.WriteLine($"    {sizes.Sum(s => s.Bytes) / 1024} KB over {sizes.Count} still frames.");
            Console.WriteLine($"    {ScrollFrames + Repeats + 1} frames in, {stream.Count} packets out.");

            if (encoder is MediaFoundationVideoEncoder detailed)
            {
                var (convert, wait, submit, output, late) = detailed.StageTimings();
                Console.WriteLine(
                    $"    convert {convert:F1} ms, encoder wait {wait:F1} ms, submit {submit:F1} ms, "
                    + $"waiting for output {output:F1} ms, {late} packets missed their frame.");
            }

            // A long GOP is only safe if a keyframe can still be had on demand.
            var forced = 0;
            encoder.RequestKeyframe();

            for (var i = 0; i < 3; i++)
            {
                packets.Clear();
                encoder.Encode(held, SourceWidth, SourceHeight, SourceWidth * 4, origin + (clock++ * 1000 / Fps), packets);
                forced += packets.Count(p => p.Flags.HasFlag(VideoPacketFlags.Keyframe));
            }

            Console.WriteLine(forced > 0
                ? "    a keyframe asked for mid-stream arrived."
                : "    FAIL  a keyframe asked for mid-stream did not arrive.");

            ok &= forced > 0;
            Console.WriteLine();

            if (dump is not null)
            {
                Directory.CreateDirectory(dump);

                File.WriteAllBytes(
                    Path.Combine(dump, $"{plan.Label}.h264"),
                    [.. stream.SelectMany(static bytes => bytes)]);

                var reference = new byte[Nv12Converter.FrameBytes(width, height)];
                new Nv12Converter().Convert(held, SourceWidth, SourceHeight, SourceWidth * 4, reference, width, height);
                File.WriteAllBytes(Path.Combine(dump, $"{plan.Label}.nv12"), reference);

                Console.WriteLine($"    {beforeStill} scrolling frames precede the still ones in {plan.Label}.h264.");
                Console.WriteLine();
            }
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// The driver's video processor against <see cref="Nv12Converter"/>, on the same drawn page.
    /// <para>
    /// Whether the stream should leave the CPU depends on two numbers and a picture: how much time it
    /// saves, how far the driver's scaler lands from the converter's area average, and what small
    /// text looks like afterwards. <c>--dump=directory</c> writes a magnified crop of each.
    /// </para>
    /// </summary>
    private static int GpuScale(string[] args)
    {
        const int Runs = 20;

        var dump = args.FirstOrDefault(a => a.StartsWith("--dump=", StringComparison.Ordinal))?["--dump=".Length..];

        var cases = new (int SourceWidth, int SourceHeight, int Width, int Height)[]
        {
            (1920, 1080, 1280, 720),
            (2560, 1440, 1280, 720),
            (2560, 1440, 1920, 1080),
            (3840, 2160, 1280, 720),
            (3840, 2160, 1920, 1080),
            (5120, 2880, 1280, 720),
            (5120, 2880, 1920, 1080),
        };

        Console.WriteLine(
            "  {0,-22} {1,10} {2,10} {3,9} {4,9} {5,9} {6,8} {7,8} {8,8}",
            "scale",
            "cpu now",
            "convert",
            "gpu spd",
            "gpu qual",
            "psnr s/q",
            "sharp c",
            "sharp s",
            "sharp q");
        Console.WriteLine("  " + new string('-', 112));

        foreach (var (sourceWidth, sourceHeight, width, height) in cases)
        {
            var page = DrawTextPage(sourceWidth, sourceHeight, 0, 1);
            var cpu = new byte[Nv12Converter.FrameBytes(width, height)];
            var fast = new byte[cpu.Length];
            var best = new byte[cpu.Length];
            var readBack = new byte[page.Length];
            var composed = new byte[page.Length];

            var converter = new Nv12Converter();
            var convert = new double[Runs];

            converter.Convert(page, sourceWidth, sourceHeight, sourceWidth * 4, cpu, width, height);

            for (var i = 0; i < Runs; i++)
            {
                var started = Stopwatch.GetTimestamp();
                converter.Convert(page, sourceWidth, sourceHeight, sourceWidth * 4, cpu, width, height);
                convert[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            double median(double[] values)
            {
                Array.Sort(values);
                return values[values.Length / 2];
            }

            // What the frame costs today before the converter starts: out of the GPU, then the second
            // copy the pointer is composited into.
            double capture;
            double spd;
            double qual;
            string adapter;

            using (var scaler = D3D11FrameScaler.Create(sourceWidth, sourceHeight, width, height, optimalQuality: false))
            {
                adapter = scaler.Adapter;
                scaler.Upload(page);

                var samples = new double[Runs];
                var captures = new double[Runs];

                for (var i = 0; i < Runs; i++)
                {
                    samples[i] = scaler.Process();

                    var started = Stopwatch.GetTimestamp();
                    scaler.ReadBackSource(readBack);
                    Buffer.BlockCopy(readBack, 0, composed, 0, readBack.Length);
                    captures[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                spd = median(samples);
                capture = median(captures);
                scaler.ReadBackResult(fast);
            }

            using (var scaler = D3D11FrameScaler.Create(sourceWidth, sourceHeight, width, height, optimalQuality: true))
            {
                scaler.Upload(page);

                var samples = new double[Runs];

                for (var i = 0; i < Runs; i++)
                {
                    samples[i] = scaler.Process();
                }

                qual = median(samples);
                scaler.ReadBackResult(best);
            }

            var cpuMs = median(convert);

            Console.WriteLine(
                "  {0,-22} {1,10} {2,10} {3,9} {4,9} {5,9} {6,8:F1} {7,8:F1} {8,8:F1}",
                $"{sourceWidth}x{sourceHeight}>{width}x{height}",
                $"{capture + cpuMs:F1} ms",
                $"{cpuMs:F1} ms",
                $"{spd:F1} ms",
                $"{qual:F1} ms",
                $"{LumaPsnr(cpu, fast, width, height):F0}/{LumaPsnr(cpu, best, width, height):F0}",
                Sharpness(cpu, width, height),
                Sharpness(fast, width, height),
                Sharpness(best, width, height));

            if (dump is not null)
            {
                Directory.CreateDirectory(dump);
                SaveComparison(Path.Combine(dump, $"{sourceWidth}x{sourceHeight}-to-{height}.png"), width, cpu, fast, best);
            }

            if (cases[0] == (sourceWidth, sourceHeight, width, height))
            {
                Console.WriteLine($"  on {adapter}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  'cpu now' is the GPU readback, the copy for the pointer and the converter; 'convert' the");
        Console.WriteLine("  converter alone. 'psnr' is how close each GPU mode lands to the converter's area average, and");
        Console.WriteLine("  'sharp' the mean absolute Laplacian of luma: higher is crisper, or has more aliasing.");

        return 0;
    }

    private static double LumaPsnr(byte[] a, byte[] b, int width, int height)
    {
        double sum = 0;

        for (var i = 0; i < width * height; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * width * height / sum);
    }

    private static double Sharpness(byte[] nv12, int width, int height)
    {
        double sum = 0;

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var at = (y * width) + x;
                sum += Math.Abs((4 * nv12[at]) - nv12[at - 1] - nv12[at + 1] - nv12[at - width] - nv12[at + width]);
            }
        }

        return sum / ((width - 2) * (height - 2));
    }

    /// <summary>A crop of the luma of each picture, magnified, one above the other.</summary>
    private static void SaveComparison(string path, int width, params byte[][] pictures)
    {
        const int CropWidth = 300;
        const int CropHeight = 60;
        const int Zoom = 4;
        const int Gap = 6;

        var outputHeight = (pictures.Length * ((CropHeight * Zoom) + Gap)) - Gap;

        using var bitmap = new Bitmap(CropWidth * Zoom, outputHeight, PixelFormat.Format32bppArgb);

        for (var p = 0; p < pictures.Length; p++)
        {
            for (var y = 0; y < CropHeight; y++)
            {
                for (var x = 0; x < CropWidth; x++)
                {
                    var luma = pictures[p][((y + 10) * width) + x + 10];
                    var level = Math.Clamp((luma - 16) * 255 / 219, 0, 255);
                    var color = Color.FromArgb(level, level, level);

                    for (var zy = 0; zy < Zoom; zy++)
                    {
                        for (var zx = 0; zx < Zoom; zx++)
                        {
                            bitmap.SetPixel((x * Zoom) + zx, (p * ((CropHeight * Zoom) + Gap)) + (y * Zoom) + zy, color);
                        }
                    }
                }
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>A page of dense small text on white, shifted by <paramref name="scroll"/> pixels.</summary>
    private static byte[] DrawTextPage(int width, int height, int scroll, int seed)
    {
        const int LineHeight = 18;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 9.5f, GraphicsUnit.Point))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var random = new Random(seed);
            var words = "stream capture encoder keyframe bitrate display desktop owner pointer phone quality frame"
                .Split(' ');
            var firstLine = scroll / LineHeight;

            // Lines before the first visible one still consume the random sequence, so scrolling
            // moves the text rather than replacing it.
            for (var line = 0; line < firstLine + (height / LineHeight) + 2; line++)
            {
                var text = string.Join(' ', Enumerable.Range(0, 28).Select(_ => words[random.Next(words.Length)]));

                if (line >= firstLine)
                {
                    graphics.DrawString(text, font, Brushes.Black, 12, (line * LineHeight) - scroll);
                }
            }
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var pixels = new byte[width * height * 4];

            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * width * 4, width * 4);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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
