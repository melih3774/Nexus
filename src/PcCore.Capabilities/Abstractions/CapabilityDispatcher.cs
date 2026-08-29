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
    private readonly ICapabilityRegistry _registry;
    private readonly ILogger<CapabilityDispatcher> _logger;

    public CapabilityDispatcher(ICapabilityRegistry registry, ILogger<CapabilityDispatcher> logger)
    {
        _registry = registry;
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

        _logger.LogInformation(
            "-> {Action} (request {RequestId}, caller {Caller})",
            request.Action,
            request.Id,
            caller);

        var context = new CapabilityContext(request.Id, caller);

        try
        {
            var result = await capability.InvokeAsync(parameters!, context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogInformation(
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
