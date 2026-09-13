using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcCore.Protocol;

namespace PcCore.Capabilities.Abstractions;

/// <summary>
/// The single choke point every capability invocation passes through, whatever the transport.
/// <para>
/// This is also the natural seat for future cross-cutting policy — the security modes described
/// in SPEC.md 11, rate limits, or per-caller authorisation — none of which exist in v0.1.
/// </para>
/// </summary>
public interface ICapabilityDispatcher
{
    ValueTask<ResponseEnvelope> DispatchAsync(
        RequestEnvelope request,
        CallerDescriptor caller,
        CancellationToken cancellationToken = default);
}

public sealed class CapabilityDispatcher : ICapabilityDispatcher
{
    /// <summary>
    /// Actions that arrive many times a second while in use, logged at debug rather than information.
    /// <para>
    /// A moving pointer is sixty requests a second, and each wrote two lines to a log file that is
    /// opened, appended and closed per line under one lock the capture loop also takes. That cost
    /// was on the input path itself, and it filled the two-megabyte log in under two minutes —
    /// pushing out the stream measurements the log exists to keep. Denials and failures of these
    /// actions are still logged as warnings like any other.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> HighFrequencyActions = new(StringComparer.Ordinal)
    {
        "input.pointer",
        "input.scroll",
        "input.touch",
        "screen.stream.report",
        "screen.stream.keyframe",
        "media.time",
        "media.live.report",
        "media.live.keyframe",

        // A megabyte per request: a large download is thousands of these. The begin, finish and
        // hash of each transfer stay at information, which is the record of what moved.
        "files.read",
        "files.upload.chunk",
    };

    private readonly ICapabilityRegistry _registry;
    private readonly ICapabilityPolicy _policy;
    private readonly ILogger<CapabilityDispatcher> _logger;

    public CapabilityDispatcher(
        ICapabilityRegistry registry,
        ICapabilityPolicy policy,
        ILogger<CapabilityDispatcher> logger)
    {
        _registry = registry;
        _policy = policy;
        _logger = logger;
    }

    public async ValueTask<ResponseEnvelope> DispatchAsync(
        RequestEnvelope request,
        CallerDescriptor caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var stopwatch = Stopwatch.StartNew();

        if (!_registry.TryGet(request.Action, out var capability))
        {
            _logger.LogWarning(
                "Unknown action {Action} requested by {Caller} (request {RequestId})",
                request.Action,
                caller,
                request.Id);

            return Failure(
                request,
                stopwatch,
                new ProtocolError(
                    ErrorCodes.UnknownAction,
                    $"No capability is registered for action '{request.Action}'."));
        }

        // Policy runs before parameters are bound and before any capability code executes, so a
        // denied action performs no part of its work. This is the seam v0.2 documented and left
        // empty; v0.4 is the first milestone with two kinds of caller to distinguish.
        var decision = _policy.Evaluate(request.Action, caller);
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "Denied {Action} for {Caller} (request {RequestId}): {Reason}",
                request.Action,
                caller,
                request.Id,
                decision.DeniedReason);

            return Failure(
                request,
                stopwatch,
                decision.Code == ErrorCodes.UpdateRequired
                    ? new ProtocolError(
                        ErrorCodes.UpdateRequired,
                        "This version of Nexus is too old for this PC. Update the app to continue.")
                    : new ProtocolError(
                        decision.Code ?? ErrorCodes.NotAuthorized,
                        $"Action '{request.Action}' is not available to this caller."));
        }

        if (!ProtocolCodec.TryBindParameters(request.Params, capability.ParameterType, out var parameters, out var bindError))
        {
            _logger.LogWarning(
                "Invalid parameters for {Action} from {Caller} (request {RequestId}): {Reason}",
                request.Action,
                caller,
                request.Id,
                bindError.Message);

            return Failure(request, stopwatch, bindError);
        }

        var routine = HighFrequencyActions.Contains(request.Action) ? LogLevel.Debug : LogLevel.Information;

        _logger.Log(
            routine,
            "-> {Action} (request {RequestId}, caller {Caller})",
            request.Action,
            request.Id,
            caller);

        var context = new CapabilityContext(request.Id, caller);

        try
        {
            var result = await capability.InvokeAsync(parameters!, context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.Log(
                routine,
                "<- {Action} ok in {ElapsedMs} ms (request {RequestId})",
                request.Action,
                stopwatch.ElapsedMilliseconds,
                request.Id);

            return ResponseEnvelope.Ok(request.Id, request.Action, result, stopwatch.ElapsedMilliseconds);
        }
        catch (CapabilityException ex)
        {
            // Expected, classified failure: the code and message are safe to return verbatim.
            _logger.LogWarning(
                "<- {Action} failed with {Code} (request {RequestId}): {Message}",
                request.Action,
                ex.Code,
                request.Id,
                ex.Message);

            return Failure(request, stopwatch, new ProtocolError(ex.Code, ex.Message, ex.Details));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("<- {Action} cancelled (request {RequestId})", request.Action, request.Id);

            return Failure(
                request,
                stopwatch,
                new ProtocolError(ErrorCodes.InternalError, "The request was cancelled."));
        }
        catch (Exception ex)
        {
            // Unexpected: log everything locally, disclose nothing over the wire. An exception
            // message can carry paths, user names, or internals a caller has no business seeing.
            _logger.LogError(
                ex,
                "<- {Action} threw an unhandled exception (request {RequestId})",
                request.Action,
                request.Id);

            return Failure(
                request,
                stopwatch,
                new ProtocolError(ErrorCodes.InternalError, "The capability failed unexpectedly."));
        }
    }

    private static ResponseEnvelope Failure(RequestEnvelope request, Stopwatch stopwatch, ProtocolError error)
    {
        stopwatch.Stop();
        return ResponseEnvelope.Fail(request.Id, request.Action, error, stopwatch.ElapsedMilliseconds);
    }
}
