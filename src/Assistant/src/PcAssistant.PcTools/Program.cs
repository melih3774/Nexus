using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PcAssistant.PcTools;
using PcAssistant.Windows;
using PcAssistant.Windows.Capture;

// PC Assistant runs this to find out what this machine can actually do. It is read-only, needs
// no approval broker because no model asked for it, and never starts the tool server.
if (args.Contains(CapabilityProbe.Argument))
{
    using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    return await CapabilityProbe.RunAsync(probeTimeout.Token);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// One registry for the process: capture ids issued by an observation are what later input
// resolves against, so both sides must be looking at the same table.
var captures = new CaptureRegistry();

builder.Services.AddSingleton<DesktopWindows>();
builder.Services.AddSingleton<SystemDiscovery>();
builder.Services.AddSingleton<ClipboardService>();
builder.Services.AddSingleton<UiAutomationService>();
builder.Services.AddSingleton(captures);
builder.Services.AddSingleton<WindowCapture>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<AudioService>();
builder.Services.AddSingleton<AppLauncher>();

// PC Assistant's own windows are excluded from every input and UI Automation action, so an
// approval card can never be operated by the thing it is gating.
ProtectedWindows.InitializeFromEnvironment();

builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<PcTools>().WithTools<SystemTools>().WithTools<ElevatedTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, ct) =>
    {
        try
        {
            var p = request.Params ?? throw new ArgumentException("Missing tool parameters.");

            // Administrator operations authorize themselves through PC Assistant's privileged
            // path, which is a different question from the ordinary tool gate, and they need a
            // budget matched to the command the owner approved rather than the 20 s an
            // observation gets.
            if (p.Name is "elevated_request" or "elevated_status")
            {
                using var privileged = CancellationTokenSource.CreateLinkedTokenSource(ct);
                privileged.CancelAfter(ElevationBudget(p.Arguments));
                return await next(request, privileged.Token);
            }

            // Checked before the owner is asked: a window Windows will not activate must not cost one
            // approval per attempt. Two consecutive failures stop the loop instead.
            if (p.Name == "input" && p.Arguments is { } arguments && TargetWindow(arguments) is { } hwnd &&
                ForegroundService.IsBlocked(hwnd))
                throw new ForegroundUnavailableException(ForegroundService.BlockedMessage(hwnd));
            await ToolGate.AuthorizeAsync(p.Name, p.Arguments, ct);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            operation.CancelAfter(TimeSpan.FromSeconds(20));
            return await next(request, operation.Token);
        }
        catch (Exception ex)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text =
                ex is OperationCanceledException ? "Operation cancelled or timed out. Inspect current state before retrying." : ex.Message }] };
        }
    }));
await builder.Build().RunAsync();
return 0;

// Which window an input call will act on, whether it was aimed by handle or by capture id.
// Resolved here, before the owner is asked, so a window Windows refuses to activate cannot
// cost one approval per attempt through either aiming mode.
long? TargetWindow(IDictionary<string, System.Text.Json.JsonElement> arguments)
{
    if (arguments.TryGetValue("hwnd", out var handle) && handle.TryGetInt64(out var hwnd)) return hwnd;
    if (arguments.TryGetValue("captureId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String &&
        captures.TryDescribe(id.GetString() ?? "", out var captured, out _, out _, out _)) return captured;
    return null;
}

// The owner's approval plus the command's own approved timeout, with a fixed margin for the
// approval itself and the two short pipe hops.
static TimeSpan ElevationBudget(IDictionary<string, System.Text.Json.JsonElement>? arguments)
{
    var seconds = 300;
    if (arguments is not null && arguments.TryGetValue("timeoutSeconds", out var value) &&
        value.TryGetInt32(out var requested) && requested is > 0 and <= 3600)
        seconds = requested;

    return TimeSpan.FromMinutes(35) + TimeSpan.FromSeconds(seconds);
}
