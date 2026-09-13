using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Clipboard;
using PcCore.Capabilities.Files;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.System;
using PcCore.Protocol;
using PcCore.Tests.TestSupport;
using PcCore.Transport.Remote;

using Xunit;

namespace PcCore.Tests.Remote;

/// <summary>
/// Files and Clipboard over the real remote transport, with the real policy and the real owner-session
/// service: who may reach them, what a stale session gets, and whether a megabyte upload chunk fits
/// through the socket that used to refuse anything over 64 KB.
/// </summary>
public sealed class FilesOverRemoteTransportTests : IAsyncLifetime
{
    private static readonly string[] FileAndClipboardActions =
    [
        "files.roots",
        "files.list",
        "files.stat",
        "files.read",
        "files.hash",
        "files.upload.begin",
        "files.upload.chunk",
        "files.upload.finish",
        "files.upload.cancel",
        "clipboard.get",
        "clipboard.set",
    ];

    private readonly TempDirectory _root = new("remote-files");
    private readonly OwnerPhoneKey _ownerKey = new();
    private readonly InMemoryClipboard _clipboard = new();

    private TempStateDirectory _state = null!;
    private ServiceProvider _services = null!;
    private RemoteTransport _transport = null!;
    private PairingCompletion _phone = null!;
    private IPcIdentityProvider _identity = null!;
    private Uri _uri = null!;

    public async ValueTask InitializeAsync()
    {
        _state = new TempStateDirectory("remote-files-state");

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(_state.Directory);
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton(new ScreenOptions());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IDeviceEndpointProvider, NoDeviceEndpoint>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ISecurityStateService, SecurityStateService>();
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();

        var options = new RemoteTransportOptions { Port = FreePort() };
        services.AddSingleton(options);
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddRemoteControlDoubles();
        services.AddSingleton<RemoteStreamCoordinator>();

        // The file and clipboard surface, composed the way the host composes it.
        services.AddSingleton(AllowedRoots.FromRealPaths(_root.Path));
        services.AddSingleton<IRealPathResolver, PcCore.Windows.Win32RealPathResolver>();
        services.AddSingleton<IKnownFolderResolver, PortableKnownFolderResolver>();
        services.AddSingleton<PathAuthorizer>();
        services.AddSingleton(new FilesOptions());
        services.AddSingleton(FileProtections.None);
        services.AddSingleton<FileUploadService>();
        services.AddSingleton<ICapabilityAction, ListFileRootsAction>();
        services.AddSingleton<ICapabilityAction, ListFilesAction>();
        services.AddSingleton<ICapabilityAction, StatFileAction>();
        services.AddSingleton<ICapabilityAction, ReadFileAction>();
        services.AddSingleton<ICapabilityAction, HashFileAction>();
        services.AddSingleton<ICapabilityAction, BeginUploadAction>();
        services.AddSingleton<ICapabilityAction, UploadChunkAction>();
        services.AddSingleton<ICapabilityAction, FinishUploadAction>();
        services.AddSingleton<ICapabilityAction, CancelUploadAction>();
        services.AddSingleton<IClipboardAccess>(_clipboard);
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<ICapabilityAction, GetClipboardAction>();
        services.AddSingleton<ICapabilityAction, SetClipboardAction>();
        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();

        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        _services = services.BuildServiceProvider();
        _identity = _services.GetRequiredService<IPcIdentityProvider>();

        var pairing = _services.GetRequiredService<IPairingService>();
        var session = pairing.Begin(replaceExisting: true);
        Assert.True(PairingPayloadV2.TryDecode(session.Payload, out var payload));
        _phone = pairing.Complete(payload!.SessionId, payload.PairingSecret, "Test Phone");

        _transport = new RemoteTransport(_services, options, _services.GetRequiredService<RemoteSessionRegistry>(), _services.GetRequiredService<ILoggerFactory>());
        await _transport.StartAsync(new RemoteEndpoint(IPAddress.Loopback, options.Port, "test loopback endpoint"));
        _uri = new Uri($"ws://127.0.0.1:{options.Port}{RemoteTransport.Path}");
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        _ownerKey.Dispose();
        _state.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void EveryFileAndClipboardActionNeedsTheOwnerNotJustThePhone()
    {
        foreach (var action in FileAndClipboardActions)
        {
            Assert.Contains(action, DefaultCapabilityPolicy.RemoteAllowedActions);
            Assert.DoesNotContain(action, DefaultCapabilityPolicy.ActionsWithoutOwnerAuthorization);
        }
    }

    [Fact]
    public async Task APairedPhoneWhoseOwnerHasNotAuthenticatedReachesNoneOfIt()
    {
        var token = TestContext.Current.CancellationToken;
        _root.CreateFile("secret.txt", "not for a stolen phone");

        await using var client = await ConnectUnauthorizedAsync(token);

        foreach (var action in FileAndClipboardActions)
        {
            var response = await client.SendRequestAsync(action, new { path = _root.Combine("secret.txt"), text = "x" }, token);

            Assert.False(response.Success, $"{action} was allowed without owner authorisation");
            Assert.Equal(ErrorCodes.NotAuthorized, response.Error!.Code);
        }

        Assert.Null(_clipboard.Text);
    }

    [Fact]
    public async Task AnOwnerSessionBrowsesDownloadsAndUploadsInMegabyteChunks()
    {
        var token = TestContext.Current.CancellationToken;
        var original = RandomNumberGenerator.GetBytes(FileUploadService.MaxChunkBytes + 12_345);
        File.WriteAllBytes(_root.Combine("download.bin"), original);

        await using var client = await ConnectAsync(token);

        var listing = Read(await Ok(client, "files.list", new { path = _root.Path }, token));
        Assert.Contains(listing.GetProperty("entries").EnumerateArray(), e => e.GetProperty("name").GetString() == "download.bin");

        // Download, ranged.
        using var downloaded = new MemoryStream();
        while (true)
        {
            var chunk = Read(await Ok(client, "files.read", new { path = _root.Combine("download.bin"), offset = downloaded.Length }, token));
            downloaded.Write(Convert.FromBase64String(chunk.GetProperty("data").GetString()!));

            if (chunk.GetProperty("end_of_file").GetBoolean())
            {
                break;
            }
        }

        Assert.Equal(original, downloaded.ToArray());

        // Upload, with a full megabyte in one message: 1.4 MB of base64 over a socket whose handshake
        // limit is 64 KB.
        var begun = Read(await Ok(client, "files.upload.begin", new { directory = _root.Path, name = "upload.bin", size_bytes = original.Length }, token));
        var id = begun.GetProperty("upload_id").GetString();
        Assert.Equal(FileUploadService.MaxChunkBytes, begun.GetProperty("max_chunk_bytes").GetInt32());

        await Ok(client, "files.upload.chunk", new { upload_id = id, offset = 0, data = Convert.ToBase64String(original, 0, FileUploadService.MaxChunkBytes) }, token);
        await Ok(client, "files.upload.chunk", new { upload_id = id, offset = FileUploadService.MaxChunkBytes, data = Convert.ToBase64String(original, FileUploadService.MaxChunkBytes, 12_345) }, token);
        await Ok(client, "files.upload.finish", new { upload_id = id, sha256 = Convert.ToHexStringLower(SHA256.HashData(original)) }, token);

        Assert.Equal(original, File.ReadAllBytes(_root.Combine("upload.bin")));
    }

    [Fact]
    public async Task ClipboardTextGoesBothWaysAndThePhoneRecognisesItsOwn()
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(token);

        _clipboard.Text = "copied on the PC";
        var fromPc = Read(await Ok(client, "clipboard.get", null, token));
        Assert.Equal("copied on the PC", fromPc.GetProperty("text").GetString());
        Assert.Equal("pc", fromPc.GetProperty("origin").GetString());

        var set = Read(await Ok(client, "clipboard.set", new { text = "sent from the phone ✓" }, token));
        Assert.True(set.GetProperty("changed").GetBoolean());
        Assert.Equal("sent from the phone ✓", _clipboard.Text);

        Assert.Equal("phone", Read(await Ok(client, "clipboard.get", null, token)).GetProperty("origin").GetString());
    }

    /// <summary>
    /// A transfer that outlives its owner's authorisation stops at the next chunk. The partial upload is
    /// still the phone's to resume once the owner is back, and the idle sweep's to delete if not.
    /// </summary>
    [Fact]
    public async Task ATransferStopsTheMomentThePhoneLocksAndResumesAfterReconnecting()
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = RandomNumberGenerator.GetBytes(4000);
        string ticket;
        string? id;

        await using (var first = await ConnectUnauthorizedAsync(token))
        {
            ticket = await first.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);

            id = Read(await Ok(first, "files.upload.begin", new { directory = _root.Path, name = "resume.bin", size_bytes = bytes.Length }, token))
                .GetProperty("upload_id").GetString();
            await Ok(first, "files.upload.chunk", new { upload_id = id, offset = 0, data = Convert.ToBase64String(bytes, 0, 1500) }, token);
        }

        // A new socket: nothing is reachable until the owner session is resumed on it.
        await using var second = await ConnectUnauthorizedAsync(token);
        var stale = await second.SendRequestAsync("files.upload.chunk", new { upload_id = id, offset = 1500, data = Convert.ToBase64String(bytes, 1500, 100) }, token);
        Assert.Equal(ErrorCodes.NotAuthorized, stale.Error!.Code);

        await Ok(second, "session.resume", new { ticket }, token);

        var resumed = Read(await Ok(second, "files.upload.begin", new { directory = _root.Path, name = "resume.bin", size_bytes = bytes.Length }, token));
        Assert.True(resumed.GetProperty("resumed").GetBoolean());
        Assert.Equal(1500, resumed.GetProperty("received_bytes").GetInt64());

        await Ok(second, "files.upload.chunk", new { upload_id = id, offset = 1500, data = Convert.ToBase64String(bytes, 1500, 2500) }, token);

        // The owner locks the phone before finishing: the finish is refused, and nothing is in place.
        await Ok(second, "session.lock", null, token);
        var locked = await second.SendRequestAsync("files.upload.finish", new { upload_id = id }, token);
        Assert.Equal(ErrorCodes.NotAuthorized, locked.Error!.Code);
        Assert.False(File.Exists(_root.Combine("resume.bin")));
    }

    [Fact]
    public async Task TheHandshakeStillRefusesALargeMessage()
    {
        var token = TestContext.Current.CancellationToken;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_uri, token);

        var buffer = new byte[16 * 1024];
        await socket.ReceiveAsync(buffer, token); // the challenge

        var oversized = Encoding.UTF8.GetBytes(new string('x', 200 * 1024));
        await socket.SendAsync(oversized, WebSocketMessageType.Text, endOfMessage: true, token);

        var reply = await socket.ReceiveAsync(buffer, token);

        Assert.Equal(WebSocketMessageType.Close, reply.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
    }

    private async Task<RemotePhoneClient> ConnectUnauthorizedAsync(CancellationToken token)
    {
        var client = new RemotePhoneClient(_phone.DeviceId, _phone.SharedSecret);
        await client.ConnectAsync(_uri, token);
        Assert.True((await client.AuthenticateAsync(_identity.PcInstanceId, token)).Success);
        return client;
    }

    private async Task<RemotePhoneClient> ConnectAsync(CancellationToken token)
    {
        var client = await ConnectUnauthorizedAsync(token);
        await client.AuthorizeAsync(_ownerKey, _identity.PcInstanceId, _phone.DeviceId, token);
        return client;
    }

    private static async Task<object?> Ok(RemotePhoneClient client, string action, object? parameters, CancellationToken token)
    {
        var response = await client.SendRequestAsync(action, parameters, token);
        Assert.True(response.Success, $"{action} failed: {response.Error?.Code} {response.Error?.Message}");
        return response.Result;
    }

    private static JsonElement Read(object? result) => JsonSerializer.SerializeToElement(result, ProtocolCodec.Options);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class InMemoryClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public string? ReadText() => Text;

        public void WriteText(string text) => Text = text;
    }
}
