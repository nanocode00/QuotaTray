using System;
using System.Collections.Generic;
using System.Text;
using QuotaTray.Core.Models;

namespace QuotaTray.Core.Utils;

public static class QuotaDiagnostics
{
    public static string GenerateReport(IEnumerable<ProviderQuotaResult> results, int refreshInterval, int warningThreshold)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# QuotaTray Diagnostics Report");
        sb.AppendLine($"Generated At: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($".NET Runtime: {Environment.Version}");
        sb.AppendLine($"Refresh Interval: {refreshInterval} min | Warning Threshold: {warningThreshold}%");
        sb.AppendLine();
        sb.AppendLine("## Provider Status");
        sb.AppendLine("| Provider | Status | Auth Method | Quota / Balance | Next Reset | Error |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var r in results)
        {
            string statusIcon = r.AuthStatus switch
            {
                ProviderAuthStatus.Connected => "🟢 Connected",
                ProviderAuthStatus.NotConfigured => "⚪ Not Configured",
                ProviderAuthStatus.Expired => "🟡 Expired",
                ProviderAuthStatus.Error => "🔴 Error",
                _ => "⚪ Unknown"
            };

            string quotaInfo = r.IsBalanceProvider
                ? (r.BalanceFormatted ?? "$0.00")
                : $"{r.PrimaryRemainingPercent:F1}%";

            string reset = !string.IsNullOrEmpty(r.FormattedNextResetIn) ? r.FormattedNextResetIn : "-";
            string err = !string.IsNullOrEmpty(r.ErrorMessage) ? r.ErrorMessage.Replace("|", "/") : "-";

            sb.AppendLine($"| {r.ProviderTitle} | {statusIcon} | {r.AuthMethod} | {quotaInfo} | {reset} | {err} |");
        }

        sb.AppendLine();
        sb.AppendLine("> *Note: For security reasons, tokens and API keys are strictly excluded from diagnostics.*");
        return sb.ToString();
    }
}
