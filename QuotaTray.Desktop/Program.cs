using System;
#if DEBUG
using System.Linq;
#endif
using Avalonia;

namespace QuotaTray.Desktop;

internal class Program
{
#if DEBUG
    public static bool UseCodexSparkMock { get; private set; }
#endif

    [STAThread]
    public static void Main(string[] args)
    {
#if DEBUG
        UseCodexSparkMock = args.Any(a => a.Equals("--mock-codex-spark", StringComparison.OrdinalIgnoreCase));
#endif

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
