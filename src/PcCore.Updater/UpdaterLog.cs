using System.Runtime.InteropServices;

namespace PcCore.Updater;

/// <summary>
/// Where the updater writes what it did: a file beside the rest of the update state, and the parent
/// console when it was started from one.
/// <para>
/// A file because the updater's whole job happens while PC Core - and with it PC Core's own log -
/// is stopped. When an update rolls back, this is the account of why.
/// </para>
/// </summary>
internal sealed class UpdaterLog
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const long MaxBytes = 1024 * 1024;

    private readonly string? _path;
    private readonly Lock _gate = new();
    private bool _console;

    public UpdaterLog(string? path, bool attachConsole)
    {
        _path = path;
        _console = attachConsole && AttachConsole(AttachParentProcess);

        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}";

        lock (_gate)
        {
            if (_console)
            {
                try
                {
                    Console.WriteLine(line);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Stopping an old PC Core borrows its console to send Ctrl+C, which leaves this
                    // process's console handle dead. The file is the record; the console was a courtesy.
                    _console = false;
                }
            }

            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is the updater's diary, not a reason to abandon an update half way.
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
