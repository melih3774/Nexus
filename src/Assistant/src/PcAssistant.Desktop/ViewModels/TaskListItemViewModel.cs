using PcAssistant.Core.Model;
using PcAssistant.Desktop.Mvvm;

namespace PcAssistant.Desktop.ViewModels;

public sealed class TaskListItemViewModel : ObservableObject
{
    public TaskListItemViewModel(AssistantTask task) => Task = task;

    public AssistantTask Task { get; }
    public Guid Id => Task.Id;

    public string Title => Task.Title;
    public TaskState State => Task.State;

    public string StateGlyph => Task.State switch
    {
        TaskState.Running => "●",
        TaskState.WaitingForApproval => "!",
        TaskState.Completed => "✓",
        TaskState.Failed => "✕",
        TaskState.Cancelled => "■",
        TaskState.Interrupted => "⚠",
        _ => "○",
    };

    public string StateText => Task.State switch
    {
        TaskState.Draft => "Not started",
        TaskState.Running => "Working",
        TaskState.WaitingForApproval => "Needs approval",
        TaskState.Completed => "Done",
        TaskState.Failed => "Failed",
        TaskState.Cancelled => "Stopped",
        TaskState.Interrupted => "Interrupted",
        _ => Task.State.ToString(),
    };

    public string KindText => Task.Kind == TaskKind.Workspace
        ? System.IO.Path.GetFileName(Task.WorkspacePath ?? "") is { Length: > 0 } n ? n : "Workspace"
        : "General";

    public string SubtitleText => $"{KindText} · {Task.Mode.ToString().ToUpperInvariant()} · {StateText}";

    /// <summary>Re-reads the underlying task after the service mutated it.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(SubtitleText));
    }
}
