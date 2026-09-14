using System.Text;

namespace PcAssistant.Core.Logging;

/// <summary>
/// Per-task raw provider log. Bounded on purpose: a runaway Claude process must not be
/// able to fill the disk. When the cap is reached the log rotates once to <c>.1</c> and
/// the current file starts over, so the most recent output is always the one kept.
/// </summary>
public sealed class RawLogWriter : IDisposable
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    public RawLogWriter(string path, long maxBytes = 8L * 1024 * 1024)
    {
        _path = path;
        _maxBytes = maxBytes > 0 ? maxBytes : 1024;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Open(append: true);
    }

    public string FilePath => _path;
    public long BytesWritten { get { lock (_gate) return _written; } }

    private void Open(bool append)
    {
        _writer?.Dispose();
        var fs = new FileStream(_path, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        _written = append && File.Exists(_path) ? new FileInfo(_path).Length : 0;
    }

    public void Write(string channel, string line)
    {
        if (_disposed) return;

        var stamped = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{channel}] {line}";
        lock (_gate)
        {
            if (_writer is null) return;

            if (_written + stamped.Length + 2 > _maxBytes) Rotate();

            _writer.WriteLine(stamped);
            _written += stamped.Length + 2;
        }
    }

    private void Rotate()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;

            var rotated = _path + ".1";
            if (File.Exists(rotated)) File.Delete(rotated);
            if (File.Exists(_path)) File.Move(_path, rotated);
        }
        catch
        {
            // Rotation is best-effort; losing history must never break a task.
        }
        finally
        {
            Open(append: false);
            _writer?.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [pcassistant] --- log rotated (cap {_maxBytes} bytes) ---");
        }
    }

    /// <summary>Reads back the tail of the log for the raw view, without loading the whole file.</summary>
    public static string ReadTail(string path, int maxChars = 200_000)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxChars) fs.Seek(-maxChars, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "(raw log is being written to; try again)";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
