using System;
using System.Diagnostics;

namespace QuotaTray.Core.Platform;

public class CrossPlatformHelper : IPlatformHelper
{
    public void OpenBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
        }
        catch { }
    }

    public void LaunchTerminal(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k {command}",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                string script = $"tell application \"Terminal\" to do script \"{command}\"";
                Process.Start("osascript", $"-e \"{script}\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                try
                {
                    Process.Start("x-terminal-emulator", $"-e bash -c \"{command}; exec bash\"");
                }
                catch
                {
                    Process.Start("gnome-terminal", $"-- bash -c \"{command}; exec bash\"");
                }
            }
        }
        catch { }
    }
}
