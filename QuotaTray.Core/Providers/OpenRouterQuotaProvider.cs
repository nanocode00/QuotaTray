using System;
using System.Collections.Generic;
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
    private const string AuthKeyUrl = "https://openrouter.ai/api/v1/auth/key";
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
            string? key = !string.IsNullOrEmpty(_customApiKey)
                ? _customApiKey
                : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

            if (string.IsNullOrEmpty(key))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = "API Key not set. Enter OpenRouter API Key in Settings";
                return result;
            }

            using var reqCredits = new HttpRequestMessage(HttpMethod.Get, CreditsUrl);
            reqCredits.Headers.Add("Authorization", $"Bearer {key}");

            var respCredits = await _httpClient.SendAsync(reqCredits, cancellationToken);
            if (!respCredits.IsSuccessStatusCode)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = respCredits.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                result.AuthStatus = respCredits.StatusCode == System.Net.HttpStatusCode.Unauthorized ? ProviderAuthStatus.Expired : ProviderAuthStatus.Error;
                result.ErrorMessage = $"API Error ({(int)respCredits.StatusCode})";
                return result;
            }

            string jsonCredits = await respCredits.Content.ReadAsStringAsync(cancellationToken);
            using var docCredits = JsonDocument.Parse(jsonCredits);
            var rootCredits = docCredits.RootElement;

            double totalCredits = 0;
            double totalUsage = 0;
            double balance = 0;

            if (rootCredits.TryGetProperty("data", out var dataProp))
            {
                totalCredits = dataProp.TryGetProperty("total_credits", out var tc) ? tc.GetDouble() : 0.0;
                totalUsage = dataProp.TryGetProperty("total_usage", out var tu) ? tu.GetDouble() : 0.0;
                balance = Math.Max(0.0, totalCredits - totalUsage);

                result.BalanceAmount = balance;
                result.BalanceCurrency = "$";
                result.BalanceFormatted = $"${balance:F2}";
            }

            // Optional: Query /auth/key or /key for tier and key limit details
            bool isFreeTier = totalCredits < 10.0;
            double? keyLimit = null;
            double? keyUsage = null;
            double? keyLimitRemaining = null;
            string? keyResetStr = null;

            try
            {
                using var reqAuth = new HttpRequestMessage(HttpMethod.Get, AuthKeyUrl);
                reqAuth.Headers.Add("Authorization", $"Bearer {key}");
                var respAuth = await _httpClient.SendAsync(reqAuth, cancellationToken);
                if (respAuth.IsSuccessStatusCode)
                {
                    string jsonAuth = await respAuth.Content.ReadAsStringAsync(cancellationToken);
                    using var docAuth = JsonDocument.Parse(jsonAuth);
                    if (docAuth.RootElement.TryGetProperty("data", out var authData))
                    {
                        if (authData.TryGetProperty("is_free_tier", out var ft))
                        {
                            isFreeTier = ft.GetBoolean();
                        }
                        if (authData.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number)
                        {
                            keyLimit = lim.GetDouble();
                        }
                        if (authData.TryGetProperty("usage", out var usg) && usg.ValueKind == JsonValueKind.Number)
                        {
                            keyUsage = usg.GetDouble();
                        }
                        if (authData.TryGetProperty("limit_remaining", out var rem) && rem.ValueKind == JsonValueKind.Number)
                        {
                            keyLimitRemaining = rem.GetDouble();
                        }
                        if (authData.TryGetProperty("limit_reset", out var lReset) && lReset.ValueKind == JsonValueKind.String)
                        {
                            if (DateTimeOffset.TryParse(lReset.GetString(), out var resetDto))
                            {
                                long diff = Math.Max(0, (long)(resetDto - DateTimeOffset.UtcNow).TotalSeconds);
                                keyResetStr = TimeFormatter.FormatResetText(diff);
                            }
                        }
                    }
                }
            }
            catch { }

            // Badge: Free or PAYG
            bool isPayg = !isFreeTier || totalCredits >= 10.0;
            result.PlanType = isPayg ? "PAYG" : "Free";
            result.DetailsSubtitle = $"Usage: ${totalUsage:F2} · Total: ${totalCredits:F2}";

            // Construct Windows (Progress Bars & Entitlements)
            var windows = new List<QuotaWindow>();

            // 1. Account Credits Window
            double balancePct = totalCredits > 0 ? Math.Max(0.0, Math.Min(100.0, (balance / totalCredits) * 100.0)) : (balance > 0 ? 100.0 : 0.0);
            windows.Add(new QuotaWindow
            {
                Name = totalCredits > 0 ? $"Credits (${balance:F2} / ${totalCredits:F2})" : $"Credits (${balance:F2})",
                UsedPercent = Math.Max(0.0, 100.0 - balancePct),
                RemainingPercent = balancePct,
                FormattedResetIn = $"Used ${totalUsage:F2}"
            });

            // 2. Free Models Entitlement (50/day or 1,000/day)
            string freeModelsDaily = isPayg ? "1,000/day" : "50/day";
            windows.Add(new QuotaWindow
            {
                Name = $"Free models: {freeModelsDaily}",
                UsedPercent = 0.0,
                RemainingPercent = 100.0,
                FormattedResetIn = isPayg ? "누적 $10+ 계정" : "기본 무료 한도"
            });

            // 3. Key Limit Window (if key has spending limit)
            if (keyLimit.HasValue && keyLimit.Value > 0)
            {
                double ku = keyUsage ?? 0.0;
                double remKey = keyLimitRemaining ?? Math.Max(0.0, keyLimit.Value - ku);
                double remPct = Math.Max(0.0, Math.Min(100.0, (remKey / keyLimit.Value) * 100.0));
                windows.Add(new QuotaWindow
                {
                    Name = $"Key limit (${remKey:F2} / ${keyLimit.Value:F2})",
                    UsedPercent = Math.Max(0.0, 100.0 - remPct),
                    RemainingPercent = remPct,
                    FormattedResetIn = !string.IsNullOrEmpty(keyResetStr) ? keyResetStr : $"Used ${ku:F2}"
                });
            }

            result.Windows = windows;

            result.PrimaryRemainingPercent = balancePct;
            result.ResetText = $"${totalUsage:F2} used";
            result.FormattedNextResetIn = result.ResetText;

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
}
