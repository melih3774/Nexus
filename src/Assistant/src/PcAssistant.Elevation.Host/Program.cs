using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using PcAssistant.Elevation;
using PcAssistant.Elevation.Host;

// One executable, four modes. Keeping them together is what guarantees the service, the
// runner and the installer are always the same build and therefore the same protocol.
//
//   --service            the LocalSystem Windows service (started by the SCM)
//   --runner <pipe>      one elevated command in the owner's session, started by the service
//   --install ...        the one-time, UAC-confirmed setup step
//   --uninstall          the equally explicit removal step
//   --status             read-only diagnostics

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "--service";

        return verb switch
        {
            "--runner" when args.Length > 1 => RunnerHost.Run(args[1]),
            "--install" => HelperInstaller.Install(
                Value(args, "--source") ?? AppContext.BaseDirectory,
                Value(args, "--client") ?? string.Empty,
                Value(args, "--owner-sid") ?? string.Empty,
                Value(args, "--report")),
            "--uninstall" => HelperInstaller.Uninstall(Value(args, "--report")),
            "--status" => Status(),
            _ => RunService(),
        };
    }

    private static int RunService()
    {
        ServiceBase.Run(new ElevationService());
        return 0;
    }

    private static int Status()
    {
        var state = HelperPaths.ReadState();
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            installed = HelperPaths.IsInstalled,
            protocol = PrivilegedProtocol.Version,
            installRoot = HelperPaths.InstallRoot,
            owner = state?.OwnerAccount,
            client = state?.ClientExecutable,
            installedVersion = state?.InstalledVersion,
        }, new JsonSerializerOptions(PrivilegedProtocol.Json) { WriteIndented = true }));
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

/// <summary>
/// The service itself is intentionally thin: it establishes who the owner is, opens the
/// ACL-restricted local pipe, and hands each connection to the request handler. All the
/// deciding happens there.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevationService : ServiceBase
{
    private PrivilegedPipeServer? _server;

    public ElevationService() => ServiceName = PrivilegedProtocol.ServiceName;

    protected override void OnStart(string[] args)
    {
        var state = HelperPaths.ReadState();
        if (state is null || string.IsNullOrWhiteSpace(state.OwnerSid))
        {
            Log("no helper state recorded; refusing to serve any request");
            AuditLog.Write("ServiceStart", null, null, "-", "Failed", "no owner recorded");
            throw new InvalidOperationException(
                "The privileged helper has no recorded owner. Reinstall it from PC Assistant.");
        }

        var handler = new RequestHandler(state, Log);
        _server = new PrivilegedPipeServer(new System.Security.Principal.SecurityIdentifier(state.OwnerSid),
            handler.Handle, Log);
        _server.Start();

        AuditLog.Write("ServiceStart", null, null, "-", "Ok", "owner=" + state.OwnerAccount);
        Log("listening for " + state.OwnerAccount);
    }

    protected override void OnStop()
    {
        _server?.Dispose();
        _server = null;
        AuditLog.Write("ServiceStop", null, null, "-", "Ok");
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(HelperPaths.InstallRoot, "helper-service.log");
            var file = new FileInfo(path);
            if (file.Exists && file.Length > 1024 * 1024) File.Delete(path);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch (Exception) { /* diagnostics must never take the service down */ }
    }
}
