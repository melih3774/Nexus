using System.Diagnostics;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;
using PcCore.Windows.Media;

namespace PcCore.Host;

/// <summary>
/// Proves, on this machine, that the camera and the microphone actually produce live frames.
/// <para>
/// Everything else about live monitoring can be tested against a fake: the authorisation rules, the
/// stop-on-disconnect behaviour, the indicator override. This part cannot. Whether
/// <c>MediaCapture</c> opens this webcam, whether Windows' privacy settings allow it, and whether
/// <c>AudioGraph</c> yields samples from this microphone are questions only this hardware answers —
/// and they are exactly the questions that fail quietly, because a stream that produces no frames
/// looks identical to a stream nobody is watching.
/// </para>
/// <para>
/// It deliberately does not need a phone. The end-to-end path needs an owner-authenticated session,
/// which needs the real Android client; this isolates the half that lives on the PC so a failure can
/// be attributed to one side or the other.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class LiveSensorSelfTest
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggers)
    {
        var seconds = Seconds(args);

        Console.WriteLine();
        Console.WriteLine("Live camera and microphone");
        Console.WriteLine("==========================");
        Console.WriteLine();
        Console.WriteLine($"  Capturing for {seconds} second(s) from each device.");
        Console.WriteLine("  Windows should show its own camera and microphone in-use indicators.");
        Console.WriteLine();

        var camera = new WindowsLiveCamera(loggers.CreateLogger<WindowsLiveCamera>());
        var microphone = new WindowsLiveMicrophone(loggers.CreateLogger<WindowsLiveMicrophone>());

        var cameraOk = await MeasureAsync("camera", camera, seconds, Describe.Camera).ConfigureAwait(false);
        Console.WriteLine();
        var micOk = await MeasureAsync("microphone", microphone, seconds, Describe.Microphone).ConfigureAwait(false);

        Console.WriteLine();

        if (cameraOk && micOk)
        {
            Console.WriteLine("  PASS  both devices produced live frames.");
            return 0;
        }

        Console.WriteLine("  Nothing here changes Windows privacy settings. If a device produced no");
        Console.WriteLine("  frames, check Settings > Privacy & security > Camera / Microphone, and");
        Console.WriteLine("  whether another application is holding the device.");

        return cameraOk || micOk ? 3 : 4;
    }

    private enum Describe
    {
        Camera,
        Microphone,
    }

    private static async Task<bool> MeasureAsync(
        string name,
        ILiveSensorSource source,
        int seconds,
        Describe describe)
    {
        if (!await source.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Console.WriteLine($"  SKIP  no {name} is present on this machine.");
            return true;
        }

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        var frames = 0;
        long bytes = 0;
        var first = TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        var detail = string.Empty;

        try
        {
            await foreach (var frame in source.CaptureAsync(stopping.Token).ConfigureAwait(false))
            {
                if (frames == 0)
                {
                    // Time to first frame is the number that matters for "reasonable latency": it is
                    // how long the owner stares at a spinner after pressing the button.
                    first = clock.Elapsed;

                    detail = describe == Describe.Camera
                        ? $"{frame.WidthOrSampleRate}x{frame.HeightOrChannels} JPEG"
                        : $"{frame.WidthOrSampleRate} Hz, {frame.HeightOrChannels} channel(s), 16-bit PCM";
                }

                frames++;
                bytes += frame.Payload.Length;
            }
        }
        catch (OperationCanceledException)
        {
            // The measurement window closed, which is how this ends.
        }

        clock.Stop();

        if (frames == 0)
        {
            Console.WriteLine($"  FAIL  the {name} produced no frames in {seconds}s.");
            return false;
        }

        var rate = frames / Math.Max(clock.Elapsed.TotalSeconds, 0.001);

        Console.WriteLine($"  PASS  the {name} produced {frames} frame(s): {detail}.");
        Console.WriteLine(
            $"        {rate:F1}/s, {bytes / 1024.0:F0} KB total, first frame after {first.TotalMilliseconds:F0} ms.");

        return true;
    }

    private static int Seconds(string[] args)
    {
        var raw = args.SkipWhile(a => a != "--selftest-live-sensors").Skip(1).FirstOrDefault();

        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, 30) : 4;
    }
}
