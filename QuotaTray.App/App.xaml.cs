using System;
using System.Linq;
using System.Threading;
using System.Windows;
using QuotaTray.App.ViewModels;
using WpfApplication = System.Windows.Application;

namespace QuotaTray.App;

public partial class App : WpfApplication
{
    private const string MutexName = "QuotaTray_SingleInstance_App_Mutex";
    private Mutex? _singleInstanceMutex;
    private TrayController? _trayController;
    private MainWindow? _mainWindow;
    private QuotaViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running
            Shutdown();
            return;
        }

        _viewModel = new QuotaViewModel();
        _mainWindow = new MainWindow(_viewModel);
        _trayController = new TrayController(_viewModel, _mainWindow);

        bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) || a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

        // Initial quota refresh
        _ = _viewModel.RefreshQuotaAsync();

        if (!startMinimized)
        {
            _trayController.ShowWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayController?.Dispose();
        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
            catch
            {
                // Ignore
            }
        }
        base.OnExit(e);
    }
}
