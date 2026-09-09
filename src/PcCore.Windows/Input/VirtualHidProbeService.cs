using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PcCore.NativeInput.Hid;

namespace PcCore.Windows.Input;

/// <summary>Where the probe pump is, reported rather than inferred.</summary>
public enum VirtualHidProbePhase
{
    /// <summary>
    /// Nobody is driving the backend.
    /// <para>
    /// <b>This value exists because its absence hid a real defect.</b> Every piece of the virtual
    /// HID path was wired and correct except that nothing ever called
    /// <see cref="IVirtualHidBackend.Probe"/>, so the backend sat in its constructor state forever
    /// and the status line read "the Nexus HID driver is not connected" — which was true, and
    /// indistinguishable from a driver that had been tried and refused. A machine with a working
    /// driver installed and a machine with none produced the same diagnostic. This phase separates
    /// them.
    /// </para>
    /// </summary>
    NotRunning,

    /// <summary>Started, and the first probe has not completed yet.</summary>
    NeverProbed,

    /// <summary>Running, and no Nexus HID driver has been found on this machine.</summary>
    Searching,

    /// <summary>The backend published devices and is carrying, or ready to carry, input.</summary>
    Connected,

    /// <summary>A driver answered before and is not usable now. Retrying on the backend's backoff.</summary>
    Retrying,

    /// <summary>Stopped as part of host shutdown; the backend has been released.</summary>
    Stopped,
}

/// <summary>
/// The one thing that calls <see cref="IVirtualHidBackend.Probe"/> in production.
/// <para>
/// <b>Why this is a service and not a call on the input path.</b> The router asks the eligibility
/// rules which backend should carry each event, and those rules read cached component state. If
/// the answer to "is the driver there?" were computed by probing, every pointer move would open a
/// device and issue three IOCTLs, and every failure would be discovered inside an input call, where
/// the only options are to block or to lie. Connecting is slow, occasional and allowed to fail;
/// routing is fast, constant and must not. They belong on different clocks.
/// </para>
/// <para>
/// <b>This class owns when, not how.</b> The backoff, the failure budget, the generation counter and
/// the neutral handshake all live in <see cref="VirtualHidLifecycle"/> and
/// <see cref="NexusHidBackend"/>, and re-implementing any of them here would give the system two
/// opinions about when to retry. A tick simply asks; the backend decides whether that ask does any
/// work. A tick against a backend inside its post-loss cooldown costs a lock and two comparisons.
/// </para>
/// <para>
/// <b>A connected backend is not re-probed at all.</b> Probing tears the connection down and rebuilds
/// it — <c>ConnectLocked</c> disconnects first — so a periodic probe against a healthy driver would
/// delete and republish three HID devices every tick. Loss is not something this has to poll for:
/// the submit path spends the failure budget and flips availability away from Ready, and the next
/// tick picks it up from there.
/// </para>
/// </summary>
public sealed class VirtualHidProbeService : IHostedService, IDisposable
{
    /// <summary>
    /// How often a disconnected backend is asked again. Deliberately the same as the backend's own
    /// retry interval: a faster tick would be absorbed by it anyway, and a slower one would be this
    /// class quietly overriding a policy it does not own.
    /// </summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(5);

    private readonly IVirtualHidBackend _backend;
    private readonly ILogger<VirtualHidProbeService> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _tick;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _stopping;
    private Task? _pump;
    private bool _disposed;

    private VirtualHidProbePhase _phase = VirtualHidProbePhase.NotRunning;
    private VirtualHidState? _lastResult;
    private DateTimeOffset? _lastProbeUtc;
    private int _probes;
    private int _starts;

    public VirtualHidProbeService(
        IVirtualHidBackend backend,
        ILogger<VirtualHidProbeService> logger,
        TimeProvider? time = null,
        TimeSpan? tickInterval = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _tick = tickInterval ?? DefaultTickInterval;
    }

    /// <summary>How many times the backend was actually asked. Diagnostics and tests.</summary>
    public int ProbeCount
    {
        get
        {
            lock (_gate)
            {
                return _probes;
            }
        }
    }

    public VirtualHidProbePhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>What the backend said last time it was asked, if it has been.</summary>
    public VirtualHidState? LastResult
    {
        get
        {
            lock (_gate)
            {
                return _lastResult;
            }
        }
    }

    /// <summary>
    /// The honest one-liner for <c>input.status</c>. Says which of "nobody asked", "asked and found
    /// nothing" and "found it and lost it" is true, because they call for different things from the
    /// owner and the old diagnostic collapsed all three into "not connected".
    /// </summary>
    public string Describe()
    {
        lock (_gate)
        {
            return _phase switch
            {
                VirtualHidProbePhase.NotRunning =>
                    "no probe pump is running, so the Nexus HID driver is never contacted",
                VirtualHidProbePhase.NeverProbed => "probing has not completed yet",
                VirtualHidProbePhase.Searching => "no Nexus HID driver was found; still looking",
                VirtualHidProbePhase.Connected => $"connected after {_probes} probe(s)",
                VirtualHidProbePhase.Retrying =>
                    $"the Nexus HID driver answered before and is {Why(_lastResult)} now; retrying",
                _ => "stopped; the Nexus HID devices were released",
            };

            static string Why(VirtualHidState? state) => state switch
            {
                VirtualHidState.Unavailable => "not answering",
                VirtualHidState.Refused => "refusing to publish",
                _ => "unusable",
            };
        }
    }

    /// <summary>
    /// Probes once, synchronously, before the host finishes starting, then hands over to the pump.
    /// <para>
    /// The first probe is deliberately inside startup rather than on the first tick: it means the
    /// very first <c>input.status</c> a phone asks for reports what is actually true, instead of
    /// "not connected" for whatever fraction of a tick the host happened to start in.
    /// </para>
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationToken pumpToken;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _starts++;

            if (_pump is not null)
            {
                // One pump, whatever the composition did. Two would double every probe and race each
                // other into ConnectLocked, which disconnects before it connects - so the second
                // would tear down the devices the first had just published.
                _logger.LogDebug("The virtual HID probe pump is already running; ignoring a second start.");
                return Task.CompletedTask;
            }

            _phase = VirtualHidProbePhase.NeverProbed;
            _stopping = new CancellationTokenSource();
            pumpToken = _stopping.Token;
        }

        VirtualHidAvailability availability;

        try
        {
            availability = ProbeNow();
        }
        catch (Exception ex)
        {
            // The first probe must not take the host down with it. A driver that throws on contact
            // is the case that needs retrying, not the case that needs PC Core to fail to start —
            // and the pump below is started either way.
            _logger.LogWarning(ex, "The first virtual HID probe failed; PC Core will keep trying.");
            availability = _backend.Availability;
        }

        if (availability.State is VirtualHidState.Ready or VirtualHidState.Partial)
        {
            _logger.LogInformation(
                "Virtual HID: {State} — {Backend} {Version} published {Count} device(s). {Diagnostic}",
                availability.State,
                availability.BackendName ?? "backend",
                availability.BackendVersion ?? "?",
                availability.Devices.Count,

                // A Partial with no reason is the observability gap that costs a restart to close.
                // Ready has nothing to explain, so it stays quiet.
                availability.Diagnostic ?? string.Empty);
        }
        else
        {
            _logger.LogInformation(
                "Virtual HID: {State}. {Diagnostic} PC Core will keep looking every {Seconds}s.",
                availability.State,
                availability.Diagnostic,
                _tick.TotalSeconds);
        }

        lock (_gate)
        {
            _pump = PumpAsync(pumpToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the pump and then releases the devices explicitly, in that order.
    /// <para>
    /// <b>Not left to the container's disposal, and not left to the driver.</b> The driver does
    /// neutralise when the control handle closes — that is what covers a crash — but an orderly
    /// shutdown that relies on it releases held input through a path that only runs once PC Core can
    /// no longer see whether it worked. Here the neutral reports go out over a connection that is
    /// still open, and the handle closes afterwards.
    /// </para>
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? pump;
        CancellationTokenSource? stopping;

        lock (_gate)
        {
            pump = _pump;
            stopping = _stopping;
            _pump = null;
            _stopping = null;
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown is being hurried. Release anyway - that is the part that matters.
            }
        }

        stopping?.Dispose();

        _backend.Shutdown();

        lock (_gate)
        {
            _phase = VirtualHidProbePhase.Stopped;
        }

        _logger.LogInformation("Virtual HID: released and disconnected.");
    }

    private async Task PumpAsync(CancellationToken stoppingToken)
    {
        // Yield first so StartAsync returns without waiting on a timer, and the host finishes
        // starting on the first probe rather than on the second.
        await Task.Yield();

        using var timer = new PeriodicTimer(_tick, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    /// <summary>
    /// One tick. Internal so the tests can drive the decision without driving a clock — the timer
    /// above is deliberately dumb, and this is the part with an opinion.
    /// </summary>
    /// <returns>True when the backend was actually asked.</returns>
    internal bool Tick()
    {
        if (Connected())
        {
            return false;
        }

        try
        {
            ProbeNow();
        }
        catch (Exception ex)
        {
            // A probe that throws must not kill the pump: the next tick tries again, and the
            // backend's own backoff governs how hard.
            _logger.LogWarning(ex, "A virtual HID probe failed; PC Core will try again.");
        }

        return true;
    }

    /// <summary>
    /// Whether the backend currently holds published devices.
    /// <para>
    /// <see cref="VirtualHidState.Partial"/> counts. A backend publishing a mouse and a keyboard but
    /// no digitizer is a real install, and re-probing to chase the third would delete the two that
    /// work.
    /// </para>
    /// </summary>
    private bool Connected() =>
        _backend.Availability.State is VirtualHidState.Ready or VirtualHidState.Partial;

    private VirtualHidAvailability ProbeNow()
    {
        var availability = _backend.Probe();
        var now = _time.GetUtcNow();

        VirtualHidState? previous;
        VirtualHidProbePhase phase;

        lock (_gate)
        {
            previous = _lastResult;
            _probes++;
            _lastProbeUtc = now;
            _lastResult = availability.State;

            _phase = availability.State switch
            {
                VirtualHidState.Ready or VirtualHidState.Partial => VirtualHidProbePhase.Connected,
                VirtualHidState.NotInstalled => VirtualHidProbePhase.Searching,
                _ => VirtualHidProbePhase.Retrying,
            };

            phase = _phase;
        }

        if (previous is not null && previous != availability.State)
        {
            // Only on change. A machine with no driver ticks forever, and a log line every five
            // seconds saying so is noise that buries the line that matters.
            if (phase == VirtualHidProbePhase.Connected)
            {
                _logger.LogInformation(
                    "Virtual HID is now {State}: {Count} device(s) published.",
                    availability.State,
                    availability.Devices.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Virtual HID went from {Previous} to {State}. {Diagnostic}",
                    previous,
                    availability.State,
                    availability.Diagnostic);
            }
        }

        return availability;
    }

    /// <summary>When the backend was last asked. Null until the first probe.</summary>
    public DateTimeOffset? LastProbeUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastProbeUtc;
            }
        }
    }

    /// <summary>How many times <see cref="StartAsync"/> was called. Composition diagnostics.</summary>
    internal int StartCount
    {
        get
        {
            lock (_gate)
            {
                return _starts;
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? stopping;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stopping = _stopping;
            _stopping = null;
            _pump = null;
        }

        stopping?.Cancel();
        stopping?.Dispose();
    }
}

/// <summary>
/// The registration, in one place so the host and the tests compose it identically.
/// <para>
/// The service is registered as a singleton and the hosted-service entry resolves that singleton
/// rather than constructing its own. Registering the type twice — once for diagnostics to read and
/// once for the host to start — would produce two objects, one of which probes and one of which
/// reports, and they would disagree.
/// </para>
/// </summary>
public static class VirtualHidProbeRegistration
{
    public static IServiceCollection AddVirtualHidProbe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Try-add on both lines, so calling this twice - a merged branch, a copied registration -
        // leaves one service and one hosted entry rather than two pumps racing each other into the
        // connect path, where the second disconnects what the first just published.
        services.TryAddSingleton<VirtualHidProbeService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, VirtualHidProbeService>(
                p => p.GetRequiredService<VirtualHidProbeService>()));

        return services;
    }
}
