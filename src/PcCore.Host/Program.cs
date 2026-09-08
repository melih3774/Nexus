using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Files;
using PcCore.Capabilities.Indicator;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Capabilities.Storage;
using PcCore.Capabilities.System;
using PcCore.Transport.Loopback;
using PcCore.Transport.Remote;
using PcCore.Windows;
using PcCore.Windows.Input;

[assembly: SupportedOSPlatform("windows10.0.17763.0")]

namespace PcCore.Host;

/// <summary>
/// PC Core v0.1 host. A plain, user-level console process: no elevation is requested, no service
/// is installed, and nothing is exposed beyond the loopback interface.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Startup registration is management, not serving: handle it and exit without opening a
        // socket, so `--install-startup` never leaves a listener behind.
        if (TryHandleStartupCommand(args, out var startupExitCode))
        {
            return startupExitCode;
        }

        // Native input setup, for the same reason. This is the one-time, owner-driven part of the
        // input story; normal use never touches it.
        if (TryHandleNativeInputCommand(args, out var nativeExitCode))
        {
            return nativeExitCode;
        }

        // Exercises the security workflow against this machine's real hooks, camera and microphone.
        if (args.Contains("--selftest-security"))
        {
            using var securityLogs = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true));
            return await SecuritySelfTest.RunAsync(args, securityLogs).ConfigureAwait(false);
        }

        // Exercises the native input path against this machine's real windows, keyboard and touch.
        if (args.Contains("--selftest-native-input"))
        {
            DpiAwareness.TryEnablePerMonitorV2();
            using var nativeLogs = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true));
            return await NativeInputSelfTest.RunAsync(args, nativeLogs).ConfigureAwait(false);
        }

        // A local diagnostic for the three v0.6 pieces that only exist against real Windows.
        if (args.Contains("--selftest-remote-control"))
        {
            using var selfTestLogs = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true));
            return await RemoteControlSelfTest.RunAsync(args, selfTestLogs).ConfigureAwait(false);
        }

        // The development phone simulator is a client, not a server: it never opens a listener.
        if (args.Contains("--simulate-phone")
            || args.Contains("--simulate-phone-pair")
            || args.Contains("--simulate-phone-enroll"))
        {
            using var simulatorLogs = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true));

            if (args.Contains("--simulate-phone-enroll"))
            {
                return await PhoneSimulator.EnrollAsync(args, simulatorLogs).ConfigureAwait(false);
            }

            return args.Contains("--simulate-phone-pair")
                ? await PhoneSimulator.PairAsync(args, simulatorLogs).ConfigureAwait(false)
                : await PhoneSimulator.RunAsync(args, simulatorLogs).ConfigureAwait(false);
        }

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
        var pairingOptions = Bind<PairingOptions>(builder.Configuration, PairingOptions.SectionName);
        var autoStartOptions = Bind<AutoStartOptions>(builder.Configuration, AutoStartOptions.SectionName);
        var remoteOptions = Bind<RemoteTransportOptions>(builder.Configuration, RemoteTransportOptions.SectionName);
        var streamOptions = Bind<StreamOptions>(builder.Configuration, StreamOptions.SectionName);
        var inputOptions = Bind<InputOptions>(builder.Configuration, InputOptions.SectionName);
        var detectionOptions = Bind<DetectionOptions>(builder.Configuration, DetectionOptions.SectionName);
        var evidenceOptions = Bind<EvidenceOptions>(builder.Configuration, EvidenceOptions.SectionName);
        var attributionOptions = Bind<AttributionOptions>(builder.Configuration, AttributionOptions.SectionName);
        var securityStorage = Bind<SecurityStorageOptions>(builder.Configuration, SecurityStorageOptions.SectionName);

        var token = SessionToken.Generate();

        builder.ConfigureLoopbackKestrel(transportOptions);
        builder.Services.AddLoopbackTransport(transportOptions, token);

        RegisterCapabilities(
            builder.Services,
            filesOptions,
            screenOptions,
            pairingOptions,
            streamOptions,
            inputOptions,
            detectionOptions,
            evidenceOptions,
            attributionOptions,
            securityStorage);
        RegisterRemoteTransport(builder.Services, remoteOptions);

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
        app.MapDeviceSurface();

        // Touch the identity so it is generated on first run rather than on first pairing.
        var pcInstanceId = app.Services.GetRequiredService<IPcIdentityProvider>().PcInstanceId;
        var primaryPhone = app.Services.GetRequiredService<IPrimaryPhoneRegistry>().Current;

        logger.LogInformation(
            "PC instance {PcInstanceId}; primary phone: {Phone}",
            pcInstanceId,
            primaryPhone is null ? "not paired" : $"{primaryPhone.DeviceId} ({primaryPhone.DisplayName})");

        logger.LogInformation("Security state: {State}", app.Services.GetRequiredService<ISecurityStateService>().Current.State);

        WireRemoteControl(app.Services, logger);
        ReportNativeInput(app.Services, logger);
        StartSecurityWorkflow(app.Services, logger);
        logger.LogInformation("Remote transport port: {Port} (Tailscale only)", remoteOptions.Port);

        ApplyAutoStart(app.Services, autoStartOptions, logger);

        LogRemoteTransportIntent(remoteOptions, app.Services, logger);

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
        ScreenOptions screenOptions,
        PairingOptions pairingOptions,
        StreamOptions streamOptions,
        InputOptions inputOptions,
        DetectionOptions detectionOptions,
        EvidenceOptions evidenceOptions,
        AttributionOptions attributionOptions,
        SecurityStorageOptions securityStorage)
    {
        // --- Platform implementations (the only Windows-specific registrations) ---------------
        services.AddSingleton<IRealPathResolver, Win32RealPathResolver>();
        services.AddSingleton<ISecretProtector, WindowsSecretProtector>();
        services.AddSingleton<WindowsAutoStart>();
        services.AddSingleton<IKnownFolderResolver, WindowsKnownFolderResolver>();
        services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
        services.AddSingleton<IProcessLister, WindowsProcessLister>();
        services.AddSingleton<IDisplayGeometry, SystemDisplayGeometry>();
        services.AddSingleton<IScreenCapture, GdiScreenCapture>();

        // v0.6 platform pieces. Live capture, input injection and the on-screen indicator are the
        // only three things in this milestone that touch Windows directly, and all three sit behind
        // an interface the capability layer declares.
        services.AddSingleton<IDesktopStream, DxgiDesktopStream>();
        services.AddSingleton<IDisplayCatalog>(p => p.GetRequiredService<IDesktopStream>());
        services.AddSingleton<IFrameEncoder, JpegFrameEncoder>();
        // The input path. WindowsInputController is PC Core injecting for itself; BrokerInputChannel
        // is the pipe to the elevated Nexus Input Broker; NativeInputRouter picks between them on
        // every event. Only the router is registered as IInputController, so nothing above this line
        // can accidentally bypass the choice — and nothing has to be told which path is live.
        services.AddSingleton<WindowsInputController>();
        services.AddSingleton<BrokerInputChannel>();
        services.AddSingleton<NativeInputInstaller>();
        services.AddSingleton<NativeInputRouter>();
        services.AddSingleton<IInputController>(p => p.GetRequiredService<NativeInputRouter>());
        services.AddSingleton<IRemoteIndicator, WindowsRemoteIndicator>();

        // v0.7 platform pieces. Detection reads two fields of an input event and never the key; the
        // media sources go through the standard Windows APIs so the OS privacy controls and the
        // camera indicator light all still apply.
        services.AddSingleton<ISecurityActivityDetector, WindowsActivityDetector>();
        services.AddSingleton<IScreenEvidenceSource, WindowsScreenEvidence>();
        services.AddSingleton<ICameraEvidenceSource, WindowsCameraEvidence>();
        services.AddSingleton<IAudioEvidenceSource, WindowsAudioEvidence>();
        services.AddSingleton<IOwnerVerifier, WindowsHelloVerifier>();

        // --- Persistent state (all under %LOCALAPPDATA%\Nexus, never in the repository) --------
        services.AddSingleton(StateDirectory.ForCurrentUser());
        services.AddSingleton<IPcIdentityProvider, PcIdentityProvider>();
        services.AddSingleton<IPrimaryPhoneRegistry, PrimaryPhoneRegistry>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();

        // Remote pairing enrollment: not a capability, and deliberately not registered as one. The
        // remote transport is its only caller (see RemoteConnection), which is what confines it to
        // the Tailscale-bound listener.
        services.AddSingleton<IRemoteEnrollmentService, RemoteEnrollmentService>();

        // --- Security boundary ----------------------------------------------------------------
        services.AddSingleton(screenOptions);
        services.AddSingleton(pairingOptions);
        services.AddSingleton(provider => AllowedRoots.Create(
            filesOptions.AllowedRoots,
            provider.GetRequiredService<IRealPathResolver>(),
            provider.GetRequiredService<IKnownFolderResolver>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<AllowedRoots>()));
        services.AddSingleton<PathAuthorizer>();

        // The single authoritative owner-presence state. Singleton because "authoritative" and
        // "one per request" are incompatible.
        services.AddSingleton<ISecurityStateService, SecurityStateService>();

        // --- Live screen and remote input (v0.6) ------------------------------------------------
        services.AddSingleton(streamOptions);
        services.AddSingleton(inputOptions);

        // Attribution sits between input and security and belongs to neither: input declares what it
        // injected, detection asks whether an observation was already accounted for. Registering it
        // here, once, is what makes both sides talk about the same ledger.
        services.AddSingleton(attributionOptions);
        services.AddSingleton<NexusInputAttribution>();
        services.AddSingleton<INexusInputAttribution>(p => p.GetRequiredService<NexusInputAttribution>());
        services.AddSingleton<DesktopStreamService>();
        services.AddSingleton<IInputService, InputService>();
        services.AddSingleton<RemoteIndicatorService>();

        // --- The security workflow (v0.7) -------------------------------------------------------
        services.AddSingleton(detectionOptions);
        services.AddSingleton(evidenceOptions);
        services.AddSingleton(securityStorage);
        services.AddSingleton<SecurityConfigurationStore>();
        services.AddSingleton<ISecuritySessionStore, SecuritySessionStore>();
        services.AddSingleton<SecurityActivityMonitor>();
        services.AddSingleton<SecurityIncidentService>();
        services.AddSingleton<SecurityStatusReader>();

        // --- Protocol plumbing ------------------------------------------------------------------
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventPublisher>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<IEventStream>(p => p.GetRequiredService<EventBus>());
        services.AddSingleton<ICapabilityRegistry>(p => new CapabilityRegistry(p.GetServices<ICapabilityAction>()));
        // The first real caller distinction: local keeps everything, the Primary Phone gets an
        // explicit allowlist. Evaluated inside the dispatcher, so both transports share it.
        services.AddSingleton<ICapabilityPolicy, DefaultCapabilityPolicy>();
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
        services.AddSingleton<ICapabilityAction, GetPrimaryDeviceAction>();
        services.AddSingleton<ICapabilityAction, BeginPairingAction>();
        services.AddSingleton<ICapabilityAction, CancelPairingAction>();
        services.AddSingleton<ICapabilityAction, UnpairPrimaryDeviceAction>();

        // --- Capabilities added by v0.6 -----------------------------------------------------------
        services.AddSingleton<ICapabilityAction, ListDisplaysAction>();
        services.AddSingleton<ICapabilityAction, StartStreamAction>();
        services.AddSingleton<ICapabilityAction, StopStreamAction>();

        services.AddSingleton<ICapabilityAction, BeginInputAction>();
        services.AddSingleton<ICapabilityAction, EndInputAction>();
        services.AddSingleton<ICapabilityAction, PointerAction>();
        services.AddSingleton<ICapabilityAction, ButtonInputAction>();
        services.AddSingleton<ICapabilityAction, ScrollAction>();
        services.AddSingleton<ICapabilityAction, KeyInputAction>();
        services.AddSingleton<ICapabilityAction, TextInputAction>();
        services.AddSingleton<ICapabilityAction, ReleaseAllInputAction>();

        // --- Capabilities added by v0.8 -----------------------------------------------------------
        services.AddSingleton<ICapabilityAction, TouchInputAction>();
        services.AddSingleton<ICapabilityAction, GetInputStatusAction>();

        // Local-only, and deliberately absent from the DEVICE allowlist: how visible the PC makes
        // remote access is a decision taken at the PC.
        services.AddSingleton<ICapabilityAction, GetIndicatorAction>();
        services.AddSingleton<ICapabilityAction, SetIndicatorAction>();

        // --- Capabilities added by v0.7 -----------------------------------------------------------
        services.AddSingleton<ICapabilityAction, ListSecuritySessionsAction>();
        services.AddSingleton<ICapabilityAction, GetSecuritySessionAction>();
        services.AddSingleton<ICapabilityAction, GetSecurityEvidenceAction>();
        services.AddSingleton<ICapabilityAction, ThisWasMeAction>();
        services.AddSingleton<ICapabilityAction, NotMeAction>();

        // Local-only, and deliberately absent from the DEVICE allowlist: deleting evidence and
        // weakening protection are decisions taken at the PC, behind Windows Hello.
        services.AddSingleton<ICapabilityAction, DeleteSecuritySessionAction>();
        services.AddSingleton<ICapabilityAction, GetSecurityConfigAction>();
        services.AddSingleton<ICapabilityAction, SetSecurityConfigAction>();
    }

    /// <summary>
    /// Starts security detection and reconciles whatever the last run left behind.
    /// <para>
    /// Detection runs whatever the state is; what it <em>does</em> depends entirely on the state.
    /// In OWNER_PRESENT the monitor discards everything it sees, so no evidence path is reachable —
    /// that is what makes "nothing is recorded while the owner is present" a property of the code
    /// rather than a promise.
    /// </para>
    /// </summary>
    private static void StartSecurityWorkflow(IServiceProvider services, ILogger logger)
    {
        var incidents = services.GetRequiredService<SecurityIncidentService>();
        var monitor = services.GetRequiredService<SecurityActivityMonitor>();
        var configuration = services.GetRequiredService<SecurityConfigurationStore>().Current;

        // Recovery first: a restart during an incident continues that incident rather than opening
        // a second one for the same event.
        incidents.Recover();
        monitor.Start();

        logger.LogInformation(
            "Security workflow: detection {Detection}, screen {Screen}, camera {Camera}, microphone {Audio}, "
            + "retention {Days} days.",
            configuration.DetectionEnabled ? "on" : "off",
            configuration.Screen,
            configuration.Camera,
            configuration.Audio,
            configuration.RetentionDays);

        var active = incidents.Active;

        if (active is not null)
        {
            logger.LogWarning(
                "A security session is active: {SessionId}, started {Started:u}, {Resolution}.",
                active.SessionId,
                active.StartedAtUtc,
                active.Resolution);
        }
    }

    /// <summary>
    /// Brings up the indicator and reports what live control can do on this machine.
    /// <para>
    /// The indicator follows input sessions rather than connections: being connected is not being in
    /// control, and the badge must mean the second (SPEC.md 60).
    /// </para>
    /// </summary>
    private static void WireRemoteControl(IServiceProvider services, ILogger logger)
    {
        // Resolved eagerly: constructing it is what subscribes it to input activity, and an
        // indicator that only appears once something else happens to need it would be no indicator.
        var indicator = services.GetRequiredService<RemoteIndicatorService>();

        logger.LogInformation("Remote-control indicator: {Mode}", indicator.Mode);

        var displays = services.GetRequiredService<IDisplayCatalog>().GetDisplays();

        if (displays.Count == 0)
        {
            logger.LogWarning("No display could be enumerated for capture; live streaming will be unavailable.");
        }
        else
        {
            logger.LogInformation(
                "Displays available for live streaming: {Displays}",
                string.Join(", ", displays.Select(d => d.ToString())));
        }
    }

    /// <summary>
    /// The authenticated remote transport. It resolves the dispatcher from the same container the
    /// loopback transport uses, so remote requests run the identical capabilities under the
    /// identical policy — nothing is duplicated for remote callers.
    /// </summary>
    private static void RegisterRemoteTransport(IServiceCollection services, RemoteTransportOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<INetworkInterfaceProvider, SystemNetworkInterfaceProvider>();
        services.AddSingleton<TailscaleAddressDetector>();
        services.AddSingleton<RemoteSessionRegistry>();
        services.AddSingleton<RemoteTransport>();
        services.AddSingleton<RemoteTransportService>();
        services.AddHostedService(p => p.GetRequiredService<RemoteTransportService>());

        // Pairing codes describe where the phone should connect back to. The answer comes from the
        // transport's live bound endpoint, so a code can never advertise an address that is not
        // actually being served.
        services.AddSingleton<IDeviceEndpointProvider, RemoteTransportEndpointProvider>();

        // Binds a stream request to the connection that will carry its frames, and reports what is
        // going on to the indicator.
        services.AddSingleton<RemoteStreamCoordinator>();
        services.AddSingleton<IStreamCoordinator>(p => p.GetRequiredService<RemoteStreamCoordinator>());
        services.AddSingleton<IIndicatorActivityProbe>(p => p.GetRequiredService<RemoteStreamCoordinator>());
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

    /// <summary>
    /// Reports what the remote transport will attempt, before the hosted service starts. The
    /// address itself is logged by the transport once it actually binds.
    /// </summary>
    private static void LogRemoteTransportIntent(
        RemoteTransportOptions options,
        IServiceProvider services,
        ILogger logger)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Remote transport: disabled by configuration.");
            return;
        }

        var detection = services.GetRequiredService<TailscaleAddressDetector>().Detect(options.Port);

        if (detection.Found)
        {
            logger.LogInformation(
                "Remote transport: Tailscale address {Address} found; listening on port {Port}.",
                detection.Endpoint!.Address,
                options.Port);
        }
        else
        {
            // Not an error: Tailscale is the owner's to run. The hosted service keeps checking.
            logger.LogWarning(
                "Remote transport: unavailable ({Diagnostic}). Re-checking every {Seconds}s; "
                + "no remote listener will be opened until a Tailscale address exists.",
                detection.Diagnostic,
                Math.Max(5, options.AvailabilityCheckSeconds));
        }
    }

    /// <summary>
    /// Applies the auto-start preference. The config flag is the only thing that governs this:
    /// PC Core never re-registers itself against the owner's wishes and never reinstates an entry
    /// the owner has removed while the flag is off.
    /// </summary>
    private static void ApplyAutoStart(IServiceProvider services, AutoStartOptions options, ILogger logger)
    {
        var autoStart = services.GetRequiredService<WindowsAutoStart>();

        if (!options.Enabled)
        {
            var status = autoStart.GetStatus();
            logger.LogInformation(
                "Auto-start is disabled by configuration (PcCore:Startup:Enabled=false); "
                + "currently {State}.",
                status.Registered ? "still registered from an earlier run" : "not registered");
            return;
        }

        autoStart.Register();
    }

    /// <summary>
    /// Says which input path is live and what it can reach, once, at start-up.
    /// <para>
    /// Deliberately unmissable in the log, because this is the one fact about input that changes
    /// what the owner can do from their phone — and the one they would otherwise only discover by
    /// clicking an Administrator window and watching nothing happen.
    /// </para>
    /// </summary>
    private static void ReportNativeInput(IServiceProvider services, ILogger logger)
    {
        services.GetRequiredService<NexusInputAttribution>().LogConfiguration();

        var status = services.GetRequiredService<IInputController>().Status;

        if (status.State == NativeInputState.Ready)
        {
            logger.LogInformation(
                "Native input: READY via the Nexus Input Broker {Version}. Elevated application "
                + "windows are reachable; touch injection {Touch}.",
                status.BrokerVersion,
                status.TouchAvailable ? "available" : "unavailable");
        }
        else
        {
            logger.LogWarning(
                "Native input: {State}. {Diagnostic}",
                status.State.ToString().ToUpperInvariant(),
                status.Diagnostic);
        }

        if (status.PhysicalDigitizer)
        {
            // Worth saying out loud: on a machine with a real digitizer, a person touching the
            // screen produces the same kind of promoted mouse messages Nexus's own touch does, and
            // attribution then rests on the correlation ledger rather than on the source alone.
            logger.LogInformation(
                "This machine has a physical pen or touch digitizer; local touch is attributed by "
                + "correlation rather than by signature.");
        }
    }

    /// <summary>
    /// Handles <c>--install-native-input</c>, <c>--uninstall-native-input</c> and
    /// <c>--native-input-status</c>.
    /// <para>
    /// The whole owner-facing surface of native input, and it is three commands run once. Everything
    /// after that is automatic: the broker starts at logon, PC Core finds it, and nothing has to be
    /// enabled per session.
    /// </para>
    /// </summary>
    private static bool TryHandleNativeInputCommand(string[] args, out int exitCode)
    {
        exitCode = 0;

        var command = args.FirstOrDefault(a =>
            a is "--install-native-input" or "--uninstall-native-input"
                or "--repair-native-input" or "--native-input-status");

        if (command is null)
        {
            return false;
        }

        using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true));
        var installer = new NativeInputInstaller(factory.CreateLogger<NativeInputInstaller>());

        switch (command)
        {
            case "--install-native-input":
            case "--repair-native-input":
                var installed = installer.Install();
                Describe(installed);

                if (!installed.UpToDate)
                {
                    exitCode = 1;

                    if (!NativeInputInstaller.IsElevated)
                    {
                        Console.WriteLine();
                        Console.WriteLine("  Run this from an elevated terminal:");
                        Console.WriteLine($"    \"{Environment.ProcessPath}\" {command}");
                    }
                }

                break;

            case "--uninstall-native-input":
                Console.WriteLine(installer.Uninstall()
                    ? "Native input removed. Nexus will inject input itself from now on."
                    : "Native input could not be removed; see the message above.");
                break;

            default:
                Describe(installer.GetStatus());
                break;
        }

        return true;

        static void Describe(NativeInputInstallation installation)
        {
            Console.WriteLine($"Task       : {NativeInputInstaller.TaskPath}");
            Console.WriteLine($"Registered : {installation.Registered}");
            Console.WriteLine($"Elevated   : {installation.Elevated}");
            Console.WriteLine($"Command    : {installation.RegisteredCommand ?? "(none)"}");
            Console.WriteLine($"Expected   : {installation.ExpectedCommand}");
            Console.WriteLine($"Up to date : {installation.UpToDate}");

            if (installation.Diagnostic is { } diagnostic)
            {
                Console.WriteLine($"Note       : {diagnostic}");
            }
        }
    }

    /// <summary>
    /// Handles <c>--install-startup</c>, <c>--uninstall-startup</c> and <c>--startup-status</c>.
    /// Returns true when the process should exit instead of serving.
    /// </summary>
    private static bool TryHandleStartupCommand(string[] args, out int exitCode)
    {
        exitCode = 0;

        var command = args.FirstOrDefault(a =>
            a is "--install-startup" or "--uninstall-startup" or "--startup-status");

        if (command is null)
        {
            return false;
        }

        using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(c => c.SingleLine = true));
        var autoStart = new WindowsAutoStart(factory.CreateLogger<WindowsAutoStart>());

        switch (command)
        {
            case "--install-startup":
                var registered = autoStart.Register();
                Console.WriteLine($"Auto-start registered: {registered.RegisteredCommand}");
                break;

            case "--uninstall-startup":
                Console.WriteLine(autoStart.Unregister()
                    ? "Auto-start entry removed."
                    : "No auto-start entry was present.");
                break;

            default:
                var status = autoStart.GetStatus();
                Console.WriteLine($"Registered : {status.Registered}");
                Console.WriteLine($"Command    : {status.RegisteredCommand ?? "(none)"}");
                Console.WriteLine($"Expected   : {status.ExpectedCommand}");
                Console.WriteLine($"Up to date : {status.UpToDate}");
                Console.WriteLine($@"Registry   : HKCU\{WindowsAutoStart.RunKeyPath}\{WindowsAutoStart.ValueName}");
                break;
        }

        return true;
    }
}
