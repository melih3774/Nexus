using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.NativeInput;
using PcCore.Windows.Capture;

namespace PcCore.Host;

/// <summary>
/// Proves, on this machine, that the Secure Desktop can actually be photographed.
/// <para>
/// Every other part of the capture story can be tested against a fake. This one cannot: the whole
/// question is whether Windows lets a LocalSystem process in the console session read the Winlogon
/// desktop, and no amount of unit testing answers it. So this raises a real consent prompt, waits
/// for the desktop to change, asks the helper for a frame, and writes the frame to a PNG the owner
/// can look at.
/// </para>
/// <para>
/// It also reports whether the frame is uniform — an entirely black image is the specific failure
/// this test exists to catch, because it is the one that looks like success everywhere else.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SecureCaptureSelfTest
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("SecureCaptureSelfTest");
        var output = args.SkipWhile(a => a != "--selftest-secure-capture").Skip(1)
                         .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(Path.GetTempPath(), "nexus-secure-desktop.png");

        Console.WriteLine();
        Console.WriteLine("Secure Desktop capture");
        Console.WriteLine("======================");
        Console.WriteLine();

        if (!SecureDesktopCapture.IsHelperListening())
        {
            Console.WriteLine("  FAIL  the Nexus Secure Desktop Capture service is not running.");
            Console.WriteLine();
            Console.WriteLine("  Install it once, from an elevated terminal:");
            Console.WriteLine("    PcCore.SecureCapture.exe --install");
            return 2;
        }

        Console.WriteLine("  PASS  the capture service is listening.");

        using var capture = new SecureDesktopCapture(loggers.CreateLogger<SecureDesktopCapture>());

        // The plumbing first, on whatever desktop is live now. If a SYSTEM worker cannot be started
        // and cannot hand back a frame of the ordinary desktop, nothing about the Secure Desktop is
        // worth testing yet - and separating the two is what turns one FAIL into a diagnosis.
        var warmUp = capture.TryCapture();

        Console.WriteLine(warmUp is null
            ? $"  FAIL  the worker produced no frame of the current desktop: {capture.Health.Diagnostic}"
            : $"  PASS  the worker captured the current desktop: {warmUp.Width}x{warmUp.Height}, {capture.Health.Diagnostic}.");

        if (warmUp is null)
        {
            capture.Release();
            return 5;
        }

        if (args.Contains("--now"))
        {
            Save(warmUp.Pixels, warmUp.Width, warmUp.Height, warmUp.Stride, output, logger);
            Console.WriteLine($"  Saved: {output}");
            capture.Release();
            return 0;
        }

        // A consent prompt, raised by something harmless. Approving or cancelling it are equally
        // good; what matters is the few seconds while it is on screen.
        Console.WriteLine();
        Console.WriteLine("  A UAC prompt is about to appear. Leave it up for a moment, then answer it.");
        Console.WriteLine();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit",
                Verb = "runas",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("  NOTE  the prompt was dismissed before it appeared; the test continues to wait.");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var sawSecure = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var desktop = InputDesktop.Current();

            if (desktop.IsInteractive)
            {
                await Task.Delay(150).ConfigureAwait(false);
                continue;
            }


            sawSecure = true;
            Console.WriteLine($"  PASS  the input desktop is {desktop.Name ?? desktop.Kind.ToString()}.");

            var frame = capture.TryCapture();

            if (frame is null)
            {
                Console.WriteLine($"  FAIL  no frame: {capture.Health.Diagnostic}");
                await Task.Delay(400).ConfigureAwait(false);
                continue;
            }

            Console.WriteLine($"  PASS  captured {frame.Width}x{frame.Height}, {capture.Health.Diagnostic}.");

            var uniform = IsUniform(frame.Pixels, frame.Stride, frame.Width, frame.Height);

            Console.WriteLine(uniform
                ? "  FAIL  the frame is a single flat colour, which means Windows handed over nothing."
                : "  PASS  the frame has real content.");

            Save(frame.Pixels, frame.Width, frame.Height, frame.Stride, output, logger);
            Console.WriteLine($"  Saved: {output}");

            capture.Release();
            return uniform ? 3 : 0;
        }

        Console.WriteLine(sawSecure
            ? "  FAIL  the Secure Desktop appeared but no frame could be captured."
            : "  FAIL  the Secure Desktop never appeared.");

        capture.Release();
        return 4;
    }

    /// <summary>
    /// True when every pixel is the same. Sampled rather than exhaustive: a black 4K frame is black
    /// everywhere, and reading two hundred points answers the question as well as reading eight
    /// million.
    /// </summary>
    private static bool IsUniform(byte[] pixels, int stride, int width, int height)
    {
        var first = BitConverter.ToUInt32(pixels, 0) & 0x00FFFFFF;

        for (var i = 0; i < 401; i++)
        {
            var x = (int)((long)i * 7919 % width);
            var y = (int)((long)i * 6271 % height);
            var offset = (y * stride) + (x * 4);

            if ((BitConverter.ToUInt32(pixels, offset) & 0x00FFFFFF) != first)
            {
                return false;
            }
        }

        return true;
    }

    private static void Save(byte[] pixels, int width, int height, int stride, string path, ILogger logger)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            using var bitmap = new Bitmap(
                width, height, stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());

            bitmap.Save(path, ImageFormat.Png);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException)
        {
            logger.LogWarning(ex, "The captured frame could not be written to {Path}.", path);
        }
        finally
        {
            handle.Free();
        }
    }
}
