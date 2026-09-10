using System.Text.Json.Serialization;

using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Media;

public sealed record LiveSensorParameters
{
    /// <summary><c>CAMERA</c> or <c>MICROPHONE</c>.</summary>
    public string? Sensor { get; init; }
}

public sealed record LiveSensorResult
{
    public required bool Camera { get; init; }

    public required bool Microphone { get; init; }

    /// <summary>What the PC is displaying locally while this is on. Empty when nothing is live.</summary>
    public required string Indicator { get; init; }

    /// <summary>Set when a start was refused, saying why in words the owner can read.</summary>
    public string? Refused { get; init; }

    /// <summary>Whether this PC has a camera at all.</summary>
    public bool CameraAvailable { get; init; }

    public bool MicrophoneAvailable { get; init; }

    public static LiveSensorResult From(LiveSensorState state, bool camera, bool microphone, string? refused = null) =>
        new()
        {
            Camera = state.Camera,
            Microphone = state.Microphone,
            Indicator = state.Describe(),
            CameraAvailable = camera,
            MicrophoneAvailable = microphone,
            Refused = refused,
        };
}

/// <summary>
/// Starts a live camera or microphone stream.
/// <para>
/// <b>Not in the unauthenticated allowlist, and that is the point.</b> Like every other real
/// capability, this needs <c>RemoteAuthority.FullRemote</c> — the owner has to have satisfied
/// Android's own biometric prompt on the phone in the current session. A paired-but-locked phone
/// reaches <c>session.*</c> and nothing else, so a stolen phone with a valid device credential
/// cannot open the camera.
/// </para>
/// <para>
/// It does not prompt again. The Full Remote session is the authorisation, and asking for a
/// fingerprint per action would train the owner to approve without reading — the same reasoning
/// that put a fifteen-minute window on the Keystore key rather than a prompt per request.
/// </para>
/// </summary>
public sealed class StartLiveSensorAction : CapabilityAction<LiveSensorParameters, LiveSensorResult>
{
    private readonly LiveSensorService _live;
    private readonly ILiveCameraSource _camera;
    private readonly ILiveAudioSource _audio;

    public StartLiveSensorAction(LiveSensorService live, ILiveCameraSource camera, ILiveAudioSource audio)
    {
        _live = live;
        _camera = camera;
        _audio = audio;
    }

    public override string Name => "media.live.start";

    public override string Description => "Start live camera or microphone monitoring for this session.";

    protected override async ValueTask<LiveSensorResult> ExecuteAsync(
        LiveSensorParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var kind = LiveSensorParsing.Parse(parameters.Sensor);

        // The connection is the unit of authorisation: a stream belongs to the session that asked
        // for it and dies with it.
        var connectionId = context.Caller.ConnectionId;

        var outcome = await _live.StartAsync(kind, connectionId, cancellationToken).ConfigureAwait(false);

        var state = _live.TrayState;

        return LiveSensorResult.From(
            state,
            await _camera.IsAvailableAsync(cancellationToken).ConfigureAwait(false),
            await _audio.IsAvailableAsync(cancellationToken).ConfigureAwait(false),
            outcome.Started ? null : outcome.Reason);
    }
}

/// <summary>
/// Stops a live stream, or all of them.
/// <para>
/// Deliberately reachable in the same authority as starting, and also reachable from the PC's own
/// tray without any session at all. Stopping is never the risky direction.
/// </para>
/// </summary>
public sealed class StopLiveSensorAction : CapabilityAction<LiveSensorParameters, LiveSensorResult>
{
    private readonly LiveSensorService _live;
    private readonly ILiveCameraSource _camera;
    private readonly ILiveAudioSource _audio;

    public StopLiveSensorAction(LiveSensorService live, ILiveCameraSource camera, ILiveAudioSource audio)
    {
        _live = live;
        _camera = camera;
        _audio = audio;
    }

    public override string Name => "media.live.stop";

    public override string Description => "Stop live camera or microphone monitoring.";

    protected override async ValueTask<LiveSensorResult> ExecuteAsync(
        LiveSensorParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.Sensor))
        {
            _live.StopAll("the owner stopped it from the phone");
        }
        else
        {
            _live.Stop(LiveSensorParsing.Parse(parameters.Sensor), "the owner stopped it from the phone");
        }

        return LiveSensorResult.From(
            _live.TrayState,
            await _camera.IsAvailableAsync(cancellationToken).ConfigureAwait(false),
            await _audio.IsAvailableAsync(cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>What is live now, and what this PC could stream if asked.</summary>
public sealed class GetLiveSensorStatusAction : CapabilityAction<NoParameters, LiveSensorResult>
{
    private readonly LiveSensorService _live;
    private readonly ILiveCameraSource _camera;
    private readonly ILiveAudioSource _audio;

    public GetLiveSensorStatusAction(LiveSensorService live, ILiveCameraSource camera, ILiveAudioSource audio)
    {
        _live = live;
        _camera = camera;
        _audio = audio;
    }

    public override string Name => "media.live.status";

    public override string Description => "Whether the camera or microphone is being streamed right now.";

    protected override async ValueTask<LiveSensorResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        LiveSensorResult.From(
            _live.TrayState,
            await _camera.IsAvailableAsync(cancellationToken).ConfigureAwait(false),
            await _audio.IsAvailableAsync(cancellationToken).ConfigureAwait(false));
}

internal static class LiveSensorParsing
{
    public static LiveSensorKind Parse(string? sensor) => sensor?.Trim().ToUpperInvariant() switch
    {
        "CAMERA" => LiveSensorKind.Camera,
        "MICROPHONE" or "MIC" => LiveSensorKind.Microphone,
        _ => throw new CapabilityException(
            ErrorCodes.InvalidParameters, "sensor must be CAMERA or MICROPHONE."),
    };
}
