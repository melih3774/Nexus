using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Storage;

namespace PcCore.Capabilities.Indicator;

/// <summary>
/// How visible the PC makes it that a phone is watching or driving it.
/// <para>
/// Every mode shows <em>something</em>. There is no hidden mode and there will not be one: a machine
/// that can be watched and typed on with no sign of it is a surveillance tool, and the difference
/// between this and one is exactly that the owner can always tell. What the modes trade is how much
/// the indication gets in the way, not whether it exists.
/// </para>
/// </summary>
public enum IndicatorMode
{
    /// <summary>
    /// The default. An on-screen badge while input is actually live; the tray entry alone while the
    /// phone is only watching.
    /// </summary>
    [JsonStringEnumMemberName("STANDARD")]
    Standard,

    /// <summary>
    /// No on-screen badge at all — but the tray entry stays, for viewing and for input alike. For an
    /// owner who finds the overlay distracting on their own machine, not for hiding anything.
    /// </summary>
    [JsonStringEnumMemberName("MINIMAL")]
    Minimal,

    /// <summary>The badge is up for the whole session, including passive viewing.</summary>
    [JsonStringEnumMemberName("ALWAYS_VISIBLE")]
    AlwaysVisible,
}

/// <summary>What the indicator should be showing right now.</summary>
public sealed record IndicatorState(bool Overlay, bool Tray, string Text)
{
    public static readonly IndicatorState Idle = new(false, false, "");
}

/// <summary>
/// The Windows-side presentation. Purely a display: it enforces nothing and gates nothing.
/// </summary>
public interface IRemoteIndicator : IDisposable
{
    /// <summary>Applies a state. Must never steal focus, take input, or block the caller.</summary>
    void Apply(IndicatorState state);
}

/// <summary>An indicator that draws nothing. Used in tests and on any non-Windows host.</summary>
public sealed class NullRemoteIndicator : IRemoteIndicator
{
    public IndicatorState Last { get; private set; } = IndicatorState.Idle;

    public void Apply(IndicatorState state) => Last = state;

    public void Dispose()
    {
    }
}

internal sealed record IndicatorPreference
{
    public int SchemaVersion { get; init; } = 1;

    public IndicatorMode Mode { get; init; } = IndicatorMode.Standard;
}

/// <summary>
/// Decides what the PC should be showing, and remembers the owner's preference across restarts.
/// <para>
/// The decision is a pure function of three facts — the mode, whether anyone is watching, and
/// whether input is live — which is why it is a static method with its own tests rather than
/// something spread across the transport and the input service.
/// </para>
/// </summary>
public sealed class RemoteIndicatorService : IDisposable
{
    public const string FileName = "indicator.json";

    private readonly IRemoteIndicator _indicator;
    private readonly IInputService _input;
    private readonly AtomicJsonFileStore<IndicatorPreference> _store;
    private readonly ILogger<RemoteIndicatorService> _logger;
    private readonly Lock _gate = new();

    private IndicatorMode _mode;
    private int _viewers;
    private int _inputSessions;

    public RemoteIndicatorService(
        IRemoteIndicator indicator,
        IInputService input,
        StateDirectory directory,
        ILogger<RemoteIndicatorService> logger)
    {
        _indicator = indicator;
        _input = input;
        _logger = logger;
        _store = new AtomicJsonFileStore<IndicatorPreference>(directory, FileName, logger);

        // Subscribed here rather than in the composition root. The indicator is the component that
        // cares when input starts and stops, so it is the one that should have to remember.
        _input.ActivityChanged += OnActivityChanged;

        var loaded = _store.Load();
        _mode = loaded.IsLoaded ? loaded.Value!.Mode : IndicatorMode.Standard;

        if (!loaded.IsLoaded)
        {
            // A missing or unreadable preference falls back to the most visible sensible default
            // rather than the least. Failing towards "quieter" would be the wrong direction here.
            _mode = IndicatorMode.Standard;
        }

        _logger.LogInformation("Remote-control indicator mode: {Mode}", _mode);
        Refresh();
    }

    public IndicatorMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    public IndicatorState Current { get; private set; } = IndicatorState.Idle;

    public void SetMode(IndicatorMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        lock (_gate)
        {
            _mode = mode;
        }

        _store.Save(new IndicatorPreference { Mode = mode });
        _logger.LogInformation("Remote-control indicator mode set to {Mode}.", mode);

        Refresh();
    }

    /// <summary>How many phones are receiving a live stream.</summary>
    public void SetViewers(int viewers)
    {
        lock (_gate)
        {
            _viewers = Math.Max(0, viewers);
        }

        Refresh();
    }

    /// <summary>How many remote input sessions are open.</summary>
    public void SetInputSessions(int sessions)
    {
        lock (_gate)
        {
            _inputSessions = Math.Max(0, sessions);
        }

        Refresh();
    }

    /// <summary>
    /// The whole policy, in one place.
    /// </summary>
    public static IndicatorState Decide(IndicatorMode mode, bool viewing, bool inputActive)
    {
        if (!viewing && !inputActive)
        {
            return IndicatorState.Idle;
        }

        // The tray entry is present for any remote session, in every mode. It is the one thing that
        // is never traded away.
        var text = inputActive ? "Nexus Remote — controlling" : "Nexus Remote — viewing";

        var overlay = mode switch
        {
            IndicatorMode.Standard => inputActive,
            IndicatorMode.Minimal => false,
            IndicatorMode.AlwaysVisible => true,
            _ => true,
        };

        return new IndicatorState(overlay, true, text);
    }

    private void OnActivityChanged(InputActivityChanged activity) => SetInputSessions(activity.ActiveSessions);

    private void Refresh()
    {
        IndicatorState state;

        lock (_gate)
        {
            state = Decide(_mode, _viewers > 0, _inputSessions > 0);
        }

        if (state == Current)
        {
            return;
        }

        Current = state;

        try
        {
            _indicator.Apply(state);
        }
        catch (Exception ex)
        {
            // The indicator is a status display. If it cannot draw, the session carries on — but
            // this is worth knowing about, because a session running with no visible sign of it is
            // exactly what the design is trying to avoid.
            _logger.LogWarning(ex, "The remote-control indicator could not be updated.");
        }
    }

    public void Dispose()
    {
        _input.ActivityChanged -= OnActivityChanged;

        try
        {
            _indicator.Apply(IndicatorState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clearing the indicator failed during shutdown.");
        }

        _indicator.Dispose();
    }
}
