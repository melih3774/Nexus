using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Media;
using PcCore.Protocol;

namespace PcCore.Capabilities.Screen;

/// <summary>One display, as returned by <c>screen.list_displays</c>.</summary>
public sealed record DisplayResult(
    int Index,
    string Name,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary)
{
    public static DisplayResult From(DisplayInfo display) => new(
        display.Index,
        display.Name,
        display.Left,
        display.Top,
        display.Width,
        display.Height,
        display.IsPrimary);
}

public sealed record ListDisplaysResult(int Count, IReadOnlyList<DisplayResult> Displays);

public sealed class ListDisplaysAction : CapabilityAction<NoParameters, ListDisplaysResult>
{
    private readonly IDisplayCatalog _displays;

    public ListDisplaysAction(IDisplayCatalog displays) => _displays = displays;

    public override string Name => "screen.list_displays";

    public override string Description => "The displays attached to this PC and where they sit on the desktop.";

    protected override ValueTask<ListDisplaysResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var displays = _displays.GetDisplays();
        return ValueTask.FromResult(new ListDisplaysResult(displays.Count, [.. displays.Select(DisplayResult.From)]));
    }
}

public sealed record StartStreamParameters
{
    /// <summary>Which display to stream. Absent means the primary one.</summary>
    public int? DisplayIndex { get; init; }
}

public sealed record StartStreamResult
{
    public required uint StreamId { get; init; }
    public required DisplayResult Display { get; init; }

    /// <summary><c>h264</c> or <c>jpeg</c>. The client sets its decoder up from this, not from a guess.</summary>
    public required string Encoding { get; init; }

    /// <summary>The encoded frame size, which is what the client's surface should be shaped to.</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Fps { get; init; }

    public required int BitrateKbps { get; init; }

    /// <summary>Which profile produced these numbers, including the adaptive step if there is one.</summary>
    public required string Plan { get; init; }

    /// <summary>Named, so "hardware encoding" is something the owner can check rather than believe.</summary>
    public required string Encoder { get; init; }

    /// <summary>The framing version the client must understand to read the binary messages.</summary>
    public required int FrameProtocolVersion { get; init; }

    /// <summary>Kept for the fallback path, where quality is a JPEG quality factor.</summary>
    public required int MaxFps { get; init; }

    public required int MaxWidth { get; init; }

    public required int MaxHeight { get; init; }

    public required int Quality { get; init; }
}

public sealed record StopStreamResult
{
    public required bool Stopped { get; init; }
    public long FramesSent { get; init; }
    public long FramesDropped { get; init; }

    /// <summary>Captures abandoned because frames were already waiting. Not a fault — the policy working.</summary>
    public long FramesSkipped { get; init; }

    public double AverageFrameBytes { get; init; }
    public double CaptureFps { get; init; }

    /// <summary>What actually left the machine, which is the number the owner sees as smoothness.</summary>
    public double DeliveredFps { get; init; }

    public double BitrateKbps { get; init; }

    /// <summary>Milliseconds a frame spent being scaled, converted and encoded, averaged.</summary>
    public double EncodeMs { get; init; }

    public string Encoder { get; init; } = "";

    public string Plan { get; init; } = "";
}

/// <summary>
/// Opens a live desktop stream on the calling connection.
/// <para>
/// The frames themselves do not come back through this response — they arrive as binary WebSocket
/// messages on the same connection. This action exists to negotiate: which display, at what bounds,
/// under which framing version, and with what stream id so the client can recognise its own frames.
/// </para>
/// <para>
/// Starting a stream grants no input. Watching and controlling are separate on purpose (SPEC.md 57).
/// </para>
/// </summary>
public sealed class StartStreamAction : CapabilityAction<StartStreamParameters, StartStreamResult>
{
    private readonly IStreamCoordinator _coordinator;
    private readonly StreamOptions _options;

    public StartStreamAction(IStreamCoordinator coordinator, StreamOptions options)
    {
        _coordinator = coordinator;
        _options = options;
    }

    public override string Name => "screen.stream.start";

    public override string Description => "Begin a live desktop stream on this connection.";

    protected override async ValueTask<StartStreamResult> ExecuteAsync(
        StartStreamParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (parameters.DisplayIndex is < 0 or > 255)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "Display index is out of range.");
        }

        var session = await _coordinator
            .StartAsync(context.Caller, parameters.DisplayIndex, cancellationToken)
            .ConfigureAwait(false);

        var plan = session.Plan;

        return new StartStreamResult
        {
            StreamId = session.StreamId,
            Display = DisplayResult.From(session.Display),
            Encoding = session.Encoder.Contains("JPEG", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "h264",
            Width = plan.Width,
            Height = plan.Height,
            Fps = plan.Fps,
            BitrateKbps = plan.BitrateKbps,
            Plan = plan.Label,
            Encoder = session.Encoder,
            FrameProtocolVersion = StreamFrameHeader.CurrentVersion,
            MaxFps = plan.Fps,
            MaxWidth = plan.Width,
            MaxHeight = plan.Height,
            Quality = _options.Quality,
        };
    }
}

public sealed class StopStreamAction : CapabilityAction<NoParameters, StopStreamResult>
{
    private readonly IStreamCoordinator _coordinator;

    public StopStreamAction(IStreamCoordinator coordinator) => _coordinator = coordinator;

    public override string Name => "screen.stream.stop";

    public override string Description => "End the live desktop stream on this connection.";

    protected override async ValueTask<StopStreamResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var stats = await _coordinator.StopAsync(context.Caller, cancellationToken).ConfigureAwait(false);

        return new StopStreamResult
        {
            Stopped = stats is not null,
            FramesSent = stats?.FramesEncoded ?? 0,
            FramesDropped = stats?.FramesDropped ?? 0,
            FramesSkipped = stats?.FramesSkipped ?? 0,
            AverageFrameBytes = stats?.AverageFrameBytes ?? 0,
            CaptureFps = stats?.CaptureFps ?? 0,
            DeliveredFps = stats?.DeliveredFps ?? 0,
            BitrateKbps = stats?.BitrateKbps ?? 0,
            EncodeMs = stats?.EncodeMs ?? 0,
            Encoder = stats?.Encoder ?? "",
            Plan = stats?.Plan.Label ?? "",
        };
    }
}

/// <summary>
/// Connects a capability call to the transport that will carry the frames.
/// <para>
/// The capability layer must not know what a WebSocket is, and the transport must not own capture.
/// This is the seam: the transport implements it, registers itself per connection, and the action
/// simply asks for a stream on behalf of the caller it was given.
/// </para>
/// </summary>
public interface IStreamCoordinator
{
    Task<DesktopStreamSession> StartAsync(CallerDescriptor caller, int? displayIndex, CancellationToken cancellationToken);

    /// <summary>Returns the statistics of the stream that was stopped, or null if there was none.</summary>
    Task<StreamStatistics?> StopAsync(CallerDescriptor caller, CancellationToken cancellationToken);

    /// <summary>This caller's live stream, or null. Used by the actions that talk to a running stream.</summary>
    DesktopStreamSession? Find(CallerDescriptor caller);
}

/// <summary>
/// Asks for the next frame to be a keyframe.
/// <para>
/// <b>Why the client has to be able to ask.</b> With independent still frames a client that missed
/// one simply drew the next. With an interframe codec, a missing picture makes every later picture
/// wrong until an IDR arrives, and the client is the only party that knows it happened: the PC sent
/// the frame, the socket accepted it, and only the decoder found out. Two seconds of smeared desktop
/// is the difference between a stream that recovers and one that has to be restarted.
/// </para>
/// <para>
/// It is cheap and idempotent by design, so a client may call it whenever it is unsure — on
/// resuming, on re-attaching a surface, or on a decoder error — without having to reason about
/// whether it really needs to.
/// </para>
/// </summary>
public sealed class RequestKeyframeAction : CapabilityAction<NoParameters, KeyframeResult>
{
    private readonly IStreamCoordinator _coordinator;

    public RequestKeyframeAction(IStreamCoordinator coordinator) => _coordinator = coordinator;

    public override string Name => "screen.stream.keyframe";

    public override string Description => "Ask for the next streamed frame to be a full keyframe.";

    protected override ValueTask<KeyframeResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var session = _coordinator.Find(context.Caller);
        session?.RequestKeyframe();

        return ValueTask.FromResult(new KeyframeResult { Requested = session is not null });
    }
}

public sealed record KeyframeResult
{
    public required bool Requested { get; init; }
}

public sealed record StreamReportParameters
{
    /// <summary>Capture to render, in milliseconds, as the client measured it against a shared clock.</summary>
    public double? LatencyMs { get; init; }

    public double? DecodeMs { get; init; }

    public double? RenderedFps { get; init; }

    public long? Dropped { get; init; }
}

/// <summary>
/// What the phone is actually seeing, fed back so the PC can adapt to it.
/// <para>
/// <b>Why measurements come from the client at all.</b> The PC can see its own queue and its own
/// encoder, and from those alone it cannot distinguish a link that is keeping up from a phone whose
/// decoder is not. Those need opposite responses — one is fine, the other needs fewer or smaller
/// frames — so the only way to tell them apart is to ask the end that knows.
/// </para>
/// <para>
/// Nothing here is trusted for anything but rate control. It cannot start, stop or authorise
/// anything, and the worst a dishonest report can do is make the owner's own stream worse.
/// </para>
/// </summary>
public sealed class StreamReportAction : CapabilityAction<StreamReportParameters, StreamReportResult>
{
    private readonly IStreamCoordinator _coordinator;

    public StreamReportAction(IStreamCoordinator coordinator) => _coordinator = coordinator;

    public override string Name => "screen.stream.report";

    public override string Description => "Tell the PC what the phone is measuring, so the stream can adapt.";

    protected override ValueTask<StreamReportResult> ExecuteAsync(
        StreamReportParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var session = _coordinator.Find(context.Caller);

        if (session is null)
        {
            return ValueTask.FromResult(new StreamReportResult { Accepted = false });
        }

        session.ReportClient(new ClientPlayback(
            parameters.LatencyMs ?? -1,
            parameters.DecodeMs ?? -1,
            parameters.RenderedFps ?? 0,
            parameters.Dropped ?? 0));

        var plan = session.Plan;

        return ValueTask.FromResult(new StreamReportResult
        {
            Accepted = true,
            Width = plan.Width,
            Height = plan.Height,
            Fps = plan.Fps,
            BitrateKbps = plan.BitrateKbps,
            Plan = plan.Label,
        });
    }
}

/// <summary>
/// The answer to a report is the current plan, so a client learns about an adaptive change without
/// polling for it — the frames it is already receiving are the only other clue, and they do not say
/// why.
/// </summary>
public sealed record StreamReportResult
{
    public required bool Accepted { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public int BitrateKbps { get; init; }
    public string Plan { get; init; } = "";
}
