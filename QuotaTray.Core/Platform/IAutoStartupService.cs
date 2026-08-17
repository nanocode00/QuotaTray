using System;

namespace QuotaTray.Core.Platform;

public interface IAutoStartupService
{
    bool IsAutoStartupEnabled();
    bool SetAutoStartup(bool enable, string? executablePath = null);
}
