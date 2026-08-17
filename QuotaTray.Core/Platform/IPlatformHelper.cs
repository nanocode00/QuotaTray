using System;

namespace QuotaTray.Core.Platform;

public interface IPlatformHelper
{
    void OpenBrowser(string url);
    void LaunchTerminal(string command);
}
