using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using QuotaTray.App.Helpers;
using QuotaTray.App.ViewModels;

namespace QuotaTray.App;

public partial class MainWindow : Window
{
    public QuotaViewModel ViewModel { get; }
    private SettingsWindow? _settingsWindow;
    private bool _isPinned = false;

    public MainWindow(QuotaViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_isPinned && IsVisible && (_settingsWindow == null || !_settingsWindow.IsActive))
        {
            Hide();
        }
    }

    private void CompactModeToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsCompactMode = !ViewModel.IsCompactMode;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinIconText.Opacity = _isPinned ? 1.0 : 0.6;
        PinButton.ToolTip = _isPinned ? "창 고정 해제" : "창 항상 위에 고정";
    }

    public void OpenSettings(string? focusProviderKey = null)
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(ViewModel);
        }

        if (!string.IsNullOrEmpty(focusProviderKey))
        {
            _settingsWindow.SelectTab("providers");
            var match = ViewModel.AvailableProviders.FirstOrDefault(p => p.Key.Equals(focusProviderKey, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                match.IsEditingKey = true;
            }
        }

        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshQuotaAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ToggleProviderModels_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderItemViewModel vm)
        {
            vm.ToggleModels();
        }
    }

    private void CardAuthSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderItemViewModel vm)
        {
            OpenSettings(vm.Key);
        }
    }

    private void CardCliLogin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderItemViewModel vm)
        {
            ViewModel.LaunchCliLogin(vm.CliLoginCommand);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Don't close, just hide to tray
        e.Cancel = true;
        Hide();
    }
}