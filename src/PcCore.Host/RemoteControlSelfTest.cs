using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Windows;
using PcCore.Windows.Input;

namespace PcCore.Host;

/// <summary>
/// Exercises live capture, encoding, input injection and the indicator against this actual machine.
/// <para>
/// It exists because everything else in v0.6 can be tested with fakes, and the three things that
/// cannot are precisely the three that talk to Windows. A green unit suite says the pipeline is
/// correct; this says the desktop can really be duplicated here, that a JPEG comes out at a usable
/// size and rate, that <c>SendInput</c> actually moves the pointer, and that the badge appears.
/// </para>
/// <para>
/// It is a development tool, not a capability: it runs from the command line, on this PC, by the
/// owner. Nothing remote can reach it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class RemoteControlSelfTest
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var seconds = ParseSeconds(args);
        var failures = 0;

        void Step(string what, bool ok, string? detail = null)
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {what}{(detail is null ? "" : $"  [{detail}]")}");
        }

        Console.WriteLine();
        Console.WriteLine("  Nexus remote-control self-test");
        Console.WriteLine();

        // Without this the whole test runs against DPI-virtualised coordinates and proves nothing
        // about what PC Core does.
        Step("per-monitor DPI awareness", DpiAwareness.TryEnablePerMonitorV2());

        // --- Displays ------------------------------------------------------------------------------
        var stream = new DxgiDesktopStream(loggerFactory);
        var displays = stream.GetDisplays();

        Step($"enumerate displays ({displays.Count})", displays.Count > 0);

        foreach (var display in displays)
        {
            Console.WriteLine($"          {display}");
        }

        if (displays.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  No display could be duplicated; the rest of the test cannot run.");
            return 1;
        }

        // --- Capture and encode --------------------------------------------------------------------
        using var encoder = new JpegFrameEncoder();

        // Overridable so the defaults can be chosen from measurements rather than guesses.
        var options = new StreamOptions
        {
            MaxWidth = ParseInt(args, "--width", new StreamOptions().MaxWidth),
            MaxHeight = ParseInt(args, "--height", new StreamOptions().MaxHeight),
            Quality = ParseInt(args, "--quality", new StreamOptions().Quality),
            FallbackFps = ParseInt(args, "--fps", new StreamOptions().FallbackFps),
        };

        IDesktopCaptureSource? source = null;

        try
        {
            source = stream.Open(displays[0].Index);
            Step($"open duplication on {displays[0].Name}", true, $"{source.PixelWidth}x{source.PixelHeight} physical");
        }
        catch (Exception ex)
        {
            Step("open duplication", false, ex.Message);
            return 1;
        }

        var frames = 0;
        var timeouts = 0;
        var losses = 0;
        long totalBytes = 0;
        var largest = 0;
        var encodeTicks = 0L;
        var encodedWidth = 0;
        var encodedHeight = 0;

        var clock = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(seconds);

        Console.WriteLine();
        Console.WriteLine($"  Capturing for {seconds}s at up to {options.FallbackFps} fps, quality {options.Quality}, "
            + $"max {options.MaxWidth}x{options.MaxHeight}...");

        // Paced exactly as the real capture loop is, so the numbers mean something.
        var interval = TimeSpan.FromMilliseconds(1000.0 / options.FallbackFps);

        while (clock.Elapsed < deadline)
        {
            var started = clock.Elapsed;
            var result = source.TryGetFrame(TimeSpan.FromMilliseconds(options.FrameTimeoutMs));

            switch (result.Status)
            {
                case CaptureStatus.Frame:
                    var encodeStarted = Stopwatch.GetTimestamp();
                    var encoded = encoder.Encode(result.Frame!, options.MaxWidth, options.MaxHeight, options.Quality);
                    encodeTicks += Stopwatch.GetTimestamp() - encodeStarted;

                    frames++;
                    totalBytes += encoded.Bytes.Length;
                    largest = Math.Max(largest, encoded.Bytes.Length);
                    encodedWidth = encoded.Width;
                    encodedHeight = encoded.Height;
                    break;

                case CaptureStatus.Timeout:
                    timeouts++;
                    break;

                default:
                    losses++;
                    Console.WriteLine($"          capture {result.Status.ToString().ToLowerInvariant()}: {result.Diagnostic}");
                    source.Dispose();
                    source = stream.Open(displays[0].Index);
                    break;
            }

            var spent = clock.Elapsed - started;
            if (spent < interval)
            {
                await Task.Delay(interval - spent).ConfigureAwait(false);
            }
        }

        clock.Stop();
        source.Dispose();

        var elapsed = clock.Elapsed.TotalSeconds;
        var fps = frames / elapsed;
        var averageBytes = frames == 0 ? 0 : totalBytes / (double)frames;
        var encodeMs = frames == 0 ? 0 : encodeTicks * 1000.0 / Stopwatch.Frequency / frames;
        var megabits = totalBytes * 8 / elapsed / 1_000_000;

        Console.WriteLine();
        Step($"sustained capture ({frames} frames in {elapsed:F1}s)", frames > 0);

        Console.WriteLine($"          encoded size      {encodedWidth}x{encodedHeight}");
        Console.WriteLine($"          capture rate      {fps:F1} fps ({timeouts} idle, {losses} recovered loss)");
        Console.WriteLine($"          average frame     {averageBytes / 1024:F1} KB (largest {largest / 1024.0:F1} KB)");
        Console.WriteLine($"          encode time       {encodeMs:F1} ms/frame");
        Console.WriteLine($"          bandwidth         {megabits:F2} Mbit/s at this rate");

        // --- Frame header round trip ------------------------------------------------------------------
        var header = new StreamFrameHeader
        {
            Version = StreamFrameHeader.CurrentVersion,
            Encoding = FrameEncoding.Jpeg,
            Flags = FrameFlags.Complete | FrameFlags.CursorBakedIn,
            DisplayIndex = 0,
            StreamId = 7,
            Sequence = 42,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Width = (ushort)Math.Max(1, encodedWidth),
            Height = (ushort)Math.Max(1, encodedHeight),
            DesktopLeft = displays[0].Left,
            DesktopTop = displays[0].Top,
            DesktopWidth = displays[0].Width,
            DesktopHeight = displays[0].Height,
            CursorX = 100,
            CursorY = 200,
            CursorVisible = true,
            PayloadLength = 3,
        };

        var message = header.ToMessage([1, 2, 3]);
        Step(
            "binary frame header round trip",
            StreamFrameHeader.TryParse(message, out var parsed) && parsed == header,
            $"{message.Length} bytes");

        // --- Input ------------------------------------------------------------------------------------
        if (args.Contains("--skip-input"))
        {
            Console.WriteLine();
            Console.WriteLine("  Input injection skipped (--skip-input).");
        }
        else
        {
            Step("input injection", await TestInputAsync(loggerFactory));
        }

        // --- Indicator --------------------------------------------------------------------------------
        Step("indicator", TestIndicator(loggerFactory));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "  All self-test steps passed." : $"  {failures} step(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Moves the pointer a few pixels and puts it back.
    /// <para>
    /// Deliberately the smallest observable action that proves the whole path works: a relative move
    /// that Windows accepts, that actually changes the cursor position, and that restores it. Anything
    /// less would only prove <c>SendInput</c> returned a number.
    /// </para>
    /// </summary>
    private static async Task<bool> TestInputAsync(ILoggerFactory loggerFactory)
    {
        var controller = new WindowsInputController(
            new SystemDisplayGeometry(), loggerFactory.CreateLogger<WindowsInputController>());

        if (!WindowsInputController.TryGetPointerPosition(out var startX, out var startY))
        {
            Console.WriteLine("          the cursor position could not be read");
            return false;
        }

        var moved = controller.MovePointerRelative(4, 4);

        if (!moved.Succeeded)
        {
            Console.WriteLine($"          {moved.Diagnostic}");
            return false;
        }

        await Task.Delay(60).ConfigureAwait(false);
        WindowsInputController.TryGetPointerPosition(out var movedX, out var movedY);

        controller.MovePointerRelative(-4, -4);
        await Task.Delay(60).ConfigureAwait(false);
        WindowsInputController.TryGetPointerPosition(out var restoredX, out var restoredY);

        var actuallyMoved = movedX != startX || movedY != startY;
        var restored = Math.Abs(restoredX - startX) <= 1 && Math.Abs(restoredY - startY) <= 1;

        Console.WriteLine(
            $"          pointer ({startX},{startY}) -> ({movedX},{movedY}) -> ({restoredX},{restoredY})");

        return actuallyMoved && restored;
    }

    private static bool TestIndicator(ILoggerFactory loggerFactory)
    {
        using var indicator = new WindowsRemoteIndicator(loggerFactory.CreateLogger<WindowsRemoteIndicator>());
        var input = new InputService(
            new WindowsInputController(
                new SystemDisplayGeometry(), loggerFactory.CreateLogger<WindowsInputController>()),
            new DxgiDesktopStream(loggerFactory),
            new NullNexusInputAttribution(),
            new InputOptions(),
            loggerFactory.CreateLogger<InputService>());

        using var service = new RemoteIndicatorService(
            indicator, input, StateDirectory.ForCurrentUser(), loggerFactory.CreateLogger<RemoteIndicatorService>());

        var original = service.Mode;

        try
        {
            foreach (var mode in new[] { IndicatorMode.Standard, IndicatorMode.Minimal, IndicatorMode.AlwaysVisible })
            {
                service.SetMode(mode);
                service.SetViewers(1);
                service.SetInputSessions(mode == IndicatorMode.Standard ? 1 : 0);

                var state = service.Current;
                Console.WriteLine(
                    $"          {mode,-15} overlay={state.Overlay,-5} tray={state.Tray,-5} \"{state.Text}\"");

                // Long enough to be visible if someone is watching the screen while this runs.
                Thread.Sleep(700);
            }

            service.SetViewers(0);
            service.SetInputSessions(0);

            var idle = service.Current == IndicatorState.Idle;
            Console.WriteLine($"          cleared         overlay={service.Current.Overlay} tray={service.Current.Tray}");

            return idle;
        }
        finally
        {
            // The owner's preference is theirs; a diagnostic must not change it.
            service.SetMode(original);
        }
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }

    private static int ParseSeconds(string[] args)
    {
        var index = Array.IndexOf(args, "--seconds");

        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds)
            ? Math.Clamp(seconds, 1, 300)
            : 10;
    }
}
