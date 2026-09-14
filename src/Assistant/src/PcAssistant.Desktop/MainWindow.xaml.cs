using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using PcAssistant.Desktop.ViewModels;

namespace PcAssistant.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _followTranscript = true;
    private HwndSource? _windowSource;
    private const int StopHotkeyId = 0x5043;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        UpdateThemeLabel();
        ThemeManager.Changed += Theme_Changed;

        viewModel.NewTaskRequested += (_, _) => ShowNewTaskDialog();
        viewModel.ManageRootsRequested += (_, _) => ShowRootsDialog();
        viewModel.ScrollRequested += (_, _) => Dispatcher.BeginInvoke(ScrollToEnd);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedTask)) return;
            _followTranscript = true;
            Dispatcher.BeginInvoke(ScrollToEnd);
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(handle);
            _windowSource?.AddHook(HotkeyMessage);
            if (!RegisterHotKey(handle, StopHotkeyId, 0x4003, 0x13)) // no repeat, Ctrl+Alt+Pause
                _vm.StopShortcutText = "Global Stop shortcut unavailable · use Stop";
        };
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= Theme_Changed;
            if (_windowSource is null) return;
            UnregisterHotKey(_windowSource.Handle, StopHotkeyId);
            _windowSource.RemoveHook(HotkeyMessage);
        };

        Loaded += async (_, _) =>
        {
            await _vm.RefreshClaudeStatusAsync();
            if (_vm.Tasks.Count == 0) ShowNewTaskDialog();
        };
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ThemeManager.Cycle();
    private void Theme_Changed(object? sender, EventArgs e) => Dispatcher.Invoke(UpdateThemeLabel);
    private void UpdateThemeLabel() => ThemeButton.Content = ThemeManager.DisplayName;

    private void ScrollToEnd()
    {
        if (_followTranscript) TranscriptScroll.ScrollToEnd();
    }

    private void Transcript_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.VerticalChange != 0)
            _followTranscript = TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset < 32;
        else if (e.ExtentHeightChange != 0 && _followTranscript)
            TranscriptScroll.ScrollToEnd();
    }

    private IntPtr HotkeyMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == StopHotkeyId)
        {
            _vm.StopAll();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    private void ShowNewTaskDialog()
    {
        var form = _vm.CreateNewTaskViewModel();
        var dialog = new NewTaskWindow(form) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        _ = _vm.StartTaskAsync(form);
    }

    private void ShowRootsDialog()
    {
        var form = _vm.CreateNewTaskViewModel();
        new RootsWindow(form) { Owner = this }.ShowDialog();
    }

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Shift+Enter inserts a newline; plain Enter sends.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;

        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProgressItem item })
            item.DetailVisible = !item.DetailVisible;
    }
}
