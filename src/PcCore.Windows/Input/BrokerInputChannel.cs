using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PcCore.NativeInput;

namespace PcCore.Windows.Input;

/// <summary>
/// PC Core's end of the pipe to the Nexus Input Broker.
/// <para>
/// One connection, one request at a time, blocking. Input is inherently serial — a move followed by
/// a button press has to arrive in that order — so a lock around a synchronous round trip is both
/// the simplest correct design and the fastest one. Measured on this machine, a full round trip
/// through the broker costs tens of microseconds, which is nothing against the milliseconds the
/// event already spent crossing Tailscale.
/// </para>
/// <para>
/// <b>The connection is checked, not assumed.</b> PC Core verifies which executable is serving the
/// pipe before it sends anything. A pipe name is not a secret and any program running as the owner
/// could create one first; without this check, that program would receive everything the owner
/// typed from their phone. The broker performs the mirror-image check on its side.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerInputChannel : IDisposable
{
    /// <summary>
    /// How long to wait before trying the pipe again after a failure.
    /// <para>
    /// Without this, every pointer move on a machine with no broker installed would pay for a failed
    /// connection attempt. With it, the cost is one attempt every few seconds and the phone still
    /// picks the broker up on its own once it appears.
    /// </para>
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<BrokerInputChannel> _logger;
    private readonly string _expectedBrokerPath;
    private readonly Lock _gate = new();

    private NamedPipeClientStream? _pipe;
    private BrokerInfo? _info;
    private long _sequence;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private string? _diagnostic;

    public BrokerInputChannel(ILogger<BrokerInputChannel> logger, string? expectedBrokerPath = null)
    {
        _logger = logger;
        _expectedBrokerPath = expectedBrokerPath ?? NativeInputInstaller.ExpectedBrokerPath;
    }

    /// <summary>What the broker said about itself, or null when there is no live connection.</summary>
    public BrokerInfo? Info
    {
        get
        {
            lock (_gate)
            {
                return _info;
            }
        }
    }

    public string? Diagnostic
    {
        get
        {
            lock (_gate)
            {
                return _diagnostic;
            }
        }
    }

    /// <summary>
    /// Sends one request, connecting first if needed.
    /// <para>
    /// Returns null when the broker is not reachable, which is the caller's signal to fall back
    /// rather than fail. A dropped connection is retried once inside a single call, because the
    /// commonest cause is a broker that restarted between two events and the alternative is losing
    /// one otherwise-fine pointer move to it.
    /// </para>
    /// </summary>
    public BrokerResponse? Send(BrokerRequest request)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!EnsureConnected())
                {
                    return null;
                }

                try
                {
                    var framed = request with { Sequence = ++_sequence };
                    BrokerFraming.Write(_pipe!, framed);

                    var response = BrokerFraming.Read<BrokerResponse>(_pipe!);

                    if (response is null)
                    {
                        Drop("the broker closed the connection");
                        continue;
                    }

                    if (response.Sequence != framed.Sequence)
                    {
                        // A reply that does not match its request means the stream is out of step,
                        // and every later reply would be answering the wrong question. Better to
                        // drop the connection than to keep reading plausible nonsense.
                        Drop("the broker replied out of sequence");
                        return null;
                    }

                    return response;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
                {
                    Drop(ex.Message);
                }
            }

            return null;
        }
    }

    /// <summary>Re-reads the broker's self-description, connecting if it has appeared since last time.</summary>
    public BrokerInfo? Refresh()
    {
        var response = Send(new BrokerRequest { Op = BrokerProtocol.Ops.Desktop });

        if (response?.Info is null)
        {
            return Info;
        }

        lock (_gate)
        {
            _info = response.Info;
            return _info;
        }
    }

    private bool EnsureConnected()
    {
        if (_pipe is { IsConnected: true })
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;

        if (now < _nextAttempt)
        {
            return false;
        }

        _nextAttempt = now + RetryInterval;
        Close();

        var sid = WindowsIdentity.GetCurrent().User?.Value;

        if (sid is null)
        {
            _diagnostic = "the current token has no user SID";
            return false;
        }

        // isAsync: false — the whole channel is synchronous by design, and an overlapped pipe would
        // only add a completion port to every mouse movement.
        var pipe = new NamedPipeClientStream(
            ".",
            BrokerProtocol.PipeName(sid),
            PipeDirection.InOut,
            PipeOptions.None,
            TokenImpersonationLevel.None);

        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            _diagnostic = "the Nexus Input Broker is not running";
            return false;
        }

        var server = BrokerPipe.ServerImagePath(pipe.SafePipeHandle);

        if (!BrokerPipe.SameImage(server, _expectedBrokerPath))
        {
            // Deliberately loud. Something answering on the broker's pipe name that is not the
            // broker is either a bug or an attempt to intercept the owner's remote input, and both
            // are worth a warning that names neither a path nor a keystroke.
            _logger.LogWarning(
                "Refusing the native input pipe: it is served by {Executable}, not the Nexus Input Broker.",
                server is null ? "an unidentifiable process" : Path.GetFileName(server));

            pipe.Dispose();
            _diagnostic = "another process is using the Nexus Input Broker pipe name";
            return false;
        }

        _pipe = pipe;
        _sequence = 0;

        var hello = Handshake();

        if (hello is null)
        {
            Drop("the broker did not answer the handshake");
            return false;
        }

        _info = hello;
        _diagnostic = null;

        _logger.LogInformation(
            "Native input connected: broker {Version} (pid {Pid}), elevated {Elevated}, touch {Touch}.",
            hello.BrokerVersion,
            hello.ProcessId,
            hello.Elevated,
            hello.TouchAvailable);

        return true;
    }

    private BrokerInfo? Handshake()
    {
        try
        {
            var request = new BrokerRequest { Op = BrokerProtocol.Ops.Hello, Sequence = ++_sequence };
            BrokerFraming.Write(_pipe!, request);

            var response = BrokerFraming.Read<BrokerResponse>(_pipe!);

            return response?.Sequence == request.Sequence ? response.Info : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            return null;
        }
    }

    private void Drop(string reason)
    {
        Close();
        _diagnostic = reason;
        _nextAttempt = DateTimeOffset.UtcNow + RetryInterval;
        _logger.LogInformation("Native input disconnected: {Reason}", reason);
    }

    private void Close()
    {
        _pipe?.Dispose();
        _pipe = null;
        _info = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }
}
