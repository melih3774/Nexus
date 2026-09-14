using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace PcAssistant.Elevation;

/// <summary>What the owner is told about the privileged helper, in the Capabilities panel.</summary>
public enum HelperAvailability
{
    Unknown = 0,
    /// <summary>Nothing has been installed. Every privileged request fails honestly.</summary>
    NotInstalled = 1,
    /// <summary>Binary present, Windows service not running.</summary>
    ServiceStopped = 2,
    /// <summary>Binary present and service running, but it speaks a different protocol.</summary>
    VersionMismatch = 3,
    /// <summary>Installed and answering, but this account has no administrator token to elevate to.</summary>
    NoAdministratorToken = 4,
    /// <summary>Installed, running, protocol matched, elevation available.</summary>
    Ready = 5,
    /// <summary>Something else went wrong; Detail says what.</summary>
    Error = 6,
}

public sealed record HelperStatus(
    HelperAvailability Availability,
    string Detail,
    string? HelperVersion = null,
    string? OwnerAccount = null,
    int? SessionId = null)
{
    public string DisplayText => Availability switch
    {
        HelperAvailability.NotInstalled => "Not installed",
        HelperAvailability.ServiceStopped => "Installed / service stopped",
        HelperAvailability.VersionMismatch => "Installed / version mismatch",
        HelperAvailability.NoAdministratorToken => "Installed / no administrator token on this account",
        HelperAvailability.Ready => "Installed / Ready",
        HelperAvailability.Error => "Error",
        _ => "Unknown",
    };
}

/// <summary>
/// The only thing in PC Assistant that talks to the privileged helper.
///
/// It is a client, not an authority: it cannot make the helper do anything the helper's own
/// validation would refuse, and it does not carry a secret that would make it trusted. The
/// helper decides, from the connection itself, whether the caller is the owner account this
/// helper was installed for.
/// </summary>
/// <summary>
/// The pipe conversation, behind a seam. Substituting it is how the approval semantics —
/// deny means nothing is sent, allow means exactly the approved operation is sent — can be
/// proven without an installed service.
/// </summary>
public interface IPrivilegedClient
{
    Task<HelperStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<PrivilegedResponse> SendAsync(PrivilegedRequest request, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed class PrivilegedClient : IPrivilegedClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ResponseOverhead = TimeSpan.FromSeconds(45);

    private readonly string _clientVersion;

    public PrivilegedClient(string? clientVersion = null)
        => _clientVersion = clientVersion ??
                            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Honest readiness. Never claims Ready without a real round trip through the service,
    /// because a present binary says nothing about a running, compatible helper.
    /// </summary>
    public async Task<HelperStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!HelperPaths.IsInstalled)
        {
            return new HelperStatus(HelperAvailability.NotInstalled,
                "No privileged helper is installed. Administrator operations are unavailable until you install one.");
        }

        var serviceState = QueryServiceState();
        if (serviceState is not null && serviceState != ServiceControllerStatus.Running)
        {
            return new HelperStatus(HelperAvailability.ServiceStopped,
                $"The {PrivilegedProtocol.ServiceDisplayName} service is {serviceState.ToString()!.ToLowerInvariant()}. " +
                "Start it, or reinstall the helper.");
        }

        PrivilegedResponse response;
        try
        {
            response = await SendAsync(new PrivilegedRequest { Kind = PrivilegedOperationKind.Status },
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.TimeoutException)
        {
            return new HelperStatus(HelperAvailability.ServiceStopped,
                "The privileged helper is installed but did not answer on its local pipe.");
        }
        catch (Exception ex)
        {
            return new HelperStatus(HelperAvailability.Error, "Could not reach the privileged helper: " + ex.Message);
        }

        return response.Status switch
        {
            PrivilegedStatus.VersionMismatch => new HelperStatus(HelperAvailability.VersionMismatch,
                response.Message, response.HelperVersion),
            PrivilegedStatus.Denied => new HelperStatus(HelperAvailability.Error, response.Message, response.HelperVersion),
            PrivilegedStatus.Ok when response.ElevationAvailable == false => new HelperStatus(
                HelperAvailability.NoAdministratorToken, response.Message, response.HelperVersion,
                response.OwnerAccount, response.SessionId),
            PrivilegedStatus.Ok => new HelperStatus(HelperAvailability.Ready, response.Message,
                response.HelperVersion, response.OwnerAccount, response.SessionId),
            _ => new HelperStatus(HelperAvailability.Error, response.Message, response.HelperVersion),
        };
    }

    /// <summary>
    /// One request, one response. The client validates first so an obviously malformed request
    /// never even reaches the privileged boundary; the helper validates again regardless.
    /// </summary>
    public async Task<PrivilegedResponse> SendAsync(PrivilegedRequest request, CancellationToken cancellationToken = default)
    {
        var outgoing = request with
        {
            Protocol = PrivilegedProtocol.Version,
            IssuedAt = DateTimeOffset.UtcNow,
            ClientVersion = _clientVersion,
        };

        var validation = OperationValidator.Validate(outgoing);
        if (!validation.Ok)
            return PrivilegedResponse.For(outgoing, PrivilegedStatus.Invalid, validation.Error!);

        var json = JsonSerializer.Serialize(outgoing, PrivilegedProtocol.Json);
        if (Encoding.UTF8.GetByteCount(json) > PrivilegedProtocol.MaxMessageBytes)
            return PrivilegedResponse.For(outgoing, PrivilegedStatus.Invalid, "The request is too large for the helper protocol.");

        await using var pipe = new NamedPipeClientStream(
            ".", PrivilegedProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (System.TimeoutException)
        {
            throw new System.TimeoutException("The privileged helper did not accept a connection within " +
                                       $"{ConnectTimeout.TotalSeconds:0} seconds.");
        }

        // Deliberately not disposed: both leave the pipe open and the writer auto-flushes, so
        // disposal would have nothing left to do except fail once the helper has hung up.
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);

        var budget = ResponseOverhead + TimeSpan.FromSeconds(
            outgoing.Kind == PrivilegedOperationKind.Run
                ? (outgoing.TimeoutSeconds == 0 ? OperationValidator.DefaultTimeoutSeconds : outgoing.TimeoutSeconds)
                : 0);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);

        string? line;
        try
        {
            line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new System.TimeoutException("The privileged helper did not answer within " + budget.TotalSeconds + " seconds.");
        }

        if (string.IsNullOrWhiteSpace(line))
            return PrivilegedResponse.For(outgoing, PrivilegedStatus.Failed, "The privileged helper closed the connection without answering.");

        var response = JsonSerializer.Deserialize<PrivilegedResponse>(line, PrivilegedProtocol.Json)
                       ?? PrivilegedResponse.For(outgoing, PrivilegedStatus.Failed, "The privileged helper sent an unreadable answer.");

        // A helper that answers with a different protocol is refused here too, so a stale
        // helper cannot be trusted merely because it managed to reply.
        if (response.Protocol != PrivilegedProtocol.Version && response.Status != PrivilegedStatus.VersionMismatch)
        {
            return response with
            {
                Status = PrivilegedStatus.VersionMismatch,
                Message = $"The privileged helper answered with protocol {response.Protocol}; " +
                          $"PC Assistant speaks protocol {PrivilegedProtocol.Version}. Reinstall the helper.",
            };
        }

        return response;
    }

    private static ServiceControllerStatus? QueryServiceState()
    {
        try
        {
            using var controller = new ServiceController(PrivilegedProtocol.ServiceName);
            return controller.Status;
        }
        catch (Exception)
        {
            // Not registered, or not queryable. The pipe round trip is the real test.
            return null;
        }
    }
}
