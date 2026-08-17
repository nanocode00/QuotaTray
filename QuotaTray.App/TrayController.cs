using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using QuotaTray.App.Helpers;
using QuotaTray.App.ViewModels;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace QuotaTray.App;

public class TrayController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly QuotaViewModel _viewModel;
    private readonly MainWindow _mainWindow;
    private readonly ToolStripMenuItem _autoStartMenuItem;
    private readonly ToolStripMenuItem _notificationsMenuItem;

    public TrayController(QuotaViewModel viewModel, MainWindow mainWindow)
    {
        _viewModel = viewModel;
        _mainWindow = mainWindow;

        _notifyIcon = new NotifyIcon
        {
            Icon = IconHelper.CreateDynamicTrayIcon(100, false, 32),
            Text = "AI Quota Tray",
            Visible = true
        };

        // State-transition toast notification subscriber
        _viewModel.ShowToastNotification += (title, message) =>
        {
            _notifyIcon.ShowBalloonTip(4000, title, message, ToolTipIcon.Warning);
        };

        var contextMenu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("쿼터 창 열기", null, (s, e) =>
        {
            ShowWindow();
        });
        contextMenu.Items.Add(openItem);

        var refreshItem = new ToolStripMenuItem("지금 새로고침", null, async (s, e) =>
        {
            await _viewModel.RefreshQuotaAsync();
        });
        contextMenu.Items.Add(refreshItem);

        var settingsItem = new ToolStripMenuItem("환경 설정...", null, (s, e) =>
        {
            _mainWindow.OpenSettings();
        });
        contextMenu.Items.Add(settingsItem);

        var diagItem = new ToolStripMenuItem("진단 정보 복사", null, (s, e) =>
        {
            try
            {
                string report = _viewModel.GetDiagnosticsReport();
                Clipboard.SetText(report);
                MessageBox.Show("진단 정보가 클립보드에 복사되었습니다.", "진단 정보", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"복사 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
        contextMenu.Items.Add(diagItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        _notificationsMenuItem = new ToolStripMenuItem("쿼터 부족 알림 받기", null, (s, e) =>
        {
            _viewModel.EnableNotifications = !_viewModel.EnableNotifications;
        })
        {
            Checked = _viewModel.EnableNotifications
        };
        contextMenu.Items.Add(_notificationsMenuItem);

        _autoStartMenuItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (s, e) =>
        {
            bool current = AutoStartupHelper.IsAutoStartupEnabled();
            AutoStartupHelper.SetAutoStartup(!current);
            if (s is ToolStripMenuItem item)
            {
                item.Checked = !current;
            }
        })
        {
            Checked = AutoStartupHelper.IsAutoStartupEnabled()
        };
        contextMenu.Items.Add(_autoStartMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("종료", null, (s, e) =>
        {
            _notifyIcon.Visible = false;
            Application.Current.Shutdown();
        });
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;

        _viewModel.SettingsChanged += (settings) =>
        {
            _notificationsMenuItem.Checked = settings.EnableNotifications;
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleWindow();
            }
        };

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(QuotaViewModel.TrayTooltipText))
            {
                UpdateTooltip();
            }
        };

        _viewModel.RefreshCompleted += OnRefreshCompleted;

        UpdateTooltip();
    }

    private void OnRefreshCompleted()
    {
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        try
        {
            var newIcon = IconHelper.CreateDynamicTrayIcon(_viewModel.LowestRemainingPercent, _viewModel.HasAnyError, 32);
            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = newIcon;
            oldIcon?.Dispose();
        }
        catch
        {
            // Ignore icon update errors
        }
    }

    private void UpdateTooltip()
    {
        string text = _viewModel.TrayTooltipText;
        if (text.Length >= 64) text = text[..63];
        _notifyIcon.Text = text;
    }

    public void ShowWindow()
    {
        PositionWindowNearTray();
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void HideWindow()
    {
        _mainWindow.Hide();
    }

    public void ToggleWindow()
    {
        if (_mainWindow.IsVisible && _mainWindow.IsActive)
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    private void PositionWindowNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        _mainWindow.Measure(new System.Windows.Size(_mainWindow.Width, double.PositiveInfinity));
        double width = _mainWindow.Width;
        double height = _mainWindow.DesiredSize.Height > 0 ? _mainWindow.DesiredSize.Height : (_mainWindow.ActualHeight > 0 ? _mainWindow.ActualHeight : 600);

        // Position at bottom-right above taskbar with padding
        _mainWindow.Left = workArea.Right - width - 12;
        _mainWindow.Top = Math.Max(workArea.Top + 12, workArea.Bottom - height - 12);
    }

    public void Dispose()
    {
        _viewModel.RefreshCompleted -= OnRefreshCompleted;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
