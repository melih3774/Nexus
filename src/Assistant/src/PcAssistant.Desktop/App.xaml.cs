using System.Windows;
using System.Windows.Threading;
using System.IO;
using PcAssistant.ClaudeCode;
using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Elevation;
using PcAssistant.Core.Model;
using PcAssistant.Core.NexusBridge;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;
using PcAssistant.Desktop.Mvvm;
using PcAssistant.Desktop.ViewModels;

namespace PcAssistant.Desktop;

/// <summary>
/// Composition root. The object graph is small enough that wiring it by hand is clearer
/// than a container.
///
///     MainWindow / MainViewModel   (UI)
///            ↓
///     AssistantService             (task engine)
///            ↓
///     ClaudeCodeBridge             (the only place that knows the Claude CLI)
///            ↓
///     claude.exe
/// </summary>
public partial class App : Application
{
    private AppPaths? _paths;
    private AppSettings? _settings;
    private TaskStore? _store;
    private ApprovalBroker? _approvals;
    private AssistantService? _service;
    private PrivilegedOperationService? _privileged;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The privileged helper identifies its caller by the image path recorded at install
        // time, so live verification of the elevation path has to originate here, from the
        // executable the helper was actually installed for. The probes are a closed set of
        // hard-coded harmless operations; nothing about them comes from the command line
        // except which of them to run. No window is shown.
        if (PrivilegedVerification.TryHandle(e.Args, out var verificationExitCode))
        {
            Shutdown(verificationExitCode);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _paths = new AppPaths();
        _settings = AppSettings.Load(_paths.SettingsFile);
        ThemeManager.Initialize(_settings);
        Activated += (_, _) => ThemeManager.RefreshSystemTheme();
        _store = new TaskStore(_paths.DatabaseFile);
        _approvals = new ApprovalBroker(TimeSpan.FromMinutes(Math.Max(1, _settings.ApprovalTimeoutMinutes)));

        var workspaces = new WorkspaceService(() => _settings.WorkspaceRoots.ToList());

        // The trusted privileged client. It is constructed unconditionally because it is
        // harmless without an installed helper: every call then reports "not installed"
        // rather than silently doing nothing.
        _privileged = new PrivilegedOperationService(_approvals, _store);

        // The bridge asks the service for a task's current mode so a mid-task switch
        // takes effect on the very next tool call.
        var bridge = new ClaudeCodeBridge(
            _paths, _settings, _approvals,
            taskId => _service?.Find(taskId)?.Mode ?? AssistantMode.Ask,
            privileged: _privileged);

        _service = new AssistantService(_store, bridge, workspaces, _paths, _settings, _approvals);

        _mainViewModel = new MainViewModel(_service, workspaces, _settings, _paths, new DispatcherUi(Dispatcher))
        {
            Privileged = _privileged,
            HelperPayloadDirectory = Path.Combine(AppContext.BaseDirectory, "Elevation"),
            HelperReportPath = Path.Combine(_paths.Runtime, "helper-setup.json"),
        };

        _mainViewModel.StartupStateProvider = WindowsStartup.Read;
        _mainViewModel.StartupStateSetter = WindowsStartup.Set;

        _mainViewModel.CapabilityDiagnosticsProvider = async cancellationToken =>
        {
            var claude = await _service.GetClaudeStatusAsync(cancellationToken).ConfigureAwait(false);
            var browser = BrowserIntegrationStatus.Inspect();
            var pcTools = Path.Combine(AppContext.BaseDirectory, "PcTools", "PcAssistant.PcTools.exe");
            var helper = await _privileged.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            // Measured, not assumed. Every line below the helper name comes from actually
            // capturing a window, reading a control tree, reading the clipboard and the audio
            // endpoint on this machine — the panel used to say "Ready when PC tools are ready",
            // which was a statement about the build rather than about the PC.
            var probe = await CapabilityProbeRunner.RunAsync(pcTools, cancellationToken).ConfigureAwait(false);

            return string.Join(Environment.NewLine,
                $"Claude Code: {claude.DisplayText} {claude.Version}".TrimEnd(),
                $"Chrome / Edge: {browser.Summary}",
                $"PC tools helper: {(File.Exists(pcTools) ? "Present" : "Missing — build the Desktop project")}",
                string.Empty,
                CapabilityProbeRunner.Describe(probe),
                string.Empty,
                $"PC Assistant itself: running {(elevated ? "ELEVATED — this is not the intended state" : "unelevated, as intended")}",
                $"Privileged operations: {helper.DisplayText}",
                "  " + helper.Detail,
                helper.OwnerAccount is null ? "" : $"  Owner: {helper.OwnerAccount}  ·  session {helper.SessionId}",
                // Stated here rather than only in the documentation, because this panel is where
                // an owner looks to find out whether something works, and "not installed" alone
                // does not say whether that is a gap or a deliberate pause.
                helper.OwnerAccount is null
                    ? "  Installing it needs one Windows approval, and that prompt lives on the Secure Desktop, so " +
                      "it has to be answered at this PC rather than over a remote session. Everything else works " +
                      "without it."
                    : "",
                string.Empty,
                browser.Detail);
        };

        // The Nexus bridge lives in this process on purpose: the same database, task engine and
        // approval broker the window uses are the ones Nexus reaches, so a phone-started task is an
        // ordinary task here and an approval has exactly one card however it is answered.
        _claudeStatus = new CachedClaudeStatus(ct => _service.GetClaudeStatusAsync(ct));

        // A turn that proves the login unusable changes the answer at once, not twenty seconds later.
        bridge.StatusChanged += (_, _) => _claudeStatus.Invalidate();
        _bridgeLog = new BridgeLog(Path.Combine(_paths.Runtime, "nexus-bridge.log"));
        _mainViewModel.NexusBridgeChanged = () => _ = ApplyNexusBridgeAsync();
        _mainViewModel.NexusCandidateResolved = () => _nexusBridge?.ClearCandidate();
        _ = ApplyNexusBridgeAsync();

        new MainWindow(_mainViewModel).Show();
    }

    private NexusBridgeServer? _nexusBridge;
    private CachedClaudeStatus? _claudeStatus;
    private BridgeLog? _bridgeLog;
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);

    /// <summary>
    /// Brings the listener in line with the setting. Restarting on every change is deliberate: turning
    /// the bridge off, or forgetting the trusted Nexus, must end a connection admitted before.
    /// </summary>
    private async Task ApplyNexusBridgeAsync()
    {
        if (_service is null || _settings is null || _mainViewModel is null) return;

        await _bridgeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_nexusBridge is not null)
            {
                await _nexusBridge.DisposeAsync().ConfigureAwait(false);
                _nexusBridge = null;
            }

            if (!_settings.NexusBridge.Enabled)
            {
                _mainViewModel.UpdateNexusBridge(new NexusBridgeStatus(NexusBridgeListenState.Stopped));
                return;
            }

            var server = new NexusBridgeServer(
                _service,
                () => _settings.NexusBridge.TrustedClientExecutable,
                ct => _claudeStatus!.GetAsync(ct),
                _bridgeLog!.Write);

            server.StatusChanged += (_, status) => _mainViewModel.UpdateNexusBridge(status);
            _nexusBridge = server;
            server.Start();
        }
        finally
        {
            _bridgeGate.Release();
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "PC Assistant hit an unexpected error:\n\n" + e.Exception.Message,
            "PC Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The bridge first, so Nexus sees the pipe close rather than a half-shut task engine.
        try { _nexusBridge?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception) { /* shutting down regardless */ }

        // Resolve anything still waiting so no hook helper is left blocked on us.
        _approvals?.CancelAll();

        _mainViewModel?.Dispose();
        _service?.Dispose();
        _approvals?.Dispose();
        _store?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// What the bridge did, for the owner to read when Nexus says it cannot connect. Identifiers and
    /// executable paths only; never a prompt, a reply or an approval's contents. Rolled at a megabyte.
    /// </summary>
    private sealed class BridgeLog(string path)
    {
        private readonly object _gate = new();

        public void Write(string line)
        {
            lock (_gate)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
                    File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
                }
                catch (Exception)
                {
                    // A diagnostic log must never be the thing that breaks the bridge.
                }
            }
        }
    }

    private sealed class DispatcherUi(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action)
        {
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
    }
}
