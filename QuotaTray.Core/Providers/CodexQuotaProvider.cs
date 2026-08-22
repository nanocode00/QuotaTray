using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;
using QuotaTray.Core.Utils;

namespace QuotaTray.Core.Providers;

public class CodexQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "codex";
    public string ProviderTitle => "Codex";
    public string IconLetter => "C";
    public string IconColorHex => "#10A37F";
    public string IconBgColorHex => "#0F372E";
    public string AuthMethod => "OAuth (ChatGPT)";
    public string? CliLoginCommand => "codex login";
    public bool RequiresApiKey => false;

    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string TokenRefreshUrl = "https://auth.openai.com/oauth/token";
    private const string OauthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    private readonly HttpClient _httpClient;
    private string? _customApiKey;
    private string? _cachedAccessToken;
    private string? _cachedRefreshToken;
    private string? _cachedAccountId;

    public CodexQuotaProvider(HttpClient? httpClient = null)
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
            FetchedAt = DateTimeOffset.UtcNow
        };

        // Load credentials if not loaded
        LoadCredentials();

        if (string.IsNullOrEmpty(_cachedAccessToken))
        {
            result.IsSuccess = false;
            result.IsAuthMissing = true;
            result.AuthStatus = ProviderAuthStatus.NotConfigured;
            result.ErrorMessage = "No credentials found. Run 'codex login'";
            return result;
        }

        try
        {
            var (statusCode, jsonDoc) = await CallUsageApiAsync(_cachedAccessToken, _cachedAccountId, cancellationToken);

            if (statusCode == HttpStatusCode.Unauthorized)
            {
                bool refreshed = await TryRefreshTokenAsync(cancellationToken);
                if (refreshed && !string.IsNullOrEmpty(_cachedAccessToken))
                {
                    (statusCode, jsonDoc) = await CallUsageApiAsync(_cachedAccessToken, _cachedAccountId, cancellationToken);
                }
            }

            if (statusCode != HttpStatusCode.OK || jsonDoc == null)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = statusCode == HttpStatusCode.Unauthorized;
                result.AuthStatus = statusCode == HttpStatusCode.Unauthorized ? ProviderAuthStatus.Expired : ProviderAuthStatus.Error;
                result.ErrorMessage = statusCode == HttpStatusCode.Unauthorized
                    ? "Authentication expired. Run 'codex login'"
                    : $"API Error ({(int)statusCode})";
                return result;
            }

            using (jsonDoc)
            {
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("plan_type", out var planProp))
                {
                    result.PlanType = FormatPlanType(planProp.GetString());
                }

                if (string.IsNullOrEmpty(result.PlanType) && !string.IsNullOrEmpty(_cachedAccessToken))
                {
                    result.PlanType = FormatPlanType(ExtractPlanFromJwt(_cachedAccessToken));
                }

                result.DetailsSubtitle = !string.IsNullOrEmpty(result.AccountEmail) ? result.AccountEmail : "ChatGPT";

                var windows = new List<QuotaWindow>();

                if (root.TryGetProperty("rate_limit", out var rateLimitProp) && rateLimitProp.ValueKind == JsonValueKind.Object)
                {
                    ParseRateLimitWindow(rateLimitProp, "primary_window", windows);
                    ParseRateLimitWindow(rateLimitProp, "secondary_window", windows);
                }

                // Do not synthesize missing 5h/7d windows. OpenAI may expose only the
                // windows that are active for a given plan/account at that moment.
                windows.Sort((a, b) => a.LimitWindowSeconds.CompareTo(b.LimitWindowSeconds));
                result.Windows = windows;

                // Model/feature-specific quotas (for example Codex Spark) are returned
                // separately from the account-wide Codex quota. Parse them generically
                // instead of keying behavior to a specific plan name so Plus/Pro/Business
                // and future plan variants continue to behave according to the server payload.
                var additionalGroups = ParseAdditionalRateLimitGroups(root);
                if (additionalGroups.Count > 0)
                {
                    result.Groups = new List<QuotaGroup>();

                    // Detailed mode switches to grouped rendering when Groups is non-empty,
                    // so include the shared Codex windows as the first group as well.
                    if (windows.Count > 0)
                    {
                        result.Groups.Add(new QuotaGroup
                        {
                            GroupId = "codex-shared",
                            GroupName = "Codex",
                            ModelsList = new List<string> { "Shared Codex allowance" },
                            Windows = new List<QuotaWindow>(windows)
                        });
                    }

                    result.Groups.AddRange(additionalGroups);
                }

                // Compact card: keep the account-wide Codex quota as the representative
                // value. Prefer the longest window returned by the server (normally weekly).
                var representativeWindow = windows.OrderByDescending(w => w.LimitWindowSeconds).FirstOrDefault()
                                         ?? windows.FirstOrDefault();

                if (representativeWindow != null)
                {
                    result.PrimaryRemainingPercent = representativeWindow.RemainingPercent;
                    result.NextResetInSeconds = representativeWindow.ResetInSeconds;
                    result.FormattedNextResetIn = representativeWindow.FormattedResetIn;
                    result.ResetText = representativeWindow.FormattedResetIn;
                }
                else
                {
                    result.PrimaryRemainingPercent = 100.0;
                    result.ResetText = "Available";
                }

                result.AuthStatus = ProviderAuthStatus.Connected;
                result.IsSuccess = true;
                return result;
            }
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

    private void LoadCredentials()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] searchPaths =
        {
            Path.Combine(userProfile, ".codex", "auth.json"),
            Path.Combine(userProfile, ".config", "codex", "auth.json")
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    string content = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("tokens", out var tokensProp) && tokensProp.ValueKind == JsonValueKind.Object)
                    {
                        if (tokensProp.TryGetProperty("access_token", out var at)) _cachedAccessToken = at.GetString();
                        if (tokensProp.TryGetProperty("refresh_token", out var rt)) _cachedRefreshToken = rt.GetString();
                        if (tokensProp.TryGetProperty("account_id", out var acc)) _cachedAccountId = acc.GetString();
                    }

                    if (!string.IsNullOrEmpty(_cachedAccessToken)) return;
                }
                catch
                {
                    // Ignore parse error
                }
            }
        }
    }

    private async Task<(HttpStatusCode, JsonDocument?)> CallUsageApiAsync(string accessToken, string? accountId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        if (!string.IsNullOrEmpty(accountId))
        {
            request.Headers.Add("chatgpt-account-id", accountId);
        }
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        string json = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, JsonDocument.Parse(json));
    }

    private async Task<bool> TryRefreshTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_cachedRefreshToken)) return false;

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "client_id", OauthClientId },
                { "refresh_token", _cachedRefreshToken }
            });

            using var response = await _httpClient.PostAsync(TokenRefreshUrl, content, ct);
            if (!response.IsSuccessStatusCode) return false;

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var atProp))
            {
                _cachedAccessToken = atProp.GetString();
            }
            if (root.TryGetProperty("refresh_token", out var rtProp))
            {
                _cachedRefreshToken = rtProp.GetString();
            }

            return !string.IsNullOrEmpty(_cachedAccessToken);
        }
        catch
        {
            return false;
        }
    }

    private static List<QuotaGroup> ParseAdditionalRateLimitGroups(JsonElement root)
    {
        var groups = new List<QuotaGroup>();

        if (!root.TryGetProperty("additional_rate_limits", out var additionalProp) ||
            additionalProp.ValueKind != JsonValueKind.Array)
        {
            return groups;
        }

        int fallbackIndex = 0;
        foreach (var item in additionalProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? limitName = null;
            string? meteredFeature = null;

            if (item.TryGetProperty("limit_name", out var limitNameProp) && limitNameProp.ValueKind == JsonValueKind.String)
            {
                limitName = limitNameProp.GetString();
            }

            if (item.TryGetProperty("metered_feature", out var featureProp) && featureProp.ValueKind == JsonValueKind.String)
            {
                meteredFeature = featureProp.GetString();
            }

            if (!item.TryGetProperty("rate_limit", out var rateLimitProp) || rateLimitProp.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var groupWindows = new List<QuotaWindow>();
            ParseRateLimitWindow(rateLimitProp, "primary_window", groupWindows, markUnusedAsAvailable: true);
            ParseRateLimitWindow(rateLimitProp, "secondary_window", groupWindows, markUnusedAsAvailable: true);

            if (groupWindows.Count == 0)
            {
                continue;
            }

            groupWindows.Sort((a, b) => a.LimitWindowSeconds.CompareTo(b.LimitWindowSeconds));

            string groupName = !string.IsNullOrWhiteSpace(limitName)
                ? limitName!
                : !string.IsNullOrWhiteSpace(meteredFeature)
                    ? meteredFeature!
                    : $"Additional quota {fallbackIndex + 1}";

            string groupId = !string.IsNullOrWhiteSpace(meteredFeature)
                ? meteredFeature!
                : $"codex-additional-{fallbackIndex}";

            var models = new List<string>();
            if (!string.IsNullOrWhiteSpace(limitName))
            {
                models.Add(limitName!);
            }

            groups.Add(new QuotaGroup
            {
                GroupId = groupId,
                GroupName = groupName,
                ModelsList = models,
                Windows = groupWindows
            });

            fallbackIndex++;
        }

        return groups;
    }

    private static void ParseRateLimitWindow(
        JsonElement rateLimitProp,
        string propertyName,
        List<QuotaWindow> list,
        bool markUnusedAsAvailable = false)
    {
        if (!rateLimitProp.TryGetProperty(propertyName, out var windowProp) || windowProp.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        long windowSecs = 0;
        if (windowProp.TryGetProperty("limit_window_seconds", out var winSecProp) && winSecProp.TryGetInt64(out var ws))
        {
            windowSecs = ws;
        }

        double usedPercent = 0;
        if (windowProp.TryGetProperty("used_percent", out var usedProp) && usedProp.TryGetDouble(out var up))
        {
            usedPercent = up;
        }

        long resetAfterSeconds = 0;
        bool hasResetAfter = false;
        if (windowProp.TryGetProperty("reset_after_seconds", out var resetProp) && resetProp.TryGetInt64(out var rs))
        {
            resetAfterSeconds = Math.Max(0, rs);
            hasResetAfter = true;
        }

        // Some WHAM responses expose reset_at without reset_after_seconds.
        if (!hasResetAfter && windowProp.TryGetProperty("reset_at", out var resetAtProp) && resetAtProp.TryGetInt64(out var resetAt))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            resetAfterSeconds = Math.Max(0, resetAt - now);
        }

        string windowName;
        if (windowSecs > 0 && windowSecs <= 86400)
        {
            windowName = $"{Math.Max(1, windowSecs / 3600)}h";
        }
        else if (windowSecs > 86400)
        {
            windowName = $"{Math.Max(1, windowSecs / 86400)}d";
        }
        else
        {
            windowName = propertyName.Contains("primary", StringComparison.OrdinalIgnoreCase) ? "Primary" : "Secondary";
        }

        double remainingPercent = Math.Max(0.0, Math.Min(100.0, 100.0 - usedPercent));

        // A never-used feature bucket can be returned with a full-window reset timer
        // that simply re-anchors on every refresh. Surface it as available instead of
        // showing a misleading countdown.
        bool isUnusedUnanchoredWindow = markUnusedAsAvailable &&
                                        usedPercent <= 0.0 &&
                                        windowSecs > 0 &&
                                        resetAfterSeconds >= Math.Max(0, windowSecs - 60);

        list.Add(new QuotaWindow
        {
            Name = windowName,
            LimitWindowSeconds = windowSecs,
            UsedPercent = usedPercent,
            RemainingPercent = remainingPercent,
            ResetInSeconds = isUnusedUnanchoredWindow ? 0 : resetAfterSeconds,
            FormattedResetIn = isUnusedUnanchoredWindow
                ? "Available"
                : TimeFormatter.FormatResetText(resetAfterSeconds)
        });
    }

    private static string? ExtractPlanFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length >= 2)
            {
                string payload = parts[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                byte[] bytes = Convert.FromBase64String(payload);
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                if (root.TryGetProperty("https://api.openai.com/auth", out var authObj))
                {
                    if (authObj.TryGetProperty("plan_type", out var pt)) return pt.GetString();
                    if (authObj.TryGetProperty("plan", out var p)) return p.GetString();
                }
                if (root.TryGetProperty("plan_type", out var pt2)) return pt2.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string FormatPlanType(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return "";
        plan = plan.Trim();
        if (plan.Length == 1) return plan.ToUpperInvariant();
        return char.ToUpperInvariant(plan[0]) + plan.Substring(1);
    }
}
