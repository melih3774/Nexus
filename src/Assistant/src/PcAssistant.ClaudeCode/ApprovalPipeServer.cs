using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace PcAssistant.ClaudeCode;

/// <summary>
/// Listens on a per-task named pipe for hook callbacks from the Claude Code process.
/// The pipe ACL allows only the current user, and it is never exposed on the network.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApprovalPipeServer : IAsyncDisposable
{
    private const int MaxConcurrentHooks = 8;

    private readonly string _pipeName;
    private readonly Func<HookRequest, CancellationToken, Task<HookReply>> _handler;
    private readonly Action<string>? _diagnostics;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _listeners = new();

    public ApprovalPipeServer(
        string pipeName,
        Func<HookRequest, CancellationToken, Task<HookReply>> handler,
        Action<string>? diagnostics = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _diagnostics = diagnostics;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        for (var i = 0; i < MaxConcurrentHooks; i++)
            _listeners.Add(Task.Run(ListenLoopAsync));
    }

    private async Task ListenLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                await HandleConnectionAsync(server).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad hook connection must never take the app down.
                _diagnostics?.Invoke("hook pipe error: " + ex.Message);
                await Task.Delay(50).ConfigureAwait(false);
            }
            finally
            {
                if (server is not null)
                {
                    try { if (server.IsConnected) server.Disconnect(); } catch { /* already gone */ }
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxConcurrentHooks,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;

        HookReply reply;
        try
        {
            var request = JsonSerializer.Deserialize<HookRequest>(line, HookProtocol.Json);
            reply = request is null
                ? HookReply.Deny("Unparsable hook payload.")
                : await _handler(request, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reply = HookReply.Deny("PC Assistant stopped this task.");
        }
        catch (Exception ex)
        {
            // A malformed event falls back to a generic outcome instead of crashing.
            _diagnostics?.Invoke("hook handler error: " + ex.Message);
            reply = HookReply.Deny("PC Assistant could not evaluate this event.");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(reply, HookProtocol.Json)).ConfigureAwait(false);

        try { server.WaitForPipeDrain(); } catch { /* client may have exited */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested) await _shutdown.CancelAsync().ConfigureAwait(false);

        // Unblock listeners parked in WaitForConnectionAsync.
        foreach (var _ in _listeners)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                await client.ConnectAsync(25).ConfigureAwait(false);
            }
            catch
            {
                // Nothing listening any more; that is the desired end state.
            }
        }

        try { await Task.WhenAll(_listeners).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { /* best effort */ }

        _shutdown.Dispose();
    }
}
