using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace PcAssistant.Elevation.Host;

/// <summary>
/// The "--runner" mode: a short-lived elevated process in the owner's own session that runs
/// exactly one already-validated command and reports its result.
///
/// It holds no policy and accepts work from nowhere else. Its only input arrives on a
/// private pipe whose name it was given on its command line, created by the service with a
/// DACL that excludes unelevated callers, and it re-validates that input before running it.
/// It exits as soon as it has answered.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RunnerHost
{
    private const int ConnectTimeoutMs = 15_000;

    public static int Run(string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(ConnectTimeoutMs);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var line = reader.ReadLine();
            var request = string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<PrivilegedRequest>(line, PrivilegedProtocol.Json);

            var response = request is null
                ? new PrivilegedResponse { Status = PrivilegedStatus.Invalid, Message = "The runner received no usable operation." }
                : Execute(request);

            writer.WriteLine(JsonSerializer.Serialize(response, PrivilegedProtocol.Json));
            pipe.WaitForPipeDrain();
            return 0;
        }
        catch (Exception)
        {
            // Nothing useful can be reported from here; the service treats a silent runner as
            // a cancelled or failed operation, which is the honest outcome.
            return 1;
        }
    }

    private static PrivilegedResponse Execute(PrivilegedRequest request)
    {
        // Validated a third time, in the process that will actually start the program.
        var validation = OperationValidator.Validate(request);
        if (!validation.Ok || request.Kind != PrivilegedOperationKind.Run)
            return PrivilegedResponse.For(request, PrivilegedStatus.Invalid, validation.Error ?? "The runner only executes Run operations.");

        var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds == 0
            ? OperationValidator.DefaultTimeoutSeconds
            : request.TimeoutSeconds);

        using var process = new Process();
        process.StartInfo.FileName = Path.GetFullPath(request.Executable!);
        foreach (var argument in request.Arguments ?? Array.Empty<string>())
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
        process.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            process.StartInfo.WorkingDirectory = request.WorkingDirectory;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return PrivilegedResponse.For(request, PrivilegedStatus.Failed, "Could not start the program: " + ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            return PrivilegedResponse.For(request, PrivilegedStatus.TimedOut,
                $"The command was still running after {timeout.TotalSeconds:0} seconds and its process tree was terminated.")
                with { DurationMilliseconds = stopwatch.ElapsedMilliseconds };
        }

        // WaitForExit(timeout) does not wait for the redirected streams to drain; this does.
        process.WaitForExit();

        return new PrivilegedResponse
        {
            RequestId = request.RequestId,
            Status = PrivilegedStatus.Ok,
            ExitCode = process.ExitCode,
            StandardOutput = Truncate(Result(stdout)),
            StandardError = Truncate(Result(stderr)),
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            Message = $"Exit code {process.ExitCode} after {stopwatch.ElapsedMilliseconds} ms.",
        };
    }

    private static string Result(Task<string> reader)
    {
        try { return reader.GetAwaiter().GetResult(); }
        catch (Exception) { return string.Empty; }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= PrivilegedProtocol.MaxCapturedCharacters
            ? value
            : value[..PrivilegedProtocol.MaxCapturedCharacters] +
              $"\n… truncated at {PrivilegedProtocol.MaxCapturedCharacters} characters …";
    }
}
