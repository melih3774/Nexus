using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PcAssistant.Elevation;
using PcAssistant.Elevation.Host;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The privileged helper's refusals, proven against the real <see cref="PrivilegedPipeServer"/>
/// and the real <see cref="RequestHandler"/> — the shipped classes, not a re-implementation.
///
/// The server is hosted here on a test-only pipe name, unelevated. That is exactly why these
/// tests can run without an installed service, and it is also safe: an unelevated host cannot
/// call WTSQueryUserToken at all, so no request it accepts could elevate anything. Every
/// assertion below is therefore about the boundary's *refusals* and its *identity checks* —
/// the parts that must hold before elevation is ever reached.
///
/// The elevation itself is proven separately, against the installed service, in
/// PrivilegedHelperLiveTests.
/// </summary>
public sealed class PrivilegedBoundaryServerTests : IDisposable
{
    private readonly string _pipeName = "PcAssistant.Elevation.Test." + Guid.NewGuid().ToString("N");
    private readonly PrivilegedPipeServer _server;
    private readonly List<string> _log = [];

    public PrivilegedBoundaryServerTests()
        : this(state => state)
    {
    }

    private PrivilegedBoundaryServerTests(Func<HelperState, HelperState> shape)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;

        var state = shape(new HelperState
        {
            OwnerSid = sid.Value,
            OwnerAccount = identity.Name,
            ClientExecutable = Environment.ProcessPath ?? string.Empty,
            InstalledVersion = "test",
            Protocol = PrivilegedProtocol.Version,
        });

        var handler = new RequestHandler(state, message => _log.Add(message));
        _server = new PrivilegedPipeServer(sid, handler.Handle, message => _log.Add(message), _pipeName);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    // ---------------------------------------------------------------- malformed

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"kind\":")]
    [InlineData("[1,2,3]")]
    public async Task GarbageIsRefusedAndTheServerKeepsServing(string raw)
    {
        var refused = await ExchangeAsync(raw);
        Assert.Equal(PrivilegedStatus.Invalid, refused.Status);

        // The listener must survive a bad connection; the next request still works.
        var after = await ExchangeAsync(Serialize(new PrivilegedRequest { Kind = PrivilegedOperationKind.Status }));
        Assert.Equal(PrivilegedStatus.Ok, after.Status);
    }

    [Fact]
    public async Task AnOversizedRequestIsDiscardedWholeRatherThanPartiallyParsed()
    {
        var padding = new string('x', PrivilegedProtocol.MaxMessageBytes + 4096);
        var response = await ExchangeAsync("{\"kind\":\"Status\",\"protocol\":1,\"reason\":\"" + padding + "\"}");

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
    }

    [Fact]
    public async Task AnUnknownOperationNameIsRefused()
    {
        var response = await ExchangeAsync(
            "{\"protocol\":1,\"kind\":\"BecomeSystem\",\"requestId\":\"" + Guid.NewGuid().ToString("N") +
            "\",\"issuedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}");

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
    }

    // ----------------------------------------------------------------- protocol

    [Fact]
    public async Task AnOlderHelperRefusesANewerClientsProtocolInsteadOfGuessing()
    {
        var response = await ExchangeAsync(Serialize(
            new PrivilegedRequest { Kind = PrivilegedOperationKind.Status, ClientVersion = "9.9.9" },
            protocolOverride: PrivilegedProtocol.Version + 5));

        Assert.Equal(PrivilegedStatus.VersionMismatch, response.Status);
        Assert.Contains("protocol", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9.9.9", response.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- freshness / replay

    [Fact]
    public async Task ARequestIdIsAcceptedOnceAndNeverAgain()
    {
        var request = new PrivilegedRequest { Kind = PrivilegedOperationKind.Status };

        var first = await ExchangeAsync(Serialize(request));
        var replay = await ExchangeAsync(Serialize(request));

        Assert.Equal(PrivilegedStatus.Ok, first.Status);
        Assert.Equal(PrivilegedStatus.Invalid, replay.Status);
        Assert.Contains("replay", replay.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-90)]     // far in the past
    [InlineData(90)]      // far in the future
    public async Task AStaleOrSkewedTimestampIsRefused(int minutes)
    {
        var response = await ExchangeAsync(Serialize(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Status,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(minutes),
        }));

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
        Assert.Contains("stale", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOverlongRequestIdIsRefused()
    {
        var response = await ExchangeAsync(Serialize(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Status,
            RequestId = new string('a', 200),
        }));

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
    }

    // ------------------------------------------------------------ parameters

    [Fact]
    public async Task TheHelperValidatesParametersItselfEvenWhenTheClientDidNot()
    {
        // Sent as raw JSON so nothing client-side gets a chance to sanitise it first.
        var response = await ExchangeAsync(
            "{\"protocol\":1,\"kind\":\"Run\",\"requestId\":\"" + Guid.NewGuid().ToString("N") +
            "\",\"issuedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") +
            "\",\"executable\":\"\\\\\\\\evil\\\\share\\\\tool.exe\",\"arguments\":[\"x\"]}");

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
        Assert.Contains("UNC", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AClaimThatTheOwnerAlreadyApprovedIsNotAnInput()
    {
        // "Claude said this was safe" carries no weight: the helper judges the request only by
        // what it can check itself, and an unknown field changes nothing.
        var response = await ExchangeAsync(
            "{\"protocol\":1,\"kind\":\"Run\",\"requestId\":\"" + Guid.NewGuid().ToString("N") +
            "\",\"issuedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") +
            "\",\"ownerApproved\":true,\"trusted\":true,\"executable\":\"notafullpath.exe\"}");

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
    }

    [Fact]
    public async Task WeakeningUacIsRefusedAtTheBoundaryNotOnlyInTheClient()
    {
        var reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
        var response = await ExchangeAsync(Serialize(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Run,
            Executable = reg,
            Arguments = ["add", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System",
                         "/v", "EnableLUA", "/t", "REG_DWORD", "/d", "0", "/f"],
        }));

        Assert.Equal(PrivilegedStatus.Invalid, response.Status);
        Assert.Contains("User Account Control", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- identity

    [Fact]
    public async Task AClientWhoseImagePathWasNotTheOneInstalledForIsRefused()
    {
        using var strict = new PrivilegedBoundaryServerTests(
            state => state with { ClientExecutable = @"C:\Somewhere\Else\PcAssistant.Desktop.exe" });

        var response = await strict.ExchangeAsync(Serialize(new PrivilegedRequest { Kind = PrivilegedOperationKind.Status }));

        Assert.Equal(PrivilegedStatus.Denied, response.Status);
        Assert.Contains("Reinstall", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHelperWithNoRecordedOwnerRefusesEverything()
    {
        using var ownerless = new PrivilegedBoundaryServerTests(state => state with { OwnerSid = string.Empty });

        var response = await ownerless.ExchangeAsync(Serialize(new PrivilegedRequest { Kind = PrivilegedOperationKind.Status }));

        Assert.Equal(PrivilegedStatus.Denied, response.Status);
        Assert.Contains("no recorded owner", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARequestFromADifferentAccountIsRefused()
    {
        using var otherOwner = new PrivilegedBoundaryServerTests(
            state => state with
            {
                // A well-known SID that is definitely not the account running these tests.
                OwnerSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                OwnerAccount = "NT AUTHORITY\\SYSTEM",
            });

        var response = await otherOwner.ExchangeAsync(Serialize(new PrivilegedRequest { Kind = PrivilegedOperationKind.Status }));

        Assert.Equal(PrivilegedStatus.Denied, response.Status);
        Assert.Contains("refuses requests", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------- elevation floor

    [Fact]
    public async Task AnUnelevatedHostCannotElevateAnythingAndSaysSo()
    {
        // The clearest possible statement of the floor: without LocalSystem there is no way to
        // reach the owner's administrator token, and the helper reports that rather than
        // improvising a fallback.
        var whoami = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        var response = await ExchangeAsync(Serialize(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Run,
            Executable = whoami,
            Arguments = ["/groups"],
            TimeoutSeconds = 20,
        }));

        Assert.Equal(PrivilegedStatus.NotElevatable, response.Status);
    }

    // ---------------------------------------------------------------- helpers

    private static string Serialize(PrivilegedRequest request, int? protocolOverride = null)
    {
        var json = JsonSerializer.Serialize(request, PrivilegedProtocol.Json);
        if (protocolOverride is null) return json;

        using var document = JsonDocument.Parse(json);
        var patched = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.EnumerateObject()) patched[property.Name] = property.Value.Clone();
        patched["protocol"] = JsonSerializer.SerializeToElement(protocolOverride.Value);
        return JsonSerializer.Serialize(patched, PrivilegedProtocol.Json);
    }

    private async Task<PrivilegedResponse> ExchangeAsync(string line)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(line);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reply = await reader.ReadLineAsync(deadline.Token);

        Assert.False(string.IsNullOrWhiteSpace(reply), "the helper closed the connection without answering");
        return JsonSerializer.Deserialize<PrivilegedResponse>(reply!, PrivilegedProtocol.Json)!;
    }
}
