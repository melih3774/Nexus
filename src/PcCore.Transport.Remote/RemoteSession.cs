using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;

namespace PcCore.Transport.Remote;

public sealed class RemoteTransportOptions
{
    public const string SectionName = "PcCore:RemoteTransport";

    /// <summary>
    /// Turns the remote listener on. When false PC Core never opens the remote port at all, which
    /// is the switch to use if the owner wants the PC reachable only from the machine itself.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fixed application port. Reachable only through the bound Tailscale address, so it needs no
    /// router port forwarding and never appears on the LAN.
    /// </summary>
    public int Port { get; set; } = 47812;

    /// <summary>
    /// How often to re-check for a Tailscale address while the transport is offline. Modest on
    /// purpose: this is a poll for an interface appearing, not a network monitoring subsystem.
    /// </summary>
    public int AvailabilityCheckSeconds { get; set; } = 30;

    /// <summary>How long an unauthenticated connection may stay open before it is closed.</summary>
    public int AuthenticationTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Simultaneous authenticated sessions allowed from the Primary Phone.
    /// <para>
    /// Four, not one. A phone that loses signal leaves a session the PC has not yet noticed is
    /// dead, and an exclusive-session rule would lock the owner out of their own machine until a
    /// timeout elapsed. Four absorbs reconnect races and a foreground/background split while still
    /// bounding memory; when the limit is reached the oldest session is closed rather than the new
    /// one refused, because the newest connection is the one the owner is actually looking at.
    /// </para>
    /// </summary>
    public int MaxSessions { get; set; } = 4;
}

/// <summary>One authenticated remote connection.</summary>
public sealed record RemoteSession(
    string SessionId,
    CallerDescriptor Caller,
    DateTimeOffset AuthenticatedAtUtc)
{
    /// <summary>Closes the underlying connection. Set by the connection that owns the session.</summary>
    public required Func<Task> CloseAsync { get; init; }
}

/// <summary>
/// The live authenticated sessions. In memory and connection-bound by design: a session is a
/// property of an open socket, not something that outlives it or is shared between processes.
/// </summary>
public sealed class RemoteSessionRegistry
{
    private readonly Lock _gate = new();
    private readonly List<RemoteSession> _sessions = [];
    private readonly RemoteTransportOptions _options;
    private readonly ILogger<RemoteSessionRegistry> _logger;

    public RemoteSessionRegistry(RemoteTransportOptions options, ILogger<RemoteSessionRegistry> logger)
    {
        _options = options;
        _logger = logger;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public IReadOnlyList<RemoteSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }

    /// <summary>
    /// Registers a newly authenticated session, returning any sessions evicted to stay within the
    /// limit. The caller closes them outside the lock.
    /// </summary>
    public IReadOnlyList<RemoteSession> Add(RemoteSession session)
    {
        List<RemoteSession> evicted = [];

        lock (_gate)
        {
            _sessions.Add(session);

            while (_sessions.Count > Math.Max(1, _options.MaxSessions))
            {
                var oldest = _sessions[0];
                _sessions.RemoveAt(0);
                evicted.Add(oldest);
            }
        }

        foreach (var old in evicted)
        {
            _logger.LogInformation(
                "Closing session {SessionId} to stay within the {Limit}-session limit.",
                old.SessionId,
                _options.MaxSessions);
        }

        return evicted;
    }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            _sessions.RemoveAll(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }
    }
}
