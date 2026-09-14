using System.Windows;
using Microsoft.Win32;
using PcAssistant.Desktop.ViewModels;

namespace PcAssistant.Desktop;

public partial class RootsWindow : Window
{
    private readonly NewTaskViewModel _form;

    public RootsWindow(NewTaskViewModel form)
    {
        _form = form;
        DataContext = form;
        InitializeComponent();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a projects folder to approve" };
        if (picker.ShowDialog(this) != true) return;

        _form.AddRoot(picker.FolderName);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
