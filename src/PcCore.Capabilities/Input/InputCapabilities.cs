using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Input;

/// <summary>
/// The result shape every input action shares.
/// <para>
/// <see cref="Delivered"/> being false is not an error: Windows refusing to send input to an
/// elevated window is a boundary PC Core deliberately does not cross, and the phone needs to be able
/// to say so rather than appearing to work.
/// </para>
/// </summary>
public sealed record InputAck
{
    public required bool Delivered { get; init; }

    /// <summary>One of <c>DELIVERED</c>, <c>BLOCKED_BY_INTEGRITY</c>, <c>FAILED</c>.</summary>
    public required string Delivery { get; init; }

    /// <summary>Safe, short explanation when something was refused. Never a stack trace.</summary>
    public string? Diagnostic { get; init; }

    public static InputAck From(InputResult result) => new()
    {
        Delivered = result.Succeeded,
        Delivery = result.Delivery switch
        {
            InputDelivery.Delivered => "DELIVERED",
            InputDelivery.BlockedByIntegrity => "BLOCKED_BY_INTEGRITY",
            _ => "FAILED",
        },
        Diagnostic = result.Diagnostic,
    };
}

public sealed record BeginInputResult
{
    public required bool Active { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required int MaxEventsPerSecond { get; init; }
}

public sealed record EndInputResult
{
    /// <summary>False when input was not enabled. Ending nothing is not an error.</summary>
    public required bool Ended { get; init; }

    /// <summary>How many held buttons and keys were released on the way out.</summary>
    public required int Released { get; init; }
}

/// <summary>
/// Base for the input actions: they all address the caller's own connection, and none of them takes
/// an owner id from the request. Which session an event belongs to is decided by which connection it
/// arrived on, never by what the message claims.
/// </summary>
public abstract class InputAction<TParameters> : CapabilityAction<TParameters, InputAck>
    where TParameters : class, new()
{
    protected InputAction(IInputService input) => Input = input;

    protected IInputService Input { get; }

    protected static string OwnerOf(CapabilityContext context) => context.Caller.ConnectionId;
}

public sealed class BeginInputAction : CapabilityAction<NoParameters, BeginInputResult>
{
    private readonly IInputService _input;
    private readonly InputOptions _options;

    public BeginInputAction(IInputService input, InputOptions options)
    {
        _input = input;
        _options = options;
    }

    public override string Name => "input.begin";

    public override string Description => "Enable remote pointer and keyboard control on this connection.";

    protected override ValueTask<BeginInputResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var session = _input.Begin(context.Caller.ConnectionId);

        return ValueTask.FromResult(new BeginInputResult
        {
            Active = true,
            StartedAtUtc = session.StartedAtUtc,
            MaxEventsPerSecond = _options.MaxEventsPerSecond,
        });
    }
}

public sealed class EndInputAction : CapabilityAction<NoParameters, EndInputResult>
{
    private readonly IInputService _input;

    public EndInputAction(IInputService input) => _input = input;

    public override string Name => "input.end";

    public override string Description => "Disable remote input and release anything it left held.";

    protected override ValueTask<EndInputResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var owner = context.Caller.ConnectionId;
        var released = _input.ReleaseAll(owner);
        var ended = _input.End(owner);

        return ValueTask.FromResult(new EndInputResult { Ended = ended, Released = released });
    }
}

public sealed record PointerParameters
{
    /// <summary><c>ABSOLUTE</c> or <c>RELATIVE</c>. Absolute is direct-touch; relative is trackpad.</summary>
    public string? Mode { get; init; }

    /// <summary>Absolute mode: position across the display, 0 to 1.</summary>
    public double X { get; init; }

    /// <summary>Absolute mode: position down the display, 0 to 1.</summary>
    public double Y { get; init; }

    /// <summary>Relative mode: movement in pixels.</summary>
    public double Dx { get; init; }

    public double Dy { get; init; }

    /// <summary>Absolute mode: which display the position is relative to.</summary>
    public int DisplayIndex { get; init; }
}

/// <summary>
/// Moves the pointer.
/// <para>
/// Absolute positions arrive normalised to the display rather than as pixels, so the phone never has
/// to know about DPI scaling, monitor origins, or the range Windows expects. Everything that has to
/// be exactly right happens on this side, where it is testable.
/// </para>
/// </summary>
public sealed class PointerAction : InputAction<PointerParameters>
{
    public PointerAction(IInputService input) : base(input)
    {
    }

    public override string Name => "input.pointer";

    public override string Description => "Move the pointer, either to a position on a display or by a delta.";

    protected override ValueTask<InputAck> ExecuteAsync(
        PointerParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var owner = OwnerOf(context);

        var result = (parameters.Mode ?? "ABSOLUTE").ToUpperInvariant() switch
        {
            "ABSOLUTE" => Input.MoveAbsolute(owner, parameters.DisplayIndex, parameters.X, parameters.Y),
            "RELATIVE" => Input.MoveRelative(owner, parameters.Dx, parameters.Dy),
            _ => throw new CapabilityException(
                ErrorCodes.InvalidParameters, "Pointer mode must be ABSOLUTE or RELATIVE."),
        };

        return ValueTask.FromResult(InputAck.From(result));
    }
}

public sealed record ButtonParameters
{
    public PointerButton Button { get; init; } = PointerButton.Left;

    public ButtonAction Action { get; init; } = ButtonAction.Click;
}

public sealed class ButtonInputAction : InputAction<ButtonParameters>
{
    public ButtonInputAction(IInputService input) : base(input)
    {
    }

    public override string Name => "input.button";

    public override string Description => "Press, release, click or double-click a mouse button.";

    protected override ValueTask<InputAck> ExecuteAsync(
        ButtonParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InputAck.From(Input.Button(OwnerOf(context), parameters.Button, parameters.Action)));
}

public sealed record ScrollParameters
{
    /// <summary>Wheel notches. Positive scrolls up, matching Windows.</summary>
    public double Vertical { get; init; }

    public double Horizontal { get; init; }
}

public sealed class ScrollAction : InputAction<ScrollParameters>
{
    public ScrollAction(IInputService input) : base(input)
    {
    }

    public override string Name => "input.scroll";

    public override string Description => "Scroll the wheel vertically and horizontally.";

    protected override ValueTask<InputAck> ExecuteAsync(
        ScrollParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            InputAck.From(Input.Scroll(OwnerOf(context), parameters.Horizontal, parameters.Vertical)));
}

public sealed record KeyParameters
{
    public NexusKey Key { get; init; } = NexusKey.Escape;

    public KeyAction Action { get; init; } = KeyAction.Press;
}

public sealed class KeyInputAction : InputAction<KeyParameters>
{
    public KeyInputAction(IInputService input) : base(input)
    {
    }

    public override string Name => "input.key";

    public override string Description => "Press or release one of the named keys.";

    protected override ValueTask<InputAck> ExecuteAsync(
        KeyParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InputAck.From(Input.Key(OwnerOf(context), parameters.Key, parameters.Action)));
}

public sealed record TextParameters
{
    public string? Text { get; init; }
}

/// <summary>
/// Types text.
/// <para>
/// Separate from <c>input.key</c> because they are different problems. A key event is a physical
/// key; text is characters, and the phone's keyboard produces characters that no key on a US
/// keyboard corresponds to. Sending those as Unicode means an owner typing in their own language
/// gets what they typed instead of a mangled approximation.
/// </para>
/// </summary>
public sealed class TextInputAction : InputAction<TextParameters>
{
    public TextInputAction(IInputService input) : base(input)
    {
    }

    public override string Name => "input.text";

    public override string Description => "Type Unicode text at the current focus.";

    protected override ValueTask<InputAck> ExecuteAsync(
        TextParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InputAck.From(Input.Text(OwnerOf(context), parameters.Text ?? string.Empty)));
}

public sealed record ReleaseAllResult
{
    public required int Released { get; init; }
}

/// <summary>
/// Releases everything this connection holds, without ending the session.
/// <para>
/// The phone calls this when its control surface loses focus, when a gesture is interrupted, and
/// whenever it is unsure. Being able to say "forget whatever you think I am holding" cheaply is what
/// stops a missed touch-up from leaving a button down.
/// </para>
/// </summary>
public sealed class ReleaseAllInputAction : CapabilityAction<NoParameters, ReleaseAllResult>
{
    private readonly IInputService _input;

    public ReleaseAllInputAction(IInputService input) => _input = input;

    public override string Name => "input.release_all";

    public override string Description => "Release every mouse button and key this connection is holding.";

    protected override ValueTask<ReleaseAllResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ReleaseAllResult { Released = _input.ReleaseAll(context.Caller.ConnectionId) });
}
