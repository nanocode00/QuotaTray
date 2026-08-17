using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace QuotaTray.Core.Platform;

public class CrossPlatformAutoStartup : IAutoStartupService
{
    private const string AppName = "QuotaTray";
    private const string MacPlistName = "com.quotatray.desktop.plist";
    private const string LinuxDesktopName = "quotatray.desktop";

    public bool IsAutoStartupEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue(AppName) != null;
            }
            else if (OperatingSystem.IsMacOS())
            {
                string plistPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents", MacPlistName);
                return File.Exists(plistPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                string desktopPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "autostart", LinuxDesktopName);
                return File.Exists(desktopPath);
            }
        }
        catch { }

        return false;
    }

    public bool SetAutoStartup(bool enable, string? executablePath = null)
    {
        string exePath = executablePath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exePath)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return false;

                if (enable)
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
                return true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                string launchAgentsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents");
                string plistPath = Path.Combine(launchAgentsDir, MacPlistName);

                if (enable)
                {
                    Directory.CreateDirectory(launchAgentsDir);
                    string plistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>com.quotatray.desktop</string>
    <key>ProgramArguments</key>
    <array>
        <string>{exePath}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
</dict>
</plist>";
                    File.WriteAllText(plistPath, plistContent, Encoding.UTF8);
                }
                else if (File.Exists(plistPath))
                {
                    File.Delete(plistPath);
                }
                return true;
            }
            else if (OperatingSystem.IsLinux())
            {
                string autostartDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "autostart");
                string desktopPath = Path.Combine(autostartDir, LinuxDesktopName);

                if (enable)
                {
                    Directory.CreateDirectory(autostartDir);
                    string desktopContent = $@"[Desktop Entry]
Type=Application
Exec={exePath}
Hidden=false
NoDisplay=false
X-GNOME-Autostart-enabled=true
Name=QuotaTray
Comment=AI Quota Tray Monitor";
                    File.WriteAllText(desktopPath, desktopContent, Encoding.UTF8);
                }
                else if (File.Exists(desktopPath))
                {
                    File.Delete(desktopPath);
                }
                return true;
            }
        }
        catch { }

        return false;
    }
}
