using System.Runtime.InteropServices;
using System.Text;

namespace PcCore.InputBroker;

/// <summary>
/// The broker's whole logging story: a small rolling text file, plus the console when there is one.
/// <para>
/// Deliberately not a logging framework. This process runs elevated, and a logging pipeline is a
/// configuration surface, a plugin surface and a file-path surface all at once. What is actually
/// needed is the ability to answer "did it start, who connected, and why did it refuse" after the
/// fact — which is one append-only file in the owner's own local app data.
/// </para>
/// <para>
/// <b>Nothing about the input itself is ever written.</b> No coordinates, no key codes, no text.
/// The broker sees everything the owner types; a log that recorded it would be a keylogger with a
/// friendly name.
/// </para>
/// </summary>
internal static class BrokerLog
{
    private const long MaxBytes = 256 * 1024;

    private static readonly Lock Gate = new();
    private static string? _path;
    private static bool _console;

    public static void Open(bool attachConsole)
    {
        if (attachConsole && AttachConsole(AttachParentProcess))
        {
            _console = true;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexus");

            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "input-broker.log");

            // Rolled by truncation rather than by rotation. One generation of history is all this
            // ever needs, and a log that grows without bound on a machine nobody administers is a
            // slow-motion disk-space bug.
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";

        lock (Gate)
        {
            if (_console)
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }

            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _path = null;
            }
        }
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
