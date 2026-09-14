using System.IO;
using System.Windows;
using PcAssistant.Desktop.ViewModels;

namespace PcAssistant.Desktop;

public partial class NewProjectWindow : Window
{
    private readonly NewTaskViewModel form;

    public NewProjectWindow(NewTaskViewModel form)
    {
        this.form = form;
        DataContext = form;
        InitializeComponent();
        RootCombo.SelectedIndex = form.Roots.Count > 0 ? 0 : -1;
        Loaded += (_, _) => ProjectName.Focus();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (FullPath is null) return;
        var root = RootCombo.SelectedItem as string;
        var name = ProjectName.Text.Trim();
        try { FullPath.Text = string.IsNullOrWhiteSpace(root) ? "Choose a projects folder." : Path.Combine(root, name); }
        catch { FullPath.Text = "The resulting path is not valid."; }
        Feedback.Text = string.Empty;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (RootCombo.SelectedItem is not string root)
        {
            Feedback.Text = "Choose an approved projects folder.";
            return;
        }

        var error = form.CreateWorkspaceFolder(root, ProjectName.Text);
        if (error is not null)
        {
            Feedback.Text = error;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
