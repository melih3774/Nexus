using System.Windows;
using Microsoft.Win32;
using PcAssistant.Core.Model;
using PcAssistant.Desktop.ViewModels;

namespace PcAssistant.Desktop;

public partial class NewTaskWindow : Window
{
    private readonly NewTaskViewModel _form;

    public NewTaskWindow(NewTaskViewModel form)
    {
        _form = form;
        DataContext = form;
        InitializeComponent();

        AutoRadio.IsChecked = form.Mode == AssistantMode.Auto;
        AskRadio.IsChecked = form.Mode == AssistantMode.Ask;

        Loaded += (_, _) => MessageBox.Focus();
    }

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && WorkspacePanel is null) return;

        var workspace = WorkspaceRadio.IsChecked == true;
        _form.Kind = workspace ? TaskKind.Workspace : TaskKind.General;

        WorkspacePanel.Visibility = workspace ? Visibility.Visible : Visibility.Collapsed;
        KindHint.Text = workspace
            ? "Claude reads, edits, builds and tests inside this directory. It is the task's working directory."
            : "Runs in PC Assistant's own folder. Claude will discuss the idea and will not create a project unless you ask.";

        if (workspace && !_form.HasRoots)
            ErrorText.Text = "Add a project root first — PC Assistant has no filesystem scope by default.";
        else
            ErrorText.Text = string.Empty;
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a projects folder to approve" };
        if (picker.ShowDialog(this) != true) return;

        _form.AddRoot(picker.FolderName);
        ErrorText.Text = string.Empty;
    }

    private void SelectWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose an existing project workspace" };
        if (picker.ShowDialog(this) != true) return;

        _form.SelectWorkspace(picker.FolderName);
        WorkspaceCombo.SelectedItem = _form.WorkspacePath;
        ErrorText.Text = string.Empty;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!_form.HasRoots)
        {
            ErrorText.Text = "Add a project root first.";
            return;
        }

        var dialog = new NewProjectWindow(_form) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            WorkspaceCombo.SelectedItem = _form.WorkspacePath;
            ErrorText.Text = string.Empty;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        _form.Mode = AskRadio.IsChecked == true ? AssistantMode.Ask : AssistantMode.Auto;

        if (!_form.CanCreate)
        {
            ErrorText.Text = _form.IsWorkspaceTask && string.IsNullOrWhiteSpace(_form.WorkspacePath)
                ? "Choose a workspace."
                : "Type what you want first.";
            return;
        }

        if (_form.HasError)
        {
            ErrorText.Text = _form.Error!;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
