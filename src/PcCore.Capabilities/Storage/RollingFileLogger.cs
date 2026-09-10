using System.Text;

using Microsoft.Extensions.Logging;

namespace PcCore.Capabilities.Storage;

/// <summary>
/// PC Core's log, on disk, so it still exists after the window that printed it is gone.
/// <para>
/// <b>Why this had to exist.</b> PC Core is started from the Start Menu, from the tray, and at
/// sign-in — and in none of those cases is there a console attached. Every diagnostic it writes
/// went nowhere, which meant the one question the owner actually asks after something breaks
/// ("why did my phone stop connecting?") could only be answered by someone who happened to have
/// launched it from a terminal beforehand. That is a reasonable thing to ask of a developer and an
/// unreasonable thing to ask of the person whose PC it is.
/// </para>
/// <para>
/// Deliberately not a logging framework, for the same reason as
/// <c>PcCore.InputBroker.BrokerLog</c>: what is needed is one file that answers what happened, not
/// a configuration surface. Two generations, capped, in the owner's own local app data.
/// </para>
/// <para>
/// <b>What is never in it.</b> The same rule that governs every log statement in this codebase:
/// identifiers yes, credentials never. Device ids, installation ids and credential fingerprints are
/// safe to write; shared secrets, private keys, pairing secrets, proofs and session tickets are not
/// written anywhere, so they cannot arrive here either.
/// </para>
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const string FileName = "pccore.log";
    public const string PreviousFileName = "pccore.previous.log";

    /// <summary>
    /// Enough to hold a long session, small enough that nobody has to think about it. A machine
    /// nobody administers must not accumulate a log that grows without bound.
    /// </summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly string? _previousPath;

    public RollingFileLoggerProvider(StateDirectory directory)
    {
        try
        {
            directory.Ensure();
            _path = directory.File(FileName);
            _previousPath = directory.File(PreviousFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be opened must not stop PC Core from running. The console provider
            // is registered alongside this one and still works.
            _path = null;
        }
    }

    /// <summary>Where the log is, so the start-up banner can point at it. Null if unavailable.</summary>
    public string? Path => _path;

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Append(string line)
    {
        if (_path is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                // Rolled to a single previous generation rather than truncated. The interesting
                // event is usually in the run *before* the one the owner is looking at — they
                // restart the thing, and only then think to go looking for why.
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Move(_path, _previousPath!, overwrite: true);
                }

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a log line is not worth failing the operation that produced it.
            }
        }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// Always true: the framework's own filters have already decided, using the same
        /// configuration the console provider uses, before this is called.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {Short(logLevel)}: {category}: {message}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            owner.Append(line);
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
