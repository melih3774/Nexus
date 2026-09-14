using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PcAssistant.ClaudeCode;
using Xunit;

namespace PcAssistant.Tests;

public class FailClosedPipeTests
{
    [Fact]
    public async Task MalformedPipeRequestIsDenied()
    {
        var name = "pcassistant-test-" + Guid.NewGuid().ToString("N");
        await using var server = new ApprovalPipeServer(name, (_, _) => throw new InvalidOperationException("bad event"));
        server.Start();
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync("not json");
        var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("deny", JsonDocument.Parse(response!).RootElement.GetProperty("decision").GetString());
    }
}
