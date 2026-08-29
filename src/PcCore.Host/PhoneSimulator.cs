using System.Buffers.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Transport.Remote;

namespace PcCore.Host;

/// <summary>The simulator's own copy of the phone credential, kept separate from the PC's record.</summary>
public sealed record SimulatorCredentialRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string PcInstanceId { get; init; }
    public required string DeviceId { get; init; }

    /// <summary>Base64 of the DPAPI-protected shared secret — the phone's copy, never plaintext.</summary>
    public required string ProtectedSecret { get; init; }
}

/// <summary>
/// A development stand-in for the future Android application, at the protocol level only.
/// <para>
/// It exists so the remote transport can be exercised end to end before any Kotlin exists. It has
/// no UI, no notification handling, and no product behaviour — it pairs once, stores its credential
/// the way the Android app will use Keystore, and then runs the full remote conversation.
/// </para>
/// <para>
/// Its credential lives in <c>dev-phone-simulator.json</c> beside the real state, protected with
/// the same DPAPI mechanism and clearly named as development-only. It is deliberately a
/// <em>separate</em> file from <c>primary-phone.json</c>: this is the phone's half of the pairing,
/// and conflating the two halves in one file would blur exactly the boundary the design depends on.
/// </para>
/// </summary>
public static class PhoneSimulator
{
    public const string FileName = "dev-phone-simulator.json";

    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PcCore.PhoneSimulator");
        var directory = StateDirectory.ForCurrentUser();
        var protector = new PcCore.Windows.WindowsSecretProtector(
            loggerFactory.CreateLogger<PcCore.Windows.WindowsSecretProtector>());
        var store = new AtomicJsonFileStore<SimulatorCredentialRecord>(directory, FileName, logger);

        var loaded = store.Load();

        if (!loaded.IsLoaded)
        {
            Console.WriteLine();
            Console.WriteLine("  No simulator credential yet.");
            Console.WriteLine("  Pair one first: start PC Core, open http://127.0.0.1:47811/, click");
            Console.WriteLine("  'pairing.begin', then run:");
            Console.WriteLine();
            Console.WriteLine("      PcCore.Host.exe --simulate-phone-pair <NEXUS1....>");
            Console.WriteLine();
            return 2;
        }

        var secretBytes = protector.Unprotect(Convert.FromBase64String(loaded.Value!.ProtectedSecret));
        if (secretBytes is null)
        {
            Console.WriteLine("  Simulator credential could not be unprotected. Re-pair it.");
            return 2;
        }

        var url = args.SkipWhile(a => a != "--simulate-phone").Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("  Usage: PcCore.Host.exe --simulate-phone ws://<tailscale-ip>:47812/nexus");
            return 2;
        }

        return await RunFlowAsync(
            new Uri(url),
            loaded.Value.PcInstanceId,
            loaded.Value.DeviceId,
            Base64Url.EncodeToString(secretBytes)).ConfigureAwait(false);
    }

    /// <summary>Completes pairing from a payload the owner copied out of the local test page.</summary>
    public static async Task<int> PairAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var payload = args.SkipWhile(a => a != "--simulate-phone-pair").Skip(1).FirstOrDefault();

        if (!PairingPayload.TryDecode(payload, out _, out var pcInstanceId, out var sessionId, out var pairingSecret))
        {
            Console.WriteLine("  Usage: PcCore.Host.exe --simulate-phone-pair NEXUS1....");
            return 2;
        }

        var token = ReadSessionToken();
        if (token is null)
        {
            Console.WriteLine("  Could not read the local session token. Is PC Core running?");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47811") };
        http.DefaultRequestHeaders.Add(ProtocolConstants.TokenHeader, token);

        var response = await http.PostAsJsonAsync(
            "/device/pair",
            new { session_id = sessionId, pairing_secret = pairingSecret, display_name = "Dev Simulator" },
            ProtocolCodec.Options).ConfigureAwait(false);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        if (!json.RootElement.GetProperty("success").GetBoolean())
        {
            Console.WriteLine($"  Pairing failed: {json.RootElement.GetProperty("error").GetProperty("code").GetString()}");
            return 1;
        }

        var result = json.RootElement.GetProperty("result");
        var deviceId = result.GetProperty("device_id").GetString()!;
        var sharedSecret = result.GetProperty("shared_secret").GetString()!;

        var logger = loggerFactory.CreateLogger("PcCore.PhoneSimulator");
        var protector = new PcCore.Windows.WindowsSecretProtector(
            loggerFactory.CreateLogger<PcCore.Windows.WindowsSecretProtector>());

        new AtomicJsonFileStore<SimulatorCredentialRecord>(StateDirectory.ForCurrentUser(), FileName, logger)
            .Save(new SimulatorCredentialRecord
            {
                PcInstanceId = result.GetProperty("pc_instance_id").GetString()!,
                DeviceId = deviceId,
                ProtectedSecret = Convert.ToBase64String(
                    protector.Protect(Base64Url.DecodeFromChars(sharedSecret.AsSpan()))),
            });

        // Device id only. The secret is stored protected and never printed.
        Console.WriteLine($"  Simulator paired as device {deviceId}. Credential stored (protected).");
        return 0;
    }

    /// <summary>The twelve-step conversation the future Android client will have.</summary>
    private static async Task<int> RunFlowAsync(Uri uri, string pcInstanceId, string deviceId, string sharedSecret)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = cts.Token;
        var failures = 0;

        void Step(int n, string what, bool ok, string? detail = null)
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {n,2}. {what}{(detail is null ? "" : $"  [{detail}]")}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Connecting to {uri} as device {deviceId}");
        Console.WriteLine();

        await using (var phone = new RemotePhoneClient(deviceId, sharedSecret))
        {
            try
            {
                await phone.ConnectAsync(uri, token).ConfigureAwait(false);
                Step(1, "connect over the remote transport", true);
            }
            catch (Exception ex)
            {
                Step(1, "connect over the remote transport", false, ex.GetType().Name);
                Console.WriteLine();
                Console.WriteLine($"  Could not reach {uri}. Is PC Core running with a Tailscale address?");
                return 1;
            }

            var auth = await phone.AuthenticateAsync(pcInstanceId, token).ConfigureAwait(false);
            Step(2, "receive challenge and authenticate", auth.Success, auth.Error?.Code);

            if (!auth.Success)
            {
                return 1;
            }

            var identity = JsonSerializer.SerializeToElement(auth.Result, ProtocolCodec.Options);
            Step(
                3,
                "caller is ActorType.DEVICE",
                identity.GetProperty("actor_type").GetString() == "DEVICE",
                identity.GetProperty("device_id").GetString());

            var info = await phone.SendRequestAsync("system.get_info", null, token).ConfigureAwait(false);
            Step(4, "system.get_info", info.Success, info.Error?.Code);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var files = await phone.SendRequestAsync("files.list", new { path = home }, token).ConfigureAwait(false);
            Step(5, "files.list on an allowed root", files.Success, files.Error?.Code);

            var shot = await phone.SendRequestAsync("screen.capture", new { max_width = 320 }, token).ConfigureAwait(false);
            Step(6, "screen.capture", shot.Success, shot.Error?.Code);

            var state = await phone.SendRequestAsync("security.get_state", null, token).ConfigureAwait(false);
            Step(7, "security.get_state", state.Success, state.Error?.Code);

            var armed = await phone.SendRequestAsync("security.arm", null, token).ConfigureAwait(false);
            Step(8, "security.arm", armed.Success, armed.Error?.Code);

            var evt = await phone.WaitForEventAsync(SecurityEvents.StateChanged, TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);
            Step(9, "receive security.state_changed", evt is not null);

            var disarmed = await phone.SendRequestAsync("security.disarm", null, token).ConfigureAwait(false);
            Step(10, "security.disarm", disarmed.Success, disarmed.Error?.Code);

            var forbidden = await phone.SendRequestAsync("device.unpair_primary", null, token).ConfigureAwait(false);
            Step(
                11,
                "device.unpair_primary is DENIED",
                !forbidden.Success && forbidden.Error?.Code == ErrorCodes.NotAuthorized,
                forbidden.Error?.Code);

            await phone.CloseAsync(token).ConfigureAwait(false);
            Step(12, "disconnect cleanly", true);
        }

        // Reconnect: a new connection must authenticate from scratch with a fresh challenge.
        await using (var again = new RemotePhoneClient(deviceId, sharedSecret))
        {
            await again.ConnectAsync(uri, token).ConfigureAwait(false);
            var reauth = await again.AuthenticateAsync(pcInstanceId, token).ConfigureAwait(false);
            Step(13, "reconnect and authenticate again", reauth.Success, reauth.Error?.Code);
            await again.CloseAsync(token).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "  All simulator steps passed." : $"  {failures} step(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static string? ReadSessionToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcCore",
                "session.token");

            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
