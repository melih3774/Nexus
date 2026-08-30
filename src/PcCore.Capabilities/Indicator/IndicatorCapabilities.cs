using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Indicator;

public sealed record IndicatorStatusResult
{
    public required IndicatorMode Mode { get; init; }

    /// <summary>Whether the on-screen badge is up right now.</summary>
    public required bool OverlayVisible { get; init; }

    /// <summary>Whether the notification-area entry is present right now.</summary>
    public required bool TrayVisible { get; init; }

    public required int Viewers { get; init; }

    public required int InputSessions { get; init; }
}

public sealed record SetIndicatorParameters
{
    public IndicatorMode? Mode { get; init; }
}

/// <summary>
/// Reads the indicator setting.
/// <para>
/// Local-only, and so is the setter. How visible the PC makes remote access is a decision about the
/// PC, taken at the PC — a phone that could quiet its own indicator would be a phone that can hide
/// itself, which is the one thing this design will not allow.
/// </para>
/// </summary>
public sealed class GetIndicatorAction : CapabilityAction<NoParameters, IndicatorStatusResult>
{
    private readonly RemoteIndicatorService _indicator;
    private readonly IIndicatorActivityProbe _activity;

    public GetIndicatorAction(RemoteIndicatorService indicator, IIndicatorActivityProbe activity)
    {
        _indicator = indicator;
        _activity = activity;
    }

    public override string Name => "indicator.get";

    public override string Description => "The remote-control indicator mode and what it is currently showing.";

    protected override ValueTask<IndicatorStatusResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new IndicatorStatusResult
        {
            Mode = _indicator.Mode,
            OverlayVisible = _indicator.Current.Overlay,
            TrayVisible = _indicator.Current.Tray,
            Viewers = _activity.Viewers,
            InputSessions = _activity.InputSessions,
        });
}

public sealed class SetIndicatorAction : CapabilityAction<SetIndicatorParameters, IndicatorStatusResult>
{
    private readonly RemoteIndicatorService _indicator;
    private readonly IIndicatorActivityProbe _activity;

    public SetIndicatorAction(RemoteIndicatorService indicator, IIndicatorActivityProbe activity)
    {
        _indicator = indicator;
        _activity = activity;
    }

    public override string Name => "indicator.set";

    public override string Description => "Choose STANDARD, MINIMAL or ALWAYS_VISIBLE.";

    protected override ValueTask<IndicatorStatusResult> ExecuteAsync(
        SetIndicatorParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (parameters.Mode is not { } mode || !Enum.IsDefined(mode))
        {
            throw new CapabilityException(
                ErrorCodes.InvalidParameters, "Mode must be STANDARD, MINIMAL or ALWAYS_VISIBLE.");
        }

        _indicator.SetMode(mode);

        return ValueTask.FromResult(new IndicatorStatusResult
        {
            Mode = _indicator.Mode,
            OverlayVisible = _indicator.Current.Overlay,
            TrayVisible = _indicator.Current.Tray,
            Viewers = _activity.Viewers,
            InputSessions = _activity.InputSessions,
        });
    }
}

/// <summary>Counts what is currently going on, so the status readout does not have to guess.</summary>
public interface IIndicatorActivityProbe
{
    int Viewers { get; }

    int InputSessions { get; }
}
