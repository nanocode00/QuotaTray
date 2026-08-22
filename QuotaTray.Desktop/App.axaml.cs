using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using QuotaTray.Core.Services;
#if DEBUG
using QuotaTray.Desktop.Diagnostics;
#endif
using QuotaTray.Desktop.Utils;
using QuotaTray.Desktop.ViewModels;
using QuotaTray.Desktop.Views;

namespace QuotaTray.Desktop;

public partial class App : Application
{
    public QuotaViewModel ViewModel { get; private set; } = null!;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var quotaService = new QuotaService();
#if DEBUG
            if (Program.UseCodexSparkMock)
            {
                // Override only the Codex provider. Other enabled providers continue to use
                // their normal implementations, while Codex flows through the real parser
                // methods with a sanitized Spark fixture for end-to-end UI verification.
                quotaService.RegisterProvider(new CodexSparkMockQuotaProvider());
            }
#endif
            ViewModel = new QuotaViewModel(quotaService);

            _mainWindow = new MainWindow(ViewModel);
            _settingsWindow = new SettingsWindow(ViewModel);

            var trayIcons = TrayIcon.GetIcons(this);
            if (trayIcons != null && trayIcons.Count > 0)
            {
                _trayIcon = trayIcons[0];
                UpdateTrayIcon();
            }

            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(QuotaViewModel.TrayTooltipText) && _trayIcon != null)
                {
                    _trayIcon.ToolTipText = ViewModel.TrayTooltipText;
                }
                else if ((e.PropertyName == nameof(QuotaViewModel.LowestRemainingPercent) ||
                          e.PropertyName == nameof(QuotaViewModel.HasAnyError)) && _trayIcon != null)
                {
                    UpdateTrayIcon();
                }
            };

            ViewModel.RefreshCompleted += () =>
            {
                UpdateTrayIcon();
            };

            // Start initial background refresh
            _ = ViewModel.RefreshQuotaAsync();

            // Show window immediately on startup
            Dispatcher.UIThread.Post(() =>
            {
                _mainWindow.ShowNearTray();
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _trayIcon.ToolTipText = ViewModel.TrayTooltipText;
                _trayIcon.Icon = TrayIconRenderer.CreateTrayIcon(
                    ViewModel.LowestRemainingPercent,
                    ViewModel.HasAnyError);
            }
            catch { }
        });
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        ToggleMainWindow();
    }

    public void ToggleMainWindow()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
        }
        else
        {
            _mainWindow.ShowNearTray();
        }
    }

    public void OpenSettings(string? focusProviderKey = null)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(ViewModel);
        }

        if (!string.IsNullOrEmpty(focusProviderKey))
        {
            _settingsWindow.SelectTab("providers");
        }

        var pos = _mainWindow?.Position;
        _mainWindow?.Hide();
        _settingsWindow.ShowAtTrayPosition(pos);
    }

    public void ShowMainWindow()
    {
        _settingsWindow?.Hide();
        if (_mainWindow != null)
        {
            _mainWindow.ShowNearTray();
        }
    }

    private void OnOpenWindowClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        await ViewModel.RefreshQuotaAsync();
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        OpenSettings();
    }

    private async void OnCopyDiagnosticsClicked(object? sender, EventArgs e)
    {
        try
        {
            string report = ViewModel.GetDiagnosticsReport();
            if (_mainWindow?.Clipboard != null)
            {
                await _mainWindow.Clipboard.SetTextAsync(report);
            }
        }
        catch { }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
