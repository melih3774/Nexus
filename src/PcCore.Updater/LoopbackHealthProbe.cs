using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using PcCore.Update;

namespace PcCore.Updater;

/// <summary>
/// "Did the new PC Core start correctly?", answered by PC Core itself.
/// <para>
/// A process that exists is not a PC Core that works. This waits for the session token the new
/// process writes at start, calls <c>system.get_info</c> over loopback with it - which runs the whole
/// dispatch path, not a special health endpoint - checks the version it reports is the one that was
/// installed, and then watches it stay up for a few more seconds, because the failures worth catching
/// are the ones that happen just after start.
/// </para>
/// </summary>
internal sealed class LoopbackHealthProbe : INexusHealthProbe
{
    private readonly INexusProcessControl _processes;
    private readonly string _installDirectory;
    private readonly Action<string> _log;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan StaysUpFor { get; init; } = TimeSpan.FromSeconds(5);

    public LoopbackHealthProbe(INexusProcessControl processes, string installDirectory, Action<string> log)
    {
        _processes = processes;
        _installDirectory = installDirectory;
        _log = log;
    }

    public static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCore", "session.token");

    public async Task<HealthResult> WaitUntilHealthyAsync(
        int processId,
        string? expectedVersion,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var port = LoopbackPort();
        var deadline = DateTimeOffset.UtcNow + Timeout;
        string? lastProblem = "no answer yet";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_processes.IsRunning(processId))
            {
                return new HealthResult(false, "it exited during start-up");
            }

            var token = FreshToken(startedAtUtc);

            if (token is null)
            {
                lastProblem = "it has not written its session token";
            }
            else
            {
                var (version, problem) = await AskVersionAsync(http, port, token, cancellationToken).ConfigureAwait(false);

                if (version is not null)
                {
                    if (expectedVersion is not null && !string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        return new HealthResult(false, $"it reports version {version}, expected {expectedVersion}");
                    }

                    await Task.Delay(StaysUpFor, cancellationToken).ConfigureAwait(false);

                    return _processes.IsRunning(processId)
                        ? new HealthResult(true, $"PC Core {version} answered on port {port}")
                        : new HealthResult(false, "it answered, then exited");
                }

                lastProblem = problem;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        _log($"Health check gave up: {lastProblem}.");
        return new HealthResult(false, $"it did not answer within {Timeout.TotalSeconds:0} seconds ({lastProblem})");
    }

    private static string? FreshToken(DateTimeOffset startedAtUtc)
    {
        try
        {
            var info = new FileInfo(TokenPath);

            // Written by this start, not left over from the process that was just stopped. A little
            // slack for filesystem timestamp granularity.
            if (!info.Exists || info.LastWriteTimeUtc < startedAtUtc.UtcDateTime.AddSeconds(-2))
            {
                return null;
            }

            var token = File.ReadAllText(info.FullName).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<(string? Version, string Problem)> AskVersionAsync(
        HttpClient http, int port, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/rpc")
            {
                Content = new StringContent(
                    """{"protocol_version":1,"id":"updater-health","type":"request","action":"system.get_info"}""",
                    Encoding.UTF8,
                    "application/json"),
            };

            request.Headers.Add("X-PcCore-Token", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                return (null, "system.get_info did not succeed");
            }

            return root.GetProperty("result").TryGetProperty("pc_core_version", out var version)
                ? (version.GetString(), string.Empty)
                : (null, "system.get_info carried no version");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return (null, "the loopback endpoint is not answering yet");
        }
    }

    private int LoopbackPort()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_installDirectory, "appsettings.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (json.RootElement.TryGetProperty("PcCore", out var core)
                && core.TryGetProperty("Transport", out var transport)
                && transport.TryGetProperty("Port", out var port)
                && port.TryGetInt32(out var value))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return 47811;
    }
}
