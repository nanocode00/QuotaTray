using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;

namespace QuotaTray.Core.Providers;

/// <summary>
/// Final accuracy gate for provider results.
/// QuotaTray should prefer an unavailable/last-known-good state over displaying a
/// percentage or entitlement that was inferred from missing server data.
/// </summary>
public sealed class QuotaAccuracyGuardProvider : IQuotaProvider, IManagementKeyProvider
{
    private readonly IQuotaProvider _inner;

    public QuotaAccuracyGuardProvider(IQuotaProvider inner)
    {
        _inner = inner;
    }

    public string ProviderKey => _inner.ProviderKey;
    public string ProviderTitle => _inner.ProviderTitle;
    public string IconLetter => _inner.IconLetter;
    public string IconColorHex => _inner.IconColorHex;
    public string IconBgColorHex => _inner.IconBgColorHex;
    public string AuthMethod => _inner.AuthMethod;
    public string? CliLoginCommand => _inner.CliLoginCommand;
    public bool RequiresApiKey => _inner.RequiresApiKey;

    public void SetCustomApiKey(string? apiKey) => _inner.SetCustomApiKey(apiKey);

    public void SetManagementKey(string? managementKey)
    {
        if (_inner is IManagementKeyProvider managementKeyProvider)
        {
            managementKeyProvider.SetManagementKey(managementKey);
        }
    }

    public async Task<ProviderQuotaResult> FetchQuotaAsync(CancellationToken cancellationToken = default)
    {
        ProviderQuotaResult result = await _inner.FetchQuotaAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        switch (ProviderKey.ToLowerInvariant())
        {
            case "antigravity":
                GuardAntigravity(result);
                break;
            case "codex":
                GuardCodex(result);
                break;
            case "openrouter":
                GuardOpenRouter(result);
                break;
        }

        return result;
    }

    private static void GuardAntigravity(ProviderQuotaResult result)
    {
        // Antigravity's provider historically inserted two 100%/Available groups when
        // the real response contained no parseable quota data. Those synthetic groups
        // have no window length, which lets us distinguish them from real server buckets.
        bool isSyntheticFallback = result.Groups.Count > 0
            && result.Groups.SelectMany(group => group.Windows).Any()
            && result.Groups.SelectMany(group => group.Windows).All(window =>
                window.LimitWindowSeconds == 0
                && Math.Abs(window.RemainingPercent - 100.0) < 0.001
                && window.FormattedResetIn.Equals("Available", StringComparison.OrdinalIgnoreCase));

        if (isSyntheticFallback)
        {
            MarkQuotaUnavailable(result, "Antigravity returned no verified quota buckets.");
            return;
        }

        // Keep the known model-roster metadata used to explain each Antigravity pool.
        // It is descriptive metadata, not a fabricated quota value. Accuracy hardening
        // only filters unverified quota buckets and never turns this list into usage data.
        result.Groups = result.Groups
            .Where(group => group.Windows.Count > 0)
            .ToList();
        result.Windows = result.Groups.SelectMany(group => group.Windows).ToList();

        if (result.Groups.Count == 0)
        {
            MarkQuotaUnavailable(result, "Antigravity returned no verified quota buckets.");
        }
    }

    private static void GuardCodex(ProviderQuotaResult result)
    {
        // A successful HTTP response without any shared or additional quota bucket does
        // not mean 100% remains. Treat it as unavailable so QuotaService can use its
        // last-known-good cache instead of showing a fabricated Available state.
        if (result.Windows.Count == 0 && result.Groups.Count == 0)
        {
            MarkQuotaUnavailable(result, "Codex returned no quota windows.");
        }
    }

    private static void GuardOpenRouter(ProviderQuotaResult result)
    {
        QuotaWindow? freeWindow = result.Windows.FirstOrDefault(window =>
            window.Name.StartsWith("Free models (", StringComparison.OrdinalIgnoreCase));

        long? usedUtcToday = freeWindow == null ? null : TryParseFreeRequestsUsed(freeWindow.Name);
        int? confirmedPolicyCap = TryResolveConfirmedOpenRouterFreeCap(result);

        if (freeWindow != null && usedUtcToday.HasValue && confirmedPolicyCap.HasValue)
        {
            // Analytics gives the measured UTC-calendar-day request count, while the
            // published account policy gives the 50/1,000 daily entitlement. Restore the
            // useful daily quota row, but do not invent a reset countdown because the
            // exact free-model reset boundary is not documented here.
            long remaining = Math.Max(0, confirmedPolicyCap.Value - usedUtcToday.Value);
            double remainingPercent = Math.Clamp(
                remaining / (double)confirmedPolicyCap.Value * 100.0,
                0.0,
                100.0);

            freeWindow.Name = $"Free models ({usedUtcToday.Value:N0} / {confirmedPolicyCap.Value:N0} today)";
            freeWindow.RemainingPercent = remainingPercent;
            freeWindow.UsedPercent = Math.Clamp(100.0 - remainingPercent, 0.0, 100.0);
            freeWindow.ResetInSeconds = 0;
            freeWindow.FormattedResetIn = "20 RPM";
            result.DetailsSubtitle = "Free models · UTC today · 20 RPM";
            return;
        }

        if (freeWindow != null)
        {
            // Keep measured usage visible even if this account's daily entitlement cannot
            // be proven. Without a denominator, a progress bar would be misleading.
            result.Windows.Remove(freeWindow);
            result.DetailsSubtitle = usedUtcToday.HasValue
                ? $"Free models · {usedUtcToday.Value:N0} requests UTC today · 20 RPM · daily cap unknown"
                : "Free models · 20 RPM · daily cap unknown";
            return;
        }

        // When analytics is not configured, make it explicit that the number is policy
        // information rather than a measured per-account remaining quota.
        if (confirmedPolicyCap.HasValue)
        {
            result.DetailsSubtitle = $"Policy · Free {confirmedPolicyCap.Value:N0}/day · 20 RPM";
        }
        else if (result.DetailsSubtitle.StartsWith("Free ", StringComparison.OrdinalIgnoreCase))
        {
            result.DetailsSubtitle = "Free models · 20 RPM · daily cap unknown";
        }
    }

    private static int? TryResolveConfirmedOpenRouterFreeCap(ProviderQuotaResult result)
    {
        double? totalCredits = TryParseTotalCredits(result);
        if (totalCredits.HasValue)
        {
            // OpenRouter documents total_credits as total credits purchased. Its free-model
            // policy raises the daily cap from 50 to 1,000 after at least $10 is purchased.
            return totalCredits.Value >= 10.0 ? 1000 : 50;
        }

        if (string.Equals(result.PlanType, "Free", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return null;
    }

    private static double? TryParseTotalCredits(ProviderQuotaResult result)
    {
        QuotaWindow? credits = result.Windows.FirstOrDefault(window =>
            window.Name.StartsWith("Credits (", StringComparison.OrdinalIgnoreCase));
        if (credits == null)
        {
            return null;
        }

        int slash = credits.Name.LastIndexOf(" / $", StringComparison.Ordinal);
        int close = credits.Name.LastIndexOf(')');
        if (slash < 0 || close <= slash + 4)
        {
            return null;
        }

        string value = credits.Name[(slash + 4)..close].Replace(",", string.Empty, StringComparison.Ordinal);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static long? TryParseFreeRequestsUsed(string name)
    {
        int open = name.IndexOf('(');
        int slash = name.IndexOf(" / ", StringComparison.Ordinal);
        if (open < 0 || slash <= open + 1)
        {
            return null;
        }

        string value = name[(open + 1)..slash].Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? Math.Max(0, parsed)
            : null;
    }

    private static void MarkQuotaUnavailable(ProviderQuotaResult result, string message)
    {
        result.IsSuccess = false;
        result.IsAuthMissing = false;
        result.AuthStatus = ProviderAuthStatus.Error;
        result.ErrorMessage = message;
        result.Groups.Clear();
        result.Windows.Clear();
        result.Models.Clear();
        result.TightestModel = null;
        result.PrimaryRemainingPercent = 0.0;
        result.NextResetInSeconds = 0;
        result.FormattedNextResetIn = "Unavailable";
        result.ResetText = "Unavailable";
    }
}
