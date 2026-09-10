using System.IO.Pipes;
using System.Text;

namespace PcCore.SecureCapture;

/// <summary>
/// The capture worker: a SYSTEM process running inside the console session, on the Winlogon desktop.
/// <para>
/// It exists because desktops are per-session objects and the Winlogon desktop's DACL grants access
/// to SYSTEM alone. A service in session 0 cannot open session 1's window station at all, and a
/// process running as the logged-on user — elevated or not — cannot open the Secure Desktop, by
/// design. Windows offers no way around that and Nexus does not try to find one: it asks the
/// operating system for a process in the right place with the right identity, and that process takes
/// the picture.
/// </para>
/// <para>
/// <b>It talks to exactly one thing.</b> The frame pipe is created by PC Core, running as the
/// console user, with a name and a nonce PC Core chose; the worker is told both by the service and
/// connects outward. It never listens, never accepts a connection, and answers one question:
/// "what does the desktop that is receiving input look like right now". When the pipe closes, or
/// after a short idle period with nobody asking, it exits.
/// </para>
/// </summary>
internal static class Worker
{
    /// <summary>
    /// Long enough to sit through a UAC prompt the owner is reading, short enough that a worker
    /// whose PC Core died does not become a SYSTEM process nobody remembers starting.
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    public static int Run(string pipeName, string nonce)
    {
        if (!SecureCaptureProtocol.IsValidFramePipeName(pipeName))
        {
            return 2;
        }

        // Before anything measures the screen. Without it Windows DPI-virtualises this process and
        // GetSystemMetrics answers in logical pixels: on a display at 125% that is a 1536x864
        // picture of a 1920x1080 screen, upscaled by Windows and blurry by the time it reaches the
        // phone. PC Core does the same thing at its own start, for the same reason.
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);

        try
        {
            pipe.Connect(10_000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return 3;
        }

        var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
        var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);

        var nonceBytes = Convert.FromBase64String(nonce);

        writer.Write(SecureCaptureProtocol.FrameMagic);
        writer.Write(SecureCaptureProtocol.Version);
        writer.Write(nonceBytes.Length);
        writer.Write(nonceBytes);
        writer.Flush();

        using var capture = new DesktopCapture();
        var lastRequest = DateTimeOffset.UtcNow;

        while (true)
        {
            byte command;

            try
            {
                command = reader.ReadByte();
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
            {
                return 0;
            }

            if (command == SecureCaptureProtocol.Commands.Stop)
            {
                return 0;
            }

            if (command != SecureCaptureProtocol.Commands.Capture)
            {
                return 4;
            }

            if (DateTimeOffset.UtcNow - lastRequest > Idle)
            {
                return 0;
            }

            lastRequest = DateTimeOffset.UtcNow;

            try
            {
                Answer(writer, capture);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return 0;
            }
        }
    }

    private const nint PerMonitorAwareV2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);

    private static void Answer(BinaryWriter writer, DesktopCapture capture)
    {
        var frame = capture.TryCapture(out var diagnostic);

        if (frame is null)
        {
            var reason = Encoding.UTF8.GetBytes(diagnostic);

            writer.Write(SecureCaptureProtocol.Replies.Unavailable);
            writer.Write(reason.Length);
            writer.Write(reason);
            writer.Flush();
            return;
        }

        var desktop = Encoding.UTF8.GetBytes(frame.Desktop);

        writer.Write(SecureCaptureProtocol.Replies.Frame);
        writer.Write(frame.Width);
        writer.Write(frame.Height);
        writer.Write(frame.Stride);
        writer.Write(frame.CursorX);
        writer.Write(frame.CursorY);
        writer.Write(frame.CursorVisible);
        writer.Write(desktop.Length);
        writer.Write(desktop);
        writer.Write(frame.Pixels.Length);
        writer.Write(frame.Pixels);
        writer.Flush();
    }
}
