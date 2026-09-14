using System.Collections.ObjectModel;
using PcAssistant.Core;
using PcAssistant.Core.Approvals;
using PcAssistant.Core.Bridge;
using PcAssistant.Core.Logging;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;
using PcAssistant.Core.Elevation;
using PcAssistant.Core.NexusBridge;
using PcAssistant.Desktop.Mvvm;
using PcAssistant.Elevation;

namespace PcAssistant.Desktop.ViewModels;

/// <summary>
/// The shell view model: task list on the left, one task's conversation and progress on
/// the right. It never touches Claude processes; everything goes through AssistantService.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AssistantService _service;
    private readonly WorkspaceService _workspaces;
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly IUiDispatcher _ui;

    private readonly Dictionary<Guid, ObservableCollection<ConversationItem>> _transcripts = new();
    private readonly Dictionary<Guid, ApprovalItem> _openApprovals = new();

    public MainViewModel(
        AssistantService service,
        WorkspaceService workspaces,
        AppSettings settings,
        AppPaths paths,
        IUiDispatcher? dispatcher = null)
    {
        _service = service;
        _workspaces = workspaces;
        _settings = settings;
        _paths = paths;
        _ui = dispatcher ?? new ImmediateDispatcher();

        NewTaskCommand = new RelayCommand(() => NewTaskRequested?.Invoke(this, EventArgs.Empty));
        SendCommand = new RelayCommand(async () => await SendAsync(), () => CanSend);
        StopCommand = new RelayCommand(Stop, () => CanStop);
        ToggleRawLogCommand = new RelayCommand(ToggleRawLog);
        ContinueAsNewTaskCommand = new RelayCommand(ContinueAsNewTask, () => SelectedTask is not null);
        SetAutoCommand = new RelayCommand(() => SetMode(AssistantMode.Auto));
        SetAskCommand = new RelayCommand(() => SetMode(AssistantMode.Ask));
        ManageRootsCommand = new RelayCommand(() => ManageRootsRequested?.Invoke(this, EventArgs.Empty));
        ShowCapabilitiesCommand = new RelayCommand(async () =>
        {
            ShowCapabilities = !ShowCapabilities;
            if (ShowCapabilities) await RefreshCapabilitiesAsync();
        });
        RefreshCapabilitiesCommand = new RelayCommand(async () => await RefreshCapabilitiesAsync(), () => !IsRefreshingCapabilities);
        InstallPrivilegedHelperCommand = new RelayCommand(async () => await RunSetupAsync(install: true), () => !IsPrivilegedBusy);
        UninstallPrivilegedHelperCommand = new RelayCommand(async () => await RunSetupAsync(install: false), () => !IsPrivilegedBusy);

        _service.TaskChanged += OnTaskChanged;
        _service.EventAppended += OnEventAppended;
        _service.MessageAppended += OnMessageAppended;
        _service.Approvals.ApprovalRequested += OnApprovalRequested;
        _service.Approvals.ApprovalResolved += OnApprovalResolved;

        ClearTaskFilterCommand = new RelayCommand(() => TaskFilter = string.Empty, () => IsTaskFilterActive);
        ReloadTasks();
    }

    // ------------------------------------------------------------- commands

    public RelayCommand NewTaskCommand { get; }
    public RelayCommand SendCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleRawLogCommand { get; }
    public RelayCommand ContinueAsNewTaskCommand { get; }
    public RelayCommand SetAutoCommand { get; }
    public RelayCommand SetAskCommand { get; }
    public RelayCommand ManageRootsCommand { get; }
    public RelayCommand ShowCapabilitiesCommand { get; }
    public RelayCommand RefreshCapabilitiesCommand { get; }

    /// <summary>Composition root supplies real capability checks without coupling this UI to PC tools.</summary>
    public Func<CancellationToken, Task<string>>? CapabilityDiagnosticsProvider { get; set; }

    private bool _showCapabilities;
    public bool ShowCapabilities
    {
        get => _showCapabilities;
        private set => SetField(ref _showCapabilities, value);
    }

    private string _capabilityDiagnosticsText = "Checking capabilities…";
    public string CapabilityDiagnosticsText
    {
        get => _capabilityDiagnosticsText;
        private set => SetField(ref _capabilityDiagnosticsText, value);
    }

    private bool _isRefreshingCapabilities;
    public bool IsRefreshingCapabilities
    {
        get => _isRefreshingCapabilities;
        private set
        {
            if (SetField(ref _isRefreshingCapabilities, value)) RefreshCapabilitiesCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------- privileged operations

    public RelayCommand InstallPrivilegedHelperCommand { get; }
    public RelayCommand UninstallPrivilegedHelperCommand { get; }

    /// <summary>
    /// Supplied by the composition root. Null keeps the whole privileged section honest
    /// rather than showing buttons that would do nothing.
    /// </summary>
    public PrivilegedOperationService? Privileged { get; set; }

    /// <summary>Where the helper payload PC Assistant ships lives, and where its setup report goes.</summary>
    public string? HelperPayloadDirectory { get; set; }
    public string? HelperReportPath { get; set; }

    private string _privilegedStatusText = "Privileged operations: not checked yet";
    public string PrivilegedStatusText
    {
        get => _privilegedStatusText;
        private set => SetField(ref _privilegedStatusText, value);
    }

    private string _privilegedDetailText = string.Empty;
    public string PrivilegedDetailText
    {
        get => _privilegedDetailText;
        private set => SetField(ref _privilegedDetailText, value);
    }

    private string _privilegedHistoryText = string.Empty;
    public string PrivilegedHistoryText
    {
        get => _privilegedHistoryText;
        private set
        {
            if (SetField(ref _privilegedHistoryText, value)) OnPropertyChanged(nameof(HasPrivilegedHistory));
        }
    }

    public bool HasPrivilegedHistory => !string.IsNullOrWhiteSpace(_privilegedHistoryText);

    private bool _isPrivilegedBusy;
    public bool IsPrivilegedBusy
    {
        get => _isPrivilegedBusy;
        private set
        {
            if (!SetField(ref _isPrivilegedBusy, value)) return;
            InstallPrivilegedHelperCommand.RaiseCanExecuteChanged();
            UninstallPrivilegedHelperCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _canInstallHelper = true;
    public bool CanInstallHelper
    {
        get => _canInstallHelper;
        private set => SetField(ref _canInstallHelper, value);
    }

    private bool _canUninstallHelper;
    public bool CanUninstallHelper
    {
        get => _canUninstallHelper;
        private set => SetField(ref _canUninstallHelper, value);
    }

    /// <summary>
    /// The privileged helper's true state, established by a real round trip rather than by
    /// the presence of a file. A stale "Ready" here would be worse than no indicator at all.
    /// </summary>
    public async Task RefreshPrivilegedAsync(CancellationToken cancellationToken = default)
    {
        if (Privileged is null)
        {
            _ui.Post(() =>
            {
                PrivilegedStatusText = "Privileged operations: not available in this build";
                CanInstallHelper = CanUninstallHelper = false;
            });
            return;
        }

        HelperStatus status;
        try
        {
            status = await Privileged.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = new HelperStatus(HelperAvailability.Error, ex.Message);
        }

        var history = string.Join(Environment.NewLine,
            Privileged.RecentHistory(12).Select(record => record.Summary));

        _ui.Post(() =>
        {
            PrivilegedStatusText = "Privileged operations: " + status.DisplayText;
            PrivilegedDetailText = status.Detail;
            PrivilegedHistoryText = history;
            CanInstallHelper = status.Availability != HelperAvailability.Ready;
            CanUninstallHelper = status.Availability != HelperAvailability.NotInstalled;
        });
    }

    private async Task RunSetupAsync(bool install)
    {
        if (HelperPayloadDirectory is null || HelperReportPath is null)
        {
            _ui.Post(() => PrivilegedDetailText = "PC Assistant does not know where its privileged helper payload is.");
            return;
        }

        IsPrivilegedBusy = true;
        _ui.Post(() => PrivilegedDetailText = install
            ? "Waiting for Windows administrator approval on the local PC…"
            : "Removing the privileged helper. Windows will ask for administrator approval on the local PC…");

        try
        {
            var client = Environment.ProcessPath ?? string.Empty;
            var result = install
                ? await HelperSetup.InstallAsync(HelperPayloadDirectory, client, HelperReportPath).ConfigureAwait(false)
                : await HelperSetup.UninstallAsync(HelperPayloadDirectory, HelperReportPath).ConfigureAwait(false);

            _ui.Post(() => PrivilegedDetailText = result.Message);
        }
        catch (Exception ex)
        {
            _ui.Post(() => PrivilegedDetailText = "Setup failed: " + ex.Message);
        }
        finally
        {
            IsPrivilegedBusy = false;
            await RefreshPrivilegedAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------ Nexus bridge

    /// <summary>
    /// Supplied by the composition root: starts or stops the listener to match the setting. Null in
    /// tests and in any build without a bridge, which leaves the toggle harmless.
    /// </summary>
    public Action? NexusBridgeChanged { get; set; }

    public bool NexusBridgeEnabled
    {
        get => _settings.NexusBridge.Enabled;
        set
        {
            if (_settings.NexusBridge.Enabled == value) return;
            _settings.NexusBridge.Enabled = value;
            _settings.Save();
            OnPropertyChanged();
            NexusBridgeChanged?.Invoke();
        }
    }

    private NexusBridgeStatus _nexusBridge = new(NexusBridgeListenState.Stopped);

    public string NexusBridgeStatusText => "Nexus bridge: " + (_nexusBridge.State switch
    {
        _ when !NexusBridgeEnabled => "off",
        NexusBridgeListenState.Listening => "waiting for Nexus",
        NexusBridgeListenState.Connected => "Nexus connected",
        NexusBridgeListenState.NameInUse => "another program holds the bridge name",
        _ => "starting",
    });

    public string NexusBridgeDetailText
    {
        get
        {
            if (!NexusBridgeEnabled)
                return "When on, Nexus on this PC can list and open your tasks, start General tasks, send follow-ups, " +
                       "stop them, switch AUTO/ASK and answer ordinary approval cards for its owner-authenticated phone. " +
                       "Administrator approvals stay on this PC.";

            var trusted = _settings.NexusBridge.TrustedClientExecutable;
            var lines = new List<string>
            {
                string.IsNullOrWhiteSpace(trusted)
                    ? "No Nexus is trusted yet. Start Nexus with its assistant bridge enabled, then approve it here."
                    : "Trusted Nexus: " + trusted,
            };

            if (_nexusBridge.State == NexusBridgeListenState.NameInUse)
                lines.Add("The pipe name is already taken, so PC Assistant is not listening. Is PC Assistant open twice?");
            if (_nexusBridge.RefusedClient is { } refused && _nexusBridge.CandidateClient is null)
                lines.Add($"Refused {refused}: {_nexusBridge.RefusedReason}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string? NexusCandidateText => _nexusBridge.CandidateClient is { } path
        ? "Nexus at " + path + " asked to connect."
        : null;

    public bool HasNexusCandidate => _nexusBridge.CandidateClient is not null;
    public bool CanForgetNexus => !string.IsNullOrWhiteSpace(_settings.NexusBridge.TrustedClientExecutable);

    public RelayCommand TrustNexusCommand => _trustNexus ??= new RelayCommand(() =>
    {
        if (_nexusBridge.CandidateClient is not { } path) return;
        _settings.NexusBridge.TrustedClientExecutable = path;
        _settings.Save();
        NexusCandidateResolved?.Invoke();
        UpdateNexusBridge(_nexusBridge with { CandidateClient = null, RefusedClient = null, RefusedReason = null });
    });

    public RelayCommand ForgetNexusCommand => _forgetNexus ??= new RelayCommand(() =>
    {
        _settings.NexusBridge.TrustedClientExecutable = null;
        _settings.Save();
        // Dropping trust must also drop a live connection that was admitted under it.
        NexusBridgeChanged?.Invoke();
        UpdateNexusBridge(_nexusBridge);
    });

    private RelayCommand? _trustNexus;
    private RelayCommand? _forgetNexus;

    /// <summary>Called once the owner has trusted a candidate, so the listener can forget it too.</summary>
    public Action? NexusCandidateResolved { get; set; }

    public void UpdateNexusBridge(NexusBridgeStatus status) => _ui.Post(() =>
    {
        _nexusBridge = status;
        OnPropertyChanged(nameof(NexusBridgeEnabled));
        OnPropertyChanged(nameof(NexusBridgeStatusText));
        OnPropertyChanged(nameof(NexusBridgeDetailText));
        OnPropertyChanged(nameof(NexusCandidateText));
        OnPropertyChanged(nameof(HasNexusCandidate));
        OnPropertyChanged(nameof(CanForgetNexus));
    });

    private string _stopShortcutText = "Ctrl+Alt+Pause stops all running tasks";
    public string StopShortcutText
    {
        get => _stopShortcutText;
        set => SetField(ref _stopShortcutText, value);
    }

    public event EventHandler? NewTaskRequested;
    public event EventHandler? ManageRootsRequested;

    // ---------------------------------------------------------------- state

    private string _taskFilter = string.Empty;

    /// <summary>
    /// Narrows the task list by title or workspace. Filtering happens over the service's own
    /// list rather than by hiding rows, so the selected task survives a filter that excludes
    /// it — losing the conversation you are reading because you typed in a search box would be
    /// a worse problem than the one this solves.
    /// </summary>
    public string TaskFilter
    {
        get => _taskFilter;
        set
        {
            if (!SetField(ref _taskFilter, value ?? string.Empty)) return;
            ReloadTasks();
            OnPropertyChanged(nameof(IsTaskFilterActive));
            OnPropertyChanged(nameof(TaskFilterSummary));
        }
    }

    public bool IsTaskFilterActive => !string.IsNullOrWhiteSpace(TaskFilter);

    /// <summary>
    /// How many tasks the filter matched — which is not the same as how many rows are shown,
    /// because the open conversation stays in the list even when it does not match. Counting
    /// rows here would report "2 tasks match" for one match plus the task you are reading.
    /// </summary>
    private int _matchCount;

    public string TaskFilterSummary => !IsTaskFilterActive
        ? string.Empty
        : _matchCount switch
        {
            0 => $"No task matches “{TaskFilter.Trim()}”. Still showing the task you have open.",
            1 => $"1 task matches “{TaskFilter.Trim()}”.",
            var n => $"{n} tasks match “{TaskFilter.Trim()}”.",
        };

    /// <summary>Number of tasks the current filter matched, ignoring the pinned open task.</summary>
    public int TaskMatchCount => _matchCount;

    public RelayCommand ClearTaskFilterCommand { get; }

    private bool MatchesFilter(AssistantTask task)
    {
        var needle = TaskFilter.Trim();
        if (needle.Length == 0) return true;
        if (task.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return task.WorkspacePath is { } workspace &&
               workspace.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = new();
    public bool HasTasks => Tasks.Count > 0;

    private TaskListItemViewModel? _selected;
    public TaskListItemViewModel? SelectedTask
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;

            Transcript = value is null ? new ObservableCollection<ConversationItem>() : TranscriptFor(value.Id);
            RawLogText = string.Empty;
            ShowRawLog = false;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(IsAuto));
            OnPropertyChanged(nameof(IsAsk));
            OnPropertyChanged(nameof(IsInterrupted));
            RaiseCommandStates();
        }
    }

    public bool HasSelection => SelectedTask is not null;
    public string SelectedTitle => SelectedTask?.Title ?? "PC Assistant";

    public string SelectedSubtitle
    {
        get
        {
            var task = SelectedTask?.Task;
            if (task is null) return "Create a task to get started.";

            var where = task.Kind == TaskKind.Workspace
                ? task.WorkspacePath ?? "(workspace)"
                : "Personal computer task";

            return $"{where}  ·  {task.Mode.ToString().ToUpperInvariant()}  ·  {SelectedTask!.StateText}";
        }
    }

    public bool IsInterrupted => SelectedTask?.Task.State == TaskState.Interrupted;

    private ObservableCollection<ConversationItem> _transcript = new();
    public ObservableCollection<ConversationItem> Transcript
    {
        get => _transcript;
        private set => SetField(ref _transcript, value);
    }

    private string _composer = string.Empty;
    public string Composer
    {
        get => _composer;
        set
        {
            if (SetField(ref _composer, value)) RaiseCommandStates();
        }
    }

    private ClaudeStatus _claudeStatus = new(ClaudeAvailability.Unknown);
    public ClaudeStatus ClaudeStatus
    {
        get => _claudeStatus;
        private set
        {
            if (!SetField(ref _claudeStatus, value)) return;
            OnPropertyChanged(nameof(ClaudeStatusText));
            OnPropertyChanged(nameof(ClaudeStatusGlyph));
            OnPropertyChanged(nameof(ClaudeStatusDetail));
        }
    }

    public string ClaudeStatusText => ClaudeStatus.DisplayText;
    public string ClaudeStatusGlyph => ClaudeStatus.Availability == ClaudeAvailability.Ready ? "●" : "○";

    public string? ClaudeStatusDetail => ClaudeStatus.Availability == ClaudeAvailability.Ready
        ? $"Claude Code {ClaudeStatus.Version}"
        : ClaudeStatus.Detail;

    public bool IsAuto => SelectedTask?.Task.Mode == AssistantMode.Auto;
    public bool IsAsk => SelectedTask?.Task.Mode == AssistantMode.Ask;

    public bool IsBusy => SelectedTask is not null && _service.IsRunning(SelectedTask.Id);
    public bool CanSend => SelectedTask is not null && !IsBusy && !string.IsNullOrWhiteSpace(Composer);
    public bool CanStop => IsBusy;

    private bool _showRawLog;
    public bool ShowRawLog
    {
        get => _showRawLog;
        private set => SetField(ref _showRawLog, value);
    }

    private string _rawLogText = string.Empty;
    public string RawLogText
    {
        get => _rawLogText;
        private set => SetField(ref _rawLogText, value);
    }

    // ------------------------------------------------------------- lifecycle

    public async Task RefreshClaudeStatusAsync()
    {
        try
        {
            var status = await _service.GetClaudeStatusAsync().ConfigureAwait(false);
            _ui.Post(() => ClaudeStatus = status);
        }
        catch (Exception ex)
        {
            _ui.Post(() => ClaudeStatus = new ClaudeStatus(ClaudeAvailability.FailedToStart, Detail: ex.Message));
        }
    }


    // ------------------------------------------------------- start with Windows

    /// <summary>Composition root supplies the real registry-backed setting; tests supply their own.</summary>
    public Func<StartupState>? StartupStateProvider { get; set; }
    public Func<bool, StartupState>? StartupStateSetter { get; set; }

    private StartupState _startup = new(false, false, "Not checked", "Open Capabilities to check.");
    public StartupState Startup
    {
        get => _startup;
        private set
        {
            if (SetField(ref _startup, value))
            {
                OnPropertyChanged(nameof(StartWithWindows));
                OnPropertyChanged(nameof(StartupSummary));
                OnPropertyChanged(nameof(StartupDetail));
                OnPropertyChanged(nameof(CanChangeStartup));
            }
        }
    }

    public string StartupSummary => "Start PC Assistant with Windows — " + Startup.Summary;
    public string StartupDetail => Startup.Detail;
    public bool CanChangeStartup => Startup.Available;

    /// <summary>
    /// Bound to the toggle. Setting it writes the registry value and then reads it back, so the
    /// checkbox reflects what Windows actually holds rather than what was clicked — a write that
    /// failed leaves the box where it was and the detail line says why.
    /// </summary>
    public bool StartWithWindows
    {
        get => Startup.Enabled;
        set
        {
            // Availability is enforced here, not only by the disabled checkbox: a setting the
            // machine cannot support must not be written just because something set the property.
            if (value == Startup.Enabled || !Startup.Available || StartupStateSetter is not { } setter) return;
            Startup = setter(value);
        }
    }

    public void RefreshStartup()
    {
        if (StartupStateProvider is { } provider) Startup = provider();
    }

    public async Task RefreshCapabilitiesAsync()
    {
        if (IsRefreshingCapabilities) return;
        IsRefreshingCapabilities = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var text = CapabilityDiagnosticsProvider is { } provider
                ? await provider(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false)
                : $"Claude Code: {ClaudeStatusText}\n{ClaudeStatusDetail}\n\nPC capability diagnostics are not connected.";
            _ui.Post(() =>
            {
                CapabilityDiagnosticsText = text;
                RefreshStartup();
            });
            await RefreshPrivilegedAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ui.Post(() => CapabilityDiagnosticsText = ex is OperationCanceledException
                ? "Capability checks timed out. Try Refresh; raw task logs may include more detail."
                : "Could not check capabilities: " + ex.Message);
        }
        finally { _ui.Post(() => IsRefreshingCapabilities = false); }
    }

    public void ReloadTasks()
    {
        var previous = SelectedTask?.Id;

        Tasks.Clear();
        foreach (var t in _service.Tasks.Where(MatchesFilter)) Tasks.Add(new TaskListItemViewModel(t));
        _matchCount = Tasks.Count;

        // A filter that hides the open conversation must not close it. The selected task is
        // kept in the list even when it does not match, so searching never loses your place.
        if (previous is not null && Tasks.All(t => t.Id != previous) &&
            _service.Tasks.FirstOrDefault(t => t.Id == previous) is { } kept)
            Tasks.Insert(0, new TaskListItemViewModel(kept));

        SelectedTask = previous is null
            ? Tasks.FirstOrDefault()
            : Tasks.FirstOrDefault(t => t.Id == previous) ?? Tasks.FirstOrDefault();
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(TaskFilterSummary));
    }

    public NewTaskViewModel CreateNewTaskViewModel() => new(_workspaces, _settings);

    /// <summary>Creates the task and immediately sends its first message.</summary>
    public async Task StartTaskAsync(NewTaskViewModel form)
    {
        var task = _service.CreateTask(form.ToRequest());

        // A new task the current filter would hide is the most confusing possible outcome of
        // pressing New Task, so starting one clears the search.
        TaskFilter = string.Empty;

        SelectedTask = ListItemFor(task);

        await SendMessageAsync(task.Id, form.FirstMessage.Trim()).ConfigureAwait(false);
    }

    public async Task SendAsync()
    {
        if (SelectedTask is null || string.IsNullOrWhiteSpace(Composer)) return;

        var text = Composer.Trim();
        Composer = string.Empty;

        await SendMessageAsync(SelectedTask.Id, text).ConfigureAwait(false);
    }

    private async Task SendMessageAsync(Guid taskId, string text)
    {
        _ui.Post(RaiseCommandStates);
        try
        {
            await _service.SendAsync(taskId, text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ui.Post(() => Append(taskId, new MessageItem
            {
                Role = MessageRole.System,
                Text = ex.Message,
            }));
        }
        finally
        {
            _ui.Post(RaiseCommandStates);
        }
    }

    public void Stop()
    {
        if (SelectedTask is null) return;
        _service.Stop(SelectedTask.Id);
        RaiseCommandStates();
    }

    public void StopAll()
    {
        foreach (var task in _service.Tasks.Where(t => _service.IsRunning(t.Id))) _service.Stop(task.Id);
        RaiseCommandStates();
    }

    public void SetMode(AssistantMode mode)
    {
        if (SelectedTask is null) return;

        _service.SetMode(SelectedTask.Id, mode);
        SelectedTask.Refresh();

        OnPropertyChanged(nameof(IsAuto));
        OnPropertyChanged(nameof(IsAsk));
        OnPropertyChanged(nameof(SelectedSubtitle));
    }

    public void ContinueAsNewTask()
    {
        if (SelectedTask is null) return;

        var task = _service.ContinueAsNewTask(SelectedTask.Id);
        SelectedTask = ListItemFor(task);

        // Selecting the task loads its persisted handover exactly once.
    }

    /// <summary>
    /// The list row for a task this window just created. The service announces a new task as it
    /// creates it, and on the UI thread that announcement may already have added the row, so it is
    /// reused rather than inserted twice.
    /// </summary>
    private TaskListItemViewModel ListItemFor(AssistantTask task)
    {
        var item = Tasks.FirstOrDefault(t => t.Id == task.Id);
        if (item is not null) return item;

        item = new TaskListItemViewModel(task);
        Tasks.Insert(0, item);
        OnPropertyChanged(nameof(HasTasks));
        return item;
    }

    public void ToggleRawLog()
    {
        if (SelectedTask is null) return;

        ShowRawLog = !ShowRawLog;
        if (!ShowRawLog)
        {
            RawLogText = string.Empty;
            return;
        }

        var path = SelectedTask.Task.RawLogPath ?? _paths.RawLogFor(SelectedTask.Id);
        var text = RawLogWriter.ReadTail(path);
        RawLogText = string.IsNullOrWhiteSpace(text)
            ? "(nothing logged for this task yet)"
            : text;
    }

    // ---------------------------------------------------------------- events

    private ObservableCollection<ConversationItem> TranscriptFor(Guid taskId)
    {
        if (_transcripts.TryGetValue(taskId, out var existing)) return existing;

        var items = new ObservableCollection<ConversationItem>();

        // Rebuild history from the store, interleaved by time.
        var messages = _service.MessagesFor(taskId)
            .Select(m => (ConversationItem)new MessageItem { Id = m.Id, Role = m.Role, Text = m.Text, Timestamp = m.CreatedAt });

        var events = _service.EventsFor(taskId)
            .Where(ShouldShow)
            .Select(e => (ConversationItem)new ProgressItem
            {
                Kind = e.Kind, Title = e.Title, Detail = e.Detail, ToolName = e.ToolName, Timestamp = e.CreatedAt,
            });

        foreach (var item in messages.Concat(events).OrderBy(i => i.Timestamp))
            items.Add(item);

        _transcripts[taskId] = items;
        return items;
    }

    /// <summary>
    /// Assistant text arrives both as an event and as a stored message; showing the event
    /// too would double every reply.
    /// </summary>
    private static bool ShouldShow(AssistantEvent e) => e.Kind is not
        (AssistantEventKind.AssistantMessage or AssistantEventKind.AssistantMessageDelta);

    private void Append(Guid taskId, ConversationItem item)
    {
        var list = TranscriptFor(taskId);
        list.Add(item);
        if (SelectedTask?.Id == taskId) ScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ScrollRequested;

    private void OnTaskChanged(object? sender, AssistantTask task) => _ui.Post(() =>
    {
        var item = Tasks.FirstOrDefault(t => t.Id == task.Id);
        item?.Refresh();

        // A task this window did not create — started from the phone through Nexus — still belongs in
        // the list. It is added without being selected: the owner may be reading something else here.
        if (item is null && MatchesFilter(task))
        {
            Tasks.Insert(0, new TaskListItemViewModel(task));
            _matchCount++;
            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(TaskFilterSummary));
            SelectedTask ??= Tasks[0];
        }

        if (SelectedTask?.Id == task.Id)
        {
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(IsInterrupted));
            RaiseCommandStates();
        }
    });

    private void OnMessageAppended(object? sender, TaskMessage message) => _ui.Post(() =>
    {
        // The transcript may not be materialised yet for a background task.
        if (!_transcripts.ContainsKey(message.TaskId) && SelectedTask?.Id != message.TaskId) return;

        var existing = TranscriptFor(message.TaskId).OfType<MessageItem>().FirstOrDefault(m => m.Id == message.Id);
        if (existing is not null)
        {
            existing.Text = message.Text;
            if (SelectedTask?.Id == message.TaskId) ScrollRequested?.Invoke(this, EventArgs.Empty);
        }
        else Append(message.TaskId, new MessageItem
        {
            Id = message.Id, Role = message.Role, Text = message.Text, Timestamp = message.CreatedAt,
        });
    });

    private void OnEventAppended(object? sender, AssistantEvent e) => _ui.Post(() =>
    {
        if (!ShouldShow(e)) return;
        if (!_transcripts.ContainsKey(e.TaskId) && SelectedTask?.Id != e.TaskId) return;

        // The approval card itself carries the request; the paired events would repeat it.
        if (e.Kind is AssistantEventKind.ApprovalRequired) return;

        Append(e.TaskId, new ProgressItem
        {
            Kind = e.Kind, Title = e.Title, Detail = e.Detail, ToolName = e.ToolName, Timestamp = e.CreatedAt,
        });
    });

    private void OnApprovalRequested(object? sender, ApprovalRequest request) => _ui.Post(() =>
    {
        var card = new ApprovalItem
        {
            Request = request,
            Respond = allow => _service.Approvals.Resolve(request.Id, allow),
            RespondForTask = () => _service.Approvals.ResolveForTask(request.Id),
        };

        _openApprovals[request.Id] = card;
        Append(request.TaskId, card);
    });

    private void OnApprovalResolved(object? sender, ApprovalResolution resolution) => _ui.Post(() =>
    {
        if (!_openApprovals.Remove(resolution.RequestId, out var card)) return;

        card.MarkResolved(resolution.Outcome switch
        {
            ApprovalOutcome.Allowed => "Allowed",
            ApprovalOutcome.Denied => "Denied",
            ApprovalOutcome.TimedOut => "No answer — treated as denied",
            _ => "Cancelled",
        });
    });

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanStop));
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ContinueAsNewTaskCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _service.TaskChanged -= OnTaskChanged;
        _service.EventAppended -= OnEventAppended;
        _service.MessageAppended -= OnMessageAppended;
        _service.Approvals.ApprovalRequested -= OnApprovalRequested;
        _service.Approvals.ApprovalResolved -= OnApprovalResolved;
    }
}
