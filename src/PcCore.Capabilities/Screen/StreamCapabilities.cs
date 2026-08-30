using PcCore.Capabilities.Abstractions;
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
    public required int MaxFps { get; init; }
    public required int MaxWidth { get; init; }
    public required int MaxHeight { get; init; }
    public required int Quality { get; init; }

    /// <summary>The framing version the client must understand to read the binary messages.</summary>
    public required int FrameProtocolVersion { get; init; }
}

public sealed record StopStreamResult
{
    public required bool Stopped { get; init; }
    public long FramesSent { get; init; }
    public long FramesDropped { get; init; }
    public double AverageFrameBytes { get; init; }
    public double CaptureFps { get; init; }
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

        return new StartStreamResult
        {
            StreamId = session.StreamId,
            Display = DisplayResult.From(session.Display),
            MaxFps = _options.MaxFps,
            MaxWidth = _options.MaxWidth,
            MaxHeight = _options.MaxHeight,
            Quality = _options.Quality,
            FrameProtocolVersion = StreamFrameHeader.CurrentVersion,
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
            AverageFrameBytes = stats?.AverageFrameBytes ?? 0,
            CaptureFps = stats?.CaptureFps ?? 0,
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
}
