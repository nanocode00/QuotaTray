using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using QuotaTray.App.Helpers;
using QuotaTray.App.ViewModels;

namespace QuotaTray.App;

public partial class SettingsWindow : Window
{
    public QuotaViewModel ViewModel { get; }

    public SettingsWindow(QuotaViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;

        StartupCheckBox.IsChecked = AutoStartupHelper.IsAutoStartupEnabled();
    }

    public void SelectTab(string tabName)
    {
        if (tabName.Equals("providers", StringComparison.OrdinalIgnoreCase))
        {
            ProvidersTabRadio.IsChecked = true;
            GeneralTabRadio.IsChecked = false;
            GeneralTabContent.Visibility = Visibility.Collapsed;
            ProvidersTabContent.Visibility = Visibility.Visible;
        }
        else
        {
            GeneralTabRadio.IsChecked = true;
            ProvidersTabRadio.IsChecked = false;
            GeneralTabContent.Visibility = Visibility.Visible;
            ProvidersTabContent.Visibility = Visibility.Collapsed;
        }
    }

    private void TabRadio_Click(object sender, RoutedEventArgs e)
    {
        if (GeneralTabRadio.IsChecked == true)
        {
            GeneralTabContent.Visibility = Visibility.Visible;
            ProvidersTabContent.Visibility = Visibility.Collapsed;
        }
        else
        {
            GeneralTabContent.Visibility = Visibility.Collapsed;
            ProvidersTabContent.Visibility = Visibility.Visible;
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        AutoStartupHelper.SetAutoStartup(StartupCheckBox.IsChecked == true);
    }

    private void ToggleApiKeyInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderToggleOption opt)
        {
            opt.IsEditingKey = !opt.IsEditingKey;
        }
    }

    private void LaunchProviderCli_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderToggleOption opt)
        {
            ViewModel.LaunchCliLogin(opt.CliLoginCommand);
        }
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderToggleOption opt)
        {
            opt.SaveCurrentApiKey();
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = ViewModel.GetDiagnosticsReport();
            System.Windows.Clipboard.SetText(report);
            System.Windows.MessageBox.Show("진단 정보가 클립보드에 복사되었습니다.", "진단 정보", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"복사 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
