using System.IO.Pipes;

using Microsoft.Extensions.Logging.Abstractions;

using PcCore.Capabilities.Assistant;
using PcCore.Windows.Assistant;

using Xunit;

namespace PcCore.Tests.Capabilities;

/// <summary>
/// The Windows half of reaching PC Assistant: nothing is sent to a pipe unless the process serving it is
/// the executable the owner configured. Served here by this test process, over a pipe of its own.
/// </summary>
public sealed class AssistantPipeConnectorTests
{
    private static readonly string ThisProcess = Environment.ProcessPath!;

    [Fact]
    public async Task AServerRunningTheTrustedExecutableIsAccepted()
    {
        var name = "nexus-assistant-connector-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var connector = Connector(name, ThisProcess.ToUpperInvariant());
        await using var stream = await connector.ConnectAsync(TestContext.Current.CancellationToken);
        await waiting;

        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task AnyOtherServerIsRefusedBeforeAByteIsWritten()
    {
        var name = "nexus-assistant-connector-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var connector = Connector(name, @"C:\Program Files\PcAssistant\PcAssistant.Desktop.exe");
        var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => connector.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AssistantAvailability.NotTrusted, refused.State);

        await waiting;
        var buffer = new byte[16];
        Assert.Equal(0, await server.ReadAsync(buffer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoServerIsNotRunning()
    {
        var connector = Connector("nexus-assistant-connector-" + Guid.NewGuid().ToString("N"), ThisProcess);

        var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => connector.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AssistantAvailability.NotRunning, refused.State);
    }

    [Fact]
    public void ThePipeNameIsPerUser()
    {
        Assert.StartsWith("PcAssistant.NexusBridge.S-1-", NamedPipeAssistantConnector.PipeName(), StringComparison.Ordinal);
    }

    private static NamedPipeAssistantConnector Connector(string pipeName, string serverExecutable) =>
        new(new AssistantBridgeOptions { Enabled = true, ServerExecutable = serverExecutable, ConnectTimeoutMilliseconds = 300 },
            NullLogger<NamedPipeAssistantConnector>.Instance,
            pipeName);
}
