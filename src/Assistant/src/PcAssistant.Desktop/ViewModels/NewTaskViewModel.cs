using System.Collections.ObjectModel;
using PcAssistant.Core.Model;
using PcAssistant.Core.Persistence;
using PcAssistant.Core.Tasks;
using PcAssistant.Core.Workspaces;
using PcAssistant.Desktop.Mvvm;

namespace PcAssistant.Desktop.ViewModels;

/// <summary>
/// The New Task flow: type, mode, workspace, first message. No manual title — it is
/// derived from the message.
/// </summary>
public sealed class NewTaskViewModel : ObservableObject
{
    private readonly WorkspaceService _workspaces;
    private readonly AppSettings _settings;

    public NewTaskViewModel(WorkspaceService workspaces, AppSettings settings)
    {
        _workspaces = workspaces;
        _settings = settings;
        _mode = settings.LastMode;

        RefreshWorkspaces();
    }

    public ObservableCollection<string> AvailableWorkspaces { get; } = new();
    public ObservableCollection<string> Roots { get; } = new();

    private TaskKind _kind = TaskKind.General;
    public TaskKind Kind
    {
        get => _kind;
        set
        {
            if (!SetField(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsWorkspaceTask));
            OnPropertyChanged(nameof(CanCreate));
            Validate();
        }
    }

    public bool IsWorkspaceTask => Kind == TaskKind.Workspace;

    private AssistantMode _mode;
    public AssistantMode Mode
    {
        get => _mode;
        set => SetField(ref _mode, value);
    }

    private string? _workspacePath;
    public string? WorkspacePath
    {
        get => _workspacePath;
        set
        {
            if (!SetField(ref _workspacePath, value)) return;
            OnPropertyChanged(nameof(CanCreate));
            Validate();
            if (Kind == TaskKind.Workspace && !HasError && !string.IsNullOrWhiteSpace(_workspacePath))
                SetWorkspaceFeedback($"Workspace selected: {_workspacePath}", false);
        }
    }

    private string? _workspaceFeedback;
    public string? WorkspaceFeedback
    {
        get => _workspaceFeedback;
        private set => SetField(ref _workspaceFeedback, value);
    }

    private bool _workspaceFeedbackIsError;
    public bool WorkspaceFeedbackIsError
    {
        get => _workspaceFeedbackIsError;
        private set => SetField(ref _workspaceFeedbackIsError, value);
    }

    private string _firstMessage = string.Empty;
    public string FirstMessage
    {
        get => _firstMessage;
        set
        {
            if (!SetField(ref _firstMessage, value)) return;
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(PreviewTitle));
        }
    }

    public string PreviewTitle => TitleGenerator.FromPrompt(FirstMessage);

    private string? _error;
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(FirstMessage) &&
        (Kind == TaskKind.General || !string.IsNullOrWhiteSpace(WorkspacePath));

    public void RefreshWorkspaces()
    {
        AvailableWorkspaces.Clear();
        foreach (var w in _workspaces.EnumerateWorkspaces()) AvailableWorkspaces.Add(w);

        Roots.Clear();
        foreach (var r in _workspaces.Roots) Roots.Add(r);

        OnPropertyChanged(nameof(HasRoots));
    }

    public bool HasRoots => _workspaces.Roots.Count > 0;

    private void Validate()
    {
        if (Kind != TaskKind.Workspace || string.IsNullOrWhiteSpace(WorkspacePath))
        {
            Error = null;
            return;
        }

        var result = _workspaces.ValidateWorkspace(WorkspacePath);
        Error = result.Ok ? null : result.Error;
    }

    /// <summary>Adds a project root the owner picked. Returns an error string, or null on success.</summary>
    public string? AddRoot(string path)
    {
        var result = _workspaces.ValidateRoot(path);
        if (!result.Ok)
        {
            SetWorkspaceFeedback(result.Error, true);
            return result.Error;
        }

        var alreadyApproved = _settings.WorkspaceRoots.Contains(result.CanonicalPath!, StringComparer.OrdinalIgnoreCase);
        if (!alreadyApproved)
        {
            _settings.WorkspaceRoots.Add(result.CanonicalPath!);
            _settings.Save();
        }

        RefreshWorkspaces();
        SetWorkspaceFeedback(alreadyApproved
            ? $"Projects folder already approved: {result.CanonicalPath}"
            : $"Projects folder added: {result.CanonicalPath}", false);
        return null;
    }

    /// <summary>Validates and selects an existing project directory under an approved root.</summary>
    public string? SelectWorkspace(string path)
    {
        var result = _workspaces.ValidateWorkspace(path);
        if (!result.Ok)
        {
            SetWorkspaceFeedback(result.Error, true);
            return result.Error;
        }

        if (!AvailableWorkspaces.Contains(result.CanonicalPath!, StringComparer.OrdinalIgnoreCase))
            AvailableWorkspaces.Add(result.CanonicalPath!);

        Kind = TaskKind.Workspace;
        WorkspacePath = result.CanonicalPath;
        SetWorkspaceFeedback($"Workspace selected: {result.CanonicalPath}", false);
        return null;
    }

    /// <summary>Creates a new project folder under a root and selects it.</summary>
    public string? CreateWorkspaceFolder(string root, string folderName)
    {
        var result = _workspaces.CreateWorkspace(root, folderName);
        if (!result.Ok)
        {
            SetWorkspaceFeedback(result.Error, true);
            return result.Error;
        }

        RefreshWorkspaces();
        Kind = TaskKind.Workspace;
        WorkspacePath = result.CanonicalPath;
        SetWorkspaceFeedback($"Project created: {result.CanonicalPath}", false);
        return null;
    }

    private void SetWorkspaceFeedback(string? message, bool isError)
    {
        WorkspaceFeedbackIsError = isError;
        WorkspaceFeedback = message;
    }

    public TaskCreationRequest ToRequest() => new()
    {
        Kind = Kind,
        Mode = Mode,
        WorkspacePath = Kind == TaskKind.Workspace ? WorkspacePath : null,
        FirstMessage = FirstMessage.Trim(),
    };
}
