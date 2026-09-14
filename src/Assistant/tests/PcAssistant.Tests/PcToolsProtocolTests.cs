using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PcAssistant.ClaudeCode;
using Xunit;

namespace PcAssistant.Tests;

public class PcToolsProtocolTests
{
    [Fact]
    public async Task RealStdioServerListsToolsAndReturnsScreenshotImage()
    {
        var executable = FindPcTools();
        Assert.True(File.Exists(executable), "Desktop build must deploy the PC tools helper.");
        var pipeName = "pcassistant-pctools-test-" + Guid.NewGuid().ToString("N");
        await using var broker = new ApprovalPipeServer(pipeName, (_, _) => Task.FromResult(HookReply.Allow("read test")));
        broker.Start();

        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        process.StartInfo.Environment[HookProtocol.PipeEnvironmentVariable] = pipeName;
        Assert.True(process.Start());
        try
        {
            await SendAsync(process, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "PcAssistant.Tests", version = "1" }
            } });
            Assert.Equal(1, (await ResponseAsync(process, 1)).GetProperty("id").GetInt32());
            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            var tools = (await ResponseAsync(process, 2)).GetProperty("result").GetProperty("tools");
            Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "screenshot");
            Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "ui_snapshot");
            Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "input");

            await SendAsync(process, new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "screenshot", arguments = new { } } });
            var result = (await ResponseAsync(process, 3)).GetProperty("result");
            Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
            var image = Assert.Single(result.GetProperty("content").EnumerateArray(), c => c.GetProperty("type").GetString() == "image");
            var png = Convert.FromBase64String(image.GetProperty("data").GetString()!);
            Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static string FindPcTools()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "PcTools", "PcAssistant.PcTools.exe");
        if (File.Exists(deployed)) return deployed;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PcAssistant.Desktop", "bin", "Debug", "net10.0-windows", "PcTools", "PcAssistant.PcTools.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return deployed;
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ResponseAsync(Process process, int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
                return doc.RootElement.Clone();
        }
        throw new InvalidOperationException("PC tools closed before responding. " + await process.StandardError.ReadToEndAsync(timeout.Token));
    }
}
