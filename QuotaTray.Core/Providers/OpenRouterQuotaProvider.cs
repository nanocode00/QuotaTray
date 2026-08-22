using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;
using QuotaTray.Core.Utils;

namespace QuotaTray.Core.Providers;

public class OpenRouterQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "openrouter";
    public string ProviderTitle => "OpenRouter";
    public string IconLetter => "OR";
    public string IconColorHex => "#C8FF00";
    public string IconBgColorHex => "#232D00";
    public string AuthMethod => "API Key";
    public string? CliLoginCommand => null;
    public bool RequiresApiKey => true;

    private const string CreditsUrl = "https://openrouter.ai/api/v1/credits";
    private const string KeyUrl = "https://openrouter.ai/api/v1/key";

    private readonly HttpClient _httpClient;
    private string? _customApiKey;

    public OpenRouterQuotaProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void SetCustomApiKey(string? apiKey)
    {
        _customApiKey = apiKey;
    }

    public async Task<ProviderQuotaResult> FetchQuotaAsync(CancellationToken cancellationToken = default)
    {
        var result = new ProviderQuotaResult
        {
            ProviderKey = ProviderKey,
            ProviderTitle = ProviderTitle,
            IconLetter = IconLetter,
            IconColorHex = IconColorHex,
            IconBgColorHex = IconBgColorHex,
            AuthMethod = AuthMethod,
            CliLoginCommand = CliLoginCommand,
            RequiresApiKey = RequiresApiKey,
            IsBalanceProvider = true,
            FetchedAt = DateTimeOffset.UtcNow
        };

        try
        {
            string? key = !string.IsNullOrWhiteSpace(_customApiKey)
                ? _customApiKey
                : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

            if (string.IsNullOrWhiteSpace(key))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = "API Key not set. Enter OpenRouter API Key in Settings";
                return result;
            }

            // /key is the authoritative source for the current API key's tier, usage,
            // spending limit, reset cadence, and expiry. If this endpoint fails, the key
            // itself cannot be considered healthy even if account credits are available.
            using var keyRequest = CreateAuthorizedGet(KeyUrl, key);
            using var keyResponse = await _httpClient.SendAsync(keyRequest, cancellationToken);

            if (!keyResponse.IsSuccessStatusCode)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = keyResponse.StatusCode == HttpStatusCode.Unauthorized;
                result.AuthStatus = keyResponse.StatusCode == HttpStatusCode.Unauthorized
                    ? ProviderAuthStatus.Expired
                    : ProviderAuthStatus.Error;
                result.ErrorMessage = $"API Error ({(int)keyResponse.StatusCode})";
                return result;
            }

            string keyJson = await keyResponse.Content.ReadAsStringAsync(cancellationToken);
            var keyInfo = ParseKeyInfo(keyJson);

            result.PlanType = keyInfo.IsFreeTier switch
            {
                true => "Free",
                false => "PAYG",
                _ => "OpenRouter"
            };

            // Account credits are useful when available, but are not required for a valid
            // OpenRouter API key. Treat this as an optional enrichment so users whose keys
            // cannot access /credits still get usage and key-limit information.
            var creditsInfo = await TryFetchCreditsAsync(key, cancellationToken);
            if (creditsInfo != null)
            {
                result.BalanceAmount = creditsInfo.Balance;
                result.BalanceCurrency = "$";
                // Keep the header chip compact; detailed rows preserve sub-cent precision.
                result.BalanceFormatted = $"${creditsInfo.Balance:F2}";
            }
            else
            {
                // The existing balance-provider card can still represent a healthy key;
                // avoid showing a false $0.00 when account credits are simply unavailable.
                result.BalanceFormatted = "Active";
            }

            string monthlyUsage = keyInfo.UsageMonthly.HasValue
                ? $"Month {FormatUsd(keyInfo.UsageMonthly.Value)} used"
                : keyInfo.Usage.HasValue
                    ? $"Usage {FormatUsd(keyInfo.Usage.Value)}"
                    : "Usage unavailable";

            var windows = new List<QuotaWindow>();

            if (creditsInfo != null)
            {
                double balancePct = creditsInfo.TotalCredits > 0
                    ? ClampPercent((creditsInfo.Balance / creditsInfo.TotalCredits) * 100.0)
                    : creditsInfo.Balance > 0 ? 100.0 : 0.0;

                windows.Add(new QuotaWindow
                {
                    Name = creditsInfo.TotalCredits > 0
                        ? $"Credits ({FormatUsdDetailed(creditsInfo.Balance)} / {FormatUsd(creditsInfo.TotalCredits)})"
                        : $"Credits ({FormatUsdDetailed(creditsInfo.Balance)})",
                    UsedPercent = ClampPercent(100.0 - balancePct),
                    RemainingPercent = balancePct,
                    // Keep monthly usage on the quota row so the entitlement text can use
                    // the full subtitle width instead of being squeezed into one line.
                    FormattedResetIn = monthlyUsage
                });

                result.PrimaryRemainingPercent = balancePct;
                result.ResetText = monthlyUsage;
                result.FormattedNextResetIn = monthlyUsage;
            }

            // A spending limit is a real quota and can be represented as a progress bar.
            // Free-model request entitlements are policy information, not a live counter,
            // so they are deliberately not added as synthetic 100% windows.
            if (keyInfo.Limit is > 0)
            {
                double usage = keyInfo.Usage ?? 0.0;
                double remaining = keyInfo.LimitRemaining
                    ?? Math.Max(0.0, keyInfo.Limit.Value - usage);
                double remainingPct = ClampPercent((remaining / keyInfo.Limit.Value) * 100.0);

                windows.Add(new QuotaWindow
                {
                    Name = $"Key limit (${remaining:F2} / ${keyInfo.Limit.Value:F2})",
                    UsedPercent = ClampPercent(100.0 - remainingPct),
                    RemainingPercent = remainingPct,
                    FormattedResetIn = FormatLimitReset(keyInfo.LimitReset, usage)
                });

                if (creditsInfo == null)
                {
                    result.PrimaryRemainingPercent = remainingPct;
                    result.ResetText = windows[^1].FormattedResetIn;
                    result.FormattedNextResetIn = windows[^1].FormattedResetIn;
                }
            }

            result.Windows = windows;

            string freeModelAllowance = keyInfo.IsFreeTier switch
            {
                true => "Free 50/day · 20 RPM",
                false => "Free 1k/day · 20 RPM",
                _ => "Free allowance unavailable"
            };

            // Entitlement/policy information gets its own line. Monthly usage is shown on
            // the credits row when available, avoiding a crowded and truncated subtitle.
            result.DetailsSubtitle = freeModelAllowance;

            if (creditsInfo == null && keyInfo.Limit is not > 0)
            {
                // There is no percentage-based quota to summarize. Keep the neutral 100
                // internally; the desktop card displays the healthy balance-style state as Active.
                result.PrimaryRemainingPercent = 100.0;
                result.ResetText = keyInfo.Usage.HasValue ? $"{FormatUsd(keyInfo.Usage.Value)} used" : "Active";
                result.FormattedNextResetIn = result.ResetText;
            }

            result.AuthStatus = ProviderAuthStatus.Connected;
            result.IsSuccess = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = "Request timed out";
            return result;
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private HttpRequestMessage CreateAuthorizedGet(string url, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {key}");
        return request;
    }

    private async Task<OpenRouterCreditsInfo?> TryFetchCreditsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateAuthorizedGet(CreditsUrl, key);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseCreditsInfo(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static OpenRouterKeyInfo ParseKeyInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new OpenRouterKeyInfo();
        }

        return new OpenRouterKeyInfo
        {
            IsFreeTier = TryGetBoolean(data, "is_free_tier"),
            Limit = TryGetDouble(data, "limit"),
            LimitRemaining = TryGetDouble(data, "limit_remaining"),
            LimitReset = TryGetString(data, "limit_reset"),
            Usage = TryGetDouble(data, "usage"),
            UsageDaily = TryGetDouble(data, "usage_daily"),
            UsageWeekly = TryGetDouble(data, "usage_weekly"),
            UsageMonthly = TryGetDouble(data, "usage_monthly"),
            ExpiresAt = TryGetString(data, "expires_at")
        };
    }

    private static OpenRouterCreditsInfo? ParseCreditsInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? totalCredits = TryGetDouble(data, "total_credits");
        double? totalUsage = TryGetDouble(data, "total_usage");
        if (!totalCredits.HasValue && !totalUsage.HasValue)
        {
            return null;
        }

        double credits = Math.Max(0.0, totalCredits ?? 0.0);
        double usage = Math.Max(0.0, totalUsage ?? 0.0);
        return new OpenRouterCreditsInfo
        {
            TotalCredits = credits,
            TotalUsage = usage,
            Balance = Math.Max(0.0, credits - usage)
        };
    }

    private static string FormatLimitReset(string? reset, double usage)
    {
        if (string.IsNullOrWhiteSpace(reset))
        {
            return $"Used ${usage:F2}";
        }

        string normalized = reset.Trim().ToLowerInvariant();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? nextReset = normalized switch
        {
            "daily" => new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero),
            "weekly" => NextWeeklyReset(now),
            "monthly" => new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
            _ => null
        };

        if (nextReset.HasValue)
        {
            long seconds = Math.Max(0, (long)(nextReset.Value - now).TotalSeconds);
            return TimeFormatter.FormatResetText(seconds);
        }

        if (DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
        {
            long seconds = Math.Max(0, (long)(parsedReset - now).TotalSeconds);
            return TimeFormatter.FormatResetText(seconds);
        }

        return $"Reset {reset}";
    }

    private static DateTimeOffset NextWeeklyReset(DateTimeOffset now)
    {
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        return new DateTimeOffset(now.UtcDateTime.Date.AddDays(daysUntilMonday), TimeSpan.Zero);
    }

    private static double ClampPercent(double value) => Math.Max(0.0, Math.Min(100.0, value));

    private static string FormatUsd(double value)
    {
        if (value > 0 && value < 0.01)
        {
            return $"${value:F4}";
        }
        return $"${value:F2}";
    }

    private static string FormatUsdDetailed(double value)
    {
        if (value == 0)
        {
            return "$0.00";
        }

        // Keep up to four decimals when cents would hide a real difference, e.g.
        // $9.9976 remaining or $0.0024 used, while normal balances stay compact.
        double roundedCents = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - roundedCents) >= 0.00005)
        {
            return $"${value:F4}";
        }

        return $"${value:F2}";
    }

    private static double? TryGetDouble(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out double value))
        {
            return value;
        }
        return null;
    }

    private static bool? TryGetBoolean(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }
        return null;
    }

    private sealed class OpenRouterKeyInfo
    {
        public bool? IsFreeTier { get; init; }
        public double? Limit { get; init; }
        public double? LimitRemaining { get; init; }
        public string? LimitReset { get; init; }
        public double? Usage { get; init; }
        public double? UsageDaily { get; init; }
        public double? UsageWeekly { get; init; }
        public double? UsageMonthly { get; init; }
        public string? ExpiresAt { get; init; }
    }

    private sealed class OpenRouterCreditsInfo
    {
        public double TotalCredits { get; init; }
        public double TotalUsage { get; init; }
        public double Balance { get; init; }
    }
}
