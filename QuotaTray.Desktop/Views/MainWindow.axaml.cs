using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuotaTray.Desktop.ViewModels;

namespace QuotaTray.Desktop.Views;

public partial class MainWindow : Window
{
    public QuotaViewModel ViewModel { get; }
    private bool _isPinned = false;
    private DateTime _lastShownTime = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new QuotaViewModel();
        DataContext = ViewModel;
        SizeChanged += MainWindow_SizeChanged;
    }

    public MainWindow(QuotaViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        SizeChanged += MainWindow_SizeChanged;
    }

    public void ShowNearTray()
    {
        _lastShownTime = DateTime.UtcNow;
        UpdateMaxHeight();
        ReanchorToTray();

        WindowState = WindowState.Normal;
        Show();
        Topmost = true;
        Activate();
        Focus();
    }

    private void UpdateMaxHeight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            double scaling = screen.Scaling;
            double screenLogicalHeight = screen.WorkingArea.Height / scaling;
            MaxHeight = Math.Max(380, screenLogicalHeight * 0.50);
        }
    }

    public void ReanchorToTray()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            var workingArea = screen.WorkingArea;
            double scaling = screen.Scaling;

            int targetWidth = (int)(Width * scaling);
            int targetHeight = (int)((Bounds.Height > 50 ? Bounds.Height : 420) * scaling);

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
    }

    private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsVisible)
        {
            ReanchorToTray();
        }
    }

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Dragging disabled per user request: Window stays fixed to tray
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_isPinned && IsVisible)
        {
            // Debounce to prevent instant re-close from tray click focus race condition
            if ((DateTime.UtcNow - _lastShownTime).TotalMilliseconds < 400)
            {
                return;
            }
            Hide();
        }
    }

    private void CompactModeToggle_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.IsCompactMode = !ViewModel.IsCompactMode;
    }

    private void PinButton_Click(object? sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        if (PinIconPath != null)
        {
            PinIconPath.Opacity = _isPinned ? 1.0 : 0.5;
            PinIconPath.Fill = _isPinned ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#818CF8")) : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#94A3B8"));
        }
        ToolTip.SetTip(PinButton, _isPinned ? "창 고정 해제" : "창 항상 위에 고정");
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.OpenSettings();
        }
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshQuotaAsync();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ToggleProviderModels_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProviderItemViewModel vm)
        {
            vm.ToggleModels();
        }
    }

    private void ToggleGroupModels_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is QuotaGroupViewModel gvm)
        {
            gvm.Toggle();
        }
    }

    private void CardAuthSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProviderItemViewModel vm)
        {
            if (Application.Current is App app)
            {
                app.OpenSettings(vm.Key);
            }
        }
    }

    private void CardCliLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProviderItemViewModel vm)
        {
            ViewModel.LaunchCliLogin(vm.CliLoginCommand);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Only intercept user-initiated close (X button / Alt+F4) to hide to tray.
        // Allow explicit app shutdown and OS shutdown/logoff to proceed.
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
