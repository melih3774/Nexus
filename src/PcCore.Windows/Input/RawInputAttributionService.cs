using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Security.Detection;
using PcCore.NativeInput.Hid;

namespace PcCore.Windows.Input;

/// <summary>How the attribution stack is doing, as one value.</summary>
public sealed record RawInputAttributionHealth
{
    public required bool ListenerRunning { get; init; }

    public required bool SubscriptionActive { get; init; }

    /// <summary>True when at least one device path resolved, so identity comparison can work.</summary>
    public required bool DevicePathsResolve { get; init; }

    public required int KnownDevices { get; init; }

    public string? Diagnostic { get; init; }

    /// <summary>
    /// The single condition virtual HID eligibility hangs on.
    /// <para>
    /// All three parts, not any of them. A listener running with no working path resolution cannot
    /// tell a Nexus device from a person's, and a ledger nothing is subscribed to is a ledger that
    /// never learns anything — both of which would silently make HID input look like an intruder, or
    /// worse, make the absence of attribution look like success.
    /// </para>
    /// </summary>
    public bool IsReady => ListenerRunning && SubscriptionActive && DevicePathsResolve;

    public static RawInputAttributionHealth Down(string diagnostic) => new()
    {
        ListenerRunning = false,
        SubscriptionActive = false,
        DevicePathsResolve = false,
        KnownDevices = 0,
        Diagnostic = diagnostic,
    };
}

/// <summary>
/// The production wiring: <c>RawInputListener</c> to <c>RawInputSourceLedger</c>, and the health
/// state that virtual HID eligibility depends on.
/// <para>
/// <b>This is the piece that was missing.</b> The listener existed, the ledger existed, and the
/// security monitor already consulted the ledger — but nothing connected the first to the second, so
/// at runtime the ledger learned nothing and every rule built on it was inert. A chain that is
/// correct in every link and unconnected at one joint looks exactly like a working one from any
/// individual test, which is why this has its own type and its own health flag rather than three
/// lines in start-up.
/// </para>
/// <para>
/// <b>Only trusted observations are forwarded.</b> The listener classifies every event; this service
/// records only the ones the registry vouched for. Physical input records nothing, because there is
/// nothing to excuse — and that asymmetry is what makes the whole mechanism fail closed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawInputAttributionService : IDisposable
{
    private readonly RawInputListener _listener;
    private readonly RawInputSourceLedger _ledger;
    private readonly ILogger<RawInputAttributionService> _logger;
    private readonly Lock _gate = new();

    private Action<RawInputObservation, InputSourceVerdict>? _handler;
    private bool _started;
    private bool _disposed;

    public RawInputAttributionService(
        RawInputListener listener,
        RawInputSourceLedger ledger,
        ILogger<RawInputAttributionService> logger)
    {
        _listener = listener;
        _ledger = ledger;
        _logger = logger;
    }

    /// <summary>
    /// Starts the listener and connects it to the ledger. Idempotent: a second call does nothing,
    /// so a restart cannot leave two listeners or two subscriptions behind.
    /// </summary>
    public RawInputAttributionHealth Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return RawInputAttributionHealth.Down("the attribution service has been disposed");
            }

            if (_started)
            {
                return Health;
            }

            _handler = OnObserved;
            _listener.Observed += _handler;

            _listener.Start();
            _started = true;
        }

        var health = Health;

        // The ledger attributes nothing while unhealthy, so this flag is the enforcement point
        // rather than a label. Setting it false also discards anything already banked.
        _ledger.SetHealthy(health.IsReady);

        if (health.IsReady)
        {
            _logger.LogInformation(
                "Raw Input attribution READY: watching {Count} device(s) for source identity.",
                health.KnownDevices);
        }
        else
        {
            _logger.LogWarning(
                "Raw Input attribution UNAVAILABLE: {Reason}. Virtual HID cannot be used, and "
                + "device-originated input will all be treated as a person.",
                health.Diagnostic ?? "unknown");
        }

        return health;
    }

    /// <summary>The current health, recomputed from the listener rather than cached.</summary>
    public RawInputAttributionHealth Health
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !_started)
                {
                    return RawInputAttributionHealth.Down(
                        _disposed ? "the attribution service has been disposed" : "not started");
                }
            }

            var running = _listener.IsRunning;
            var known = _listener.KnownDeviceCount;

            return new RawInputAttributionHealth
            {
                ListenerRunning = running,
                SubscriptionActive = _handler is not null,
                // Identity comparison is path comparison, so a machine where nothing resolves cannot
                // do attribution at all, however healthy the listener looks.
                DevicePathsResolve = known > 0,
                KnownDevices = known,
                Diagnostic = running
                    ? known > 0 ? null : "no input device path could be resolved"
                    : _listener.Diagnostic ?? "the Raw Input listener is not running",
            };
        }
    }

    /// <summary>
    /// The forwarding rule. Internal rather than private so tests can exercise it directly: the
    /// alternative would be manufacturing a real WM_INPUT from a device that does not exist yet.
    /// </summary>
    internal void OnObserved(RawInputObservation observation, InputSourceVerdict verdict)
    {
        if (!verdict.IsNexus)
        {
            // A person, or a device Nexus does not own. Nothing is recorded: the hook event for this
            // action should count, and it will.
            return;
        }

        var kind = observation.Kind switch
        {
            RawInputKind.Mouse => ActivityKind.PointerActivity,
            _ => ActivityKind.KeyboardActivity,
        };

        _ledger.RecordTrustedNexusInput(kind, observation.AtUtc);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_handler is not null)
            {
                _listener.Observed -= _handler;
                _handler = null;
            }
        }

        // Health goes down before the listener stops, not after: the window between "stopped" and
        // "marked unhealthy" is a window in which banked observations could still excuse a person's
        // input.
        _ledger.SetHealthy(false);
        _listener.Dispose();

        _logger.LogInformation("Raw Input attribution stopped.");
    }
}
