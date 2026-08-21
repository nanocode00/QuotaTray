using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuotaTray.Desktop.ViewModels;

namespace QuotaTray.Desktop.Views;

public partial class SettingsWindow : Window
{
    public QuotaViewModel ViewModel { get; }

    public SettingsWindow()
    {
        InitializeComponent();
        ViewModel = new QuotaViewModel();
        DataContext = ViewModel;
    }

    public SettingsWindow(QuotaViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        SizeChanged += SettingsWindow_SizeChanged;

        StartupCheckBox.IsChecked = ViewModel.AutoStartupService.IsAutoStartupEnabled();
    }

    public void ShowAtTrayPosition(PixelPoint? preferredPosition = null)
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            double scaling = screen.Scaling;
            double screenLogicalHeight = screen.WorkingArea.Height / scaling;
            MaxHeight = Math.Max(380, screenLogicalHeight * 0.50);

            var workingArea = screen.WorkingArea;
            int targetWidth = (int)(Width * scaling);
            int targetHeight = (int)((Bounds.Height > 50 ? Bounds.Height : 450) * scaling);

            if (OperatingSystem.IsMacOS())
            {
                int x = workingArea.Right - targetWidth - (int)(16 * scaling);
                int y = workingArea.Y + (int)(32 * scaling);
                Position = new PixelPoint(x, y);
            }
            else
            {
                int x = workingArea.Right - targetWidth - (int)(16 * scaling);
                int y = workingArea.Bottom - targetHeight - (int)(12 * scaling);
                Position = new PixelPoint(x, y);
            }
        }

        WindowState = WindowState.Normal;
        Show();
        Topmost = true;
        Activate();
        Focus();
    }

    private void SettingsWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsVisible)
        {
            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen != null)
            {
                var workingArea = screen.WorkingArea;
                double scaling = screen.Scaling;
                int targetWidth = (int)(Width * scaling);
                int targetHeight = (int)((Bounds.Height > 50 ? Bounds.Height : 450) * scaling);

                if (!OperatingSystem.IsMacOS())
                {
                    int x = workingArea.Right - targetWidth - (int)(16 * scaling);
                    int y = workingArea.Bottom - targetHeight - (int)(12 * scaling);
                    Position = new PixelPoint(x, y);
                }
            }
        }
    }

    public void SelectTab(string tabName)
    {
        if (tabName.Equals("providers", StringComparison.OrdinalIgnoreCase))
        {
            ProvidersTabRadio.IsChecked = true;
            GeneralTabRadio.IsChecked = false;
            GeneralTabContent.IsVisible = false;
            ProvidersTabContent.IsVisible = true;
        }
        else
        {
            GeneralTabRadio.IsChecked = true;
            ProvidersTabRadio.IsChecked = false;
            GeneralTabContent.IsVisible = true;
            ProvidersTabContent.IsVisible = false;
        }
    }

    private void TabRadio_Click(object? sender, RoutedEventArgs e)
    {
        if (GeneralTabRadio.IsChecked == true)
        {
            GeneralTabContent.IsVisible = true;
            ProvidersTabContent.IsVisible = false;
        }
        else
        {
            GeneralTabContent.IsVisible = false;
            ProvidersTabContent.IsVisible = true;
        }
    }

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Dragging disabled per user request: Window stays fixed to tray
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
        if (Application.Current is App app)
        {
            app.ShowMainWindow();
        }
    }

    private void StartupCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        ViewModel.AutoStartupService.SetAutoStartup(StartupCheckBox.IsChecked == true);
    }

    private void ToggleApiKeyInput_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProviderToggleOption opt)
        {
            opt.IsEditingKey = !opt.IsEditingKey;
        }
    }

    private void LaunchProviderCli_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProviderToggleOption opt)
        {
            ViewModel.LaunchCliLogin(opt.CliLoginCommand);
        }
    }

    private void SaveApiKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProviderToggleOption opt)
        {
            opt.SaveCurrentApiKey();
        }
    }

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string report = ViewModel.GetDiagnosticsReport();
            if (Clipboard != null)
            {
                await Clipboard.SetTextAsync(report);
            }
        }
        catch { }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Only intercept user-initiated close (X button / Alt+F4) to hide to tray.
        // Allow explicit app shutdown and OS shutdown/logoff to proceed.
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            if (Application.Current is App app)
            {
                app.ShowMainWindow();
            }
        }
    }
}
