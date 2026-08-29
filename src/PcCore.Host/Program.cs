using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Files;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.System;
using PcCore.Transport.Loopback;
using PcCore.Windows;

[assembly: SupportedOSPlatform("windows")]

namespace PcCore.Host;

/// <summary>
/// PC Core v0.1 host. A plain, user-level console process: no elevation is requested, no service
/// is installed, and nothing is exposed beyond the loopback interface.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Must run before any GDI work. Without it Windows DPI-virtualises the process on a scaled
        // display and screenshots come back silently downscaled rather than failing outright.
        var dpiAware = DpiAwareness.TryEnablePerMonitorV2();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "PCCORE_")
            .AddCommandLine(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });

        var transportOptions = Bind<LoopbackTransportOptions>(builder.Configuration, LoopbackTransportOptions.SectionName);
        var filesOptions = Bind<FilesOptions>(builder.Configuration, FilesOptions.SectionName);
        var screenOptions = Bind<ScreenOptions>(builder.Configuration, ScreenOptions.SectionName);

        var token = SessionToken.Generate();

        builder.ConfigureLoopbackKestrel(transportOptions);
        builder.Services.AddLoopbackTransport(transportOptions, token);

        RegisterCapabilities(builder.Services, filesOptions, screenOptions);

        var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PcCore.Host");
        var info = app.Services.GetRequiredService<ISystemInfoProvider>().GetInfo();

        logger.LogInformation(
            "PC Core {Version} starting on {Machine} ({Os})",
            info.PcCoreVersion,
            info.MachineName,
            info.OsDescription);

        if (info.IsElevated)
        {
            // v0.1 is designed to need no elevation. Running elevated widens the blast radius of
            // every capability, so say so loudly rather than silently accepting it.
            logger.LogWarning(
                "PC Core is running ELEVATED. v0.1 does not need administrator rights; "
                + "run it as a standard user.");
        }

        if (!dpiAware)
        {
            logger.LogWarning("Per-monitor DPI awareness could not be enabled; screenshots may be scaled.");
        }

        var registry = app.Services.GetRequiredService<ICapabilityRegistry>();
        logger.LogInformation(
            "Capabilities: {Actions}",
            string.Join(", ", registry.Actions.Select(a => a.Name)));

        var authorizer = app.Services.GetRequiredService<PathAuthorizer>();
        if (authorizer.Roots.IsEmpty)
        {
            logger.LogWarning("No filesystem roots are configured; every files.* request will be denied.");
        }

        token.WriteToDisk(logger);

        var testPage = LoadTestPage(app.Environment.ContentRootPath, token, authorizer.Roots.Roots, logger);
        app.MapLoopbackTransport(testPage);

        var endpoint = LoopbackTransport.DescribeEndpoint(transportOptions);
        logger.LogInformation("Listening on {Endpoint} (loopback only)", endpoint);
        logger.LogInformation("Test page: {Endpoint}/  ·  RPC: POST {Endpoint}/rpc  ·  Events: {Endpoint}/ws", endpoint, endpoint, endpoint);

        // Printed rather than logged, so the token never enters a log sink or log file.
        if (transportOptions.RequireToken)
        {
            Console.WriteLine();
            Console.WriteLine($"  Session token: {token.Value}");
            Console.WriteLine($"  Send it as the '{PcCore.Protocol.ProtocolConstants.TokenHeader}' header.");
            Console.WriteLine();
        }
        else
        {
            logger.LogWarning("Token authentication is DISABLED. Any local process can call PC Core.");
        }

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The one place capabilities and their platform implementations meet. Adding a capability in
    /// a later milestone is a line here plus a class — no dispatcher or transport change.
    /// </summary>
    private static void RegisterCapabilities(
        IServiceCollection services,
        FilesOptions filesOptions,
        ScreenOptions screenOptions)
    {
        // --- Platform implementations (the only Windows-specific registrations) ---------------
        services.AddSingleton<IRealPathResolver, Win32RealPathResolver>();
        services.AddSingleton<IKnownFolderResolver, WindowsKnownFolderResolver>();
        services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
        services.AddSingleton<IProcessLister, WindowsProcessLister>();
        services.AddSingleton<IDisplayGeometry, SystemDisplayGeometry>();
        services.AddSingleton<IScreenCapture, GdiScreenCapture>();

        // --- Security boundary ----------------------------------------------------------------
        services.AddSingleton(screenOptions);
        services.AddSingleton(provider => AllowedRoots.Create(
            filesOptions.AllowedRoots,
            provider.GetRequiredService<IRealPathResolver>(),
            provider.GetRequiredService<IKnownFolderResolver>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<AllowedRoots>()));
        services.AddSingleton<PathAuthorizer>();

        // The single authoritative owner-presence state. Singleton because "authoritative" and
        // "one per request" are incompatible.
        services.AddSingleton<ISecurityStateService, SecurityStateService>();

        // --- Protocol plumbing ------------------------------------------------------------------
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        services.AddSingleton<ICapabilityDispatcher, CapabilityDispatcher>();

        // --- Capabilities exposed by v0.1 -------------------------------------------------------
        services.AddSingleton<ICapabilityAction, GetSystemInfoAction>();
        services.AddSingleton<ICapabilityAction, ListProcessesAction>();
        services.AddSingleton<ICapabilityAction, ListFilesAction>();
        services.AddSingleton<ICapabilityAction, StatFileAction>();
        services.AddSingleton<ICapabilityAction, CaptureScreenAction>();
        services.AddSingleton<ICapabilityAction, GetSecurityStateAction>();
        services.AddSingleton<ICapabilityAction, ArmSecurityAction>();
        services.AddSingleton<ICapabilityAction, DisarmSecurityAction>();
    }

    private static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var instance = new T();
        configuration.GetSection(section).Bind(instance);
        return instance;
    }

    /// <summary>
    /// Loads the developer test page and injects the session token so local debugging needs no
    /// copy-paste. See SPEC.md 7.3 for why serving the token to a same-origin page is safe here.
    /// </summary>
    private static string? LoadTestPage(
        string contentRoot,
        SessionToken token,
        IReadOnlyList<string> allowedRoots,
        ILogger logger)
    {
        var path = Path.Combine(contentRoot, "wwwroot", "index.html");

        if (!File.Exists(path))
        {
            logger.LogWarning("Test page not found at {Path}; '/' will return 404.", path);
            return null;
        }

        try
        {
            // The roots are the user's own configured directories, shown back to the user in
            // their own local page. Injecting them here avoids adding a capability whose only
            // purpose is to enumerate the boundary.
            var roots = global::System.Text.Json.JsonSerializer.Serialize(allowedRoots);

            return File.ReadAllText(path)
                .Replace("__PCCORE_TOKEN__", token.Value, StringComparison.Ordinal)
                .Replace("__PCCORE_ROOTS__", roots, StringComparison.Ordinal);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Test page could not be read; '/' will return 404.");
            return null;
        }
    }
}
