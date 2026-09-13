using PcCore.Protocol;

namespace PcCore.Capabilities.Abstractions;

/// <summary>Marker for actions that take no parameters.</summary>
public sealed record NoParameters;

/// <summary>
/// Identifies where a call came from, for logging and future policy only.
/// <para>
/// Deliberately carries no socket, no <c>HttpContext</c>, no headers and no IP address: a
/// capability must not be able to behave differently depending on whether the caller arrived
/// over loopback, LAN, a future relay, or an in-process AI bridge (SPEC.md 3.1).
/// </para>
/// </summary>
/// <param name="Transport">Stable transport name, for example <c>loopback-http</c>.</param>
/// <param name="ConnectionId">Opaque per-connection id, useful for correlating a client's calls.</param>
/// <param name="ActorType">What kind of caller this is. v0.2 only ever produces <see cref="Protocol.ActorType.Local"/>.</param>
/// <param name="DeviceId">
/// Identifier of an authenticated remote device, or <see langword="null"/>. It stays null in v0.2:
/// there is no device authentication yet, and inventing an id here would be a fake credential that
/// later code might mistake for a real one.
/// </param>
public sealed record CallerDescriptor(
    string Transport,
    string ConnectionId,
    ActorType ActorType,
    string? DeviceId = null)
{
    private readonly string? _deviceId = Validate(ActorType, DeviceId);

    /// <summary>An in-process caller: PC Core invoking its own capabilities.</summary>
    public static readonly CallerDescriptor Internal = new("internal", "internal", ActorType.Local);

    public string? DeviceId
    {
        get => _deviceId;
        init => _deviceId = Validate(ActorType, value);
    }

    /// <summary>
    /// The app build a remote device said it is running, when it said. Informational: it is the
    /// caller's own claim, and only ever used to <em>narrow</em> what it may do.
    /// </summary>
    public int? ClientVersionCode { get; init; }

    /// <summary>
    /// Set by the remote transport when the device's app is older than this PC Core serves. The
    /// policy then allows the update path and nothing else.
    /// <para>
    /// A compatibility gate, not a security control: the version is the client's own claim, and a
    /// client that overstated it would receive exactly what a current app on the same authenticated
    /// device receives. What it guarantees is that an honest old app is steered to the update instead
    /// of misbehaving against a protocol it does not understand.
    /// </para>
    /// </summary>
    public bool RequiresUpdate { get; init; }

    /// <summary>
    /// A modelling guard, not a security control: it keeps a device id from being stamped onto a
    /// caller that was never authenticated as a device, so no part of the system can later read one
    /// and conclude a pairing happened.
    /// </summary>
    private static string? Validate(ActorType actorType, string? deviceId) =>
        deviceId is not null && actorType != ActorType.Device
            ? throw new ArgumentException(
                $"A device id may only be attached to a {ActorType.Device} caller, not {actorType}.",
                nameof(deviceId))
            : deviceId;

    public override string ToString() =>
        DeviceId is null ? $"{Transport}/{ConnectionId} ({ActorType})" : $"{Transport}/{ConnectionId} ({ActorType}:{DeviceId})";
}

/// <summary>Everything a capability is told about the invocation itself.</summary>
/// <param name="RequestId">Correlation id from the request envelope.</param>
/// <param name="Caller">Who is calling, for logging.</param>
public sealed record CapabilityContext(string RequestId, CallerDescriptor Caller);

/// <summary>
/// A capability failure that maps to a specific protocol error code. Anything else that escapes
/// a capability becomes <c>internal_error</c> with its details suppressed.
/// </summary>
public sealed class CapabilityException : Exception
{
    public CapabilityException(string code, string message, string? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Details = details;
    }

    /// <summary>One of <see cref="Protocol.ErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>Optional safe context. Must not contain file contents, secrets, or stack traces.</summary>
    public string? Details { get; }
}

/// <summary>
/// The non-generic view of an action, used by the registry and dispatcher.
/// Implement <see cref="CapabilityAction{TParams, TResult}"/> rather than this.
/// </summary>
public interface ICapabilityAction
{
    /// <summary>Dotted action name, for example <c>files.list</c>.</summary>
    string Name { get; }

    /// <summary>One-line human description, surfaced by the developer test page.</summary>
    string Description { get; }

    /// <summary>The record the dispatcher must bind <c>params</c> to before invoking.</summary>
    Type ParameterType { get; }

    ValueTask<object?> InvokeAsync(object parameters, CapabilityContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for every capability action. Parameters and results are strongly typed records;
/// no capability ever sees JSON.
/// </summary>
public abstract class CapabilityAction<TParams, TResult> : ICapabilityAction
    where TParams : class
    where TResult : class
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public Type ParameterType => typeof(TParams);

    protected abstract ValueTask<TResult> ExecuteAsync(
        TParams parameters,
        CapabilityContext context,
        CancellationToken cancellationToken);

    async ValueTask<object?> ICapabilityAction.InvokeAsync(
        object parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (parameters is not TParams typed)
        {
            throw new CapabilityException(
                Protocol.ErrorCodes.InvalidParameters,
                $"Action '{Name}' expected parameters of type {typeof(TParams).Name}.");
        }

        return await ExecuteAsync(typed, context, cancellationToken).ConfigureAwait(false);
    }
}
