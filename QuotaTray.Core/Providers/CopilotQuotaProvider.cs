using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;
using QuotaTray.Core.Security;
using QuotaTray.Core.Utils;

namespace QuotaTray.Core.Providers;

public class CopilotQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "copilot";
    public string ProviderTitle => "GitHub Copilot";
    public string IconLetter => "Gh";
    public string IconColorHex => "#38BDF8";
    public string IconBgColorHex => "#0C4A6E";
    public string AuthMethod => "GitHub Token / gh";
    public string? CliLoginCommand => "gh auth login";
    public bool RequiresApiKey => false;

    private const string InternalUsageUrl = "https://api.github.com/copilot_internal/user";
    private readonly HttpClient _httpClient;
    private string? _customApiKey;

    public CopilotQuotaProvider(HttpClient? httpClient = null)
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

        try
        {
            string? token = !string.IsNullOrEmpty(_customApiKey) ? _customApiKey : LoadCredentials();
            if (string.IsNullOrEmpty(token))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = "GitHub 인증 정보가 없습니다. Settings에서 Token을 등록하거나 'gh auth login'을 실행하세요.";
                return result;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, InternalUsageUrl);
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "QuotaTray/1.0");
            request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.0");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                result.AuthStatus = result.IsAuthMissing ? ProviderAuthStatus.Expired : ProviderAuthStatus.Error;
                result.ErrorMessage = result.IsAuthMissing
                    ? "GitHub Copilot 인증이 만료되었거나 Copilot 사용 권한을 확인할 수 없습니다."
                    : $"GitHub Copilot quota API error ({(int)response.StatusCode}).";
                return result;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("login", out JsonElement loginProp) && loginProp.ValueKind == JsonValueKind.String)
            {
                result.AccountEmail = loginProp.GetString();
            }

            (string planType, bool isFreePlan) = ResolvePlan(root);
            result.PlanType = planType;

            long resetInSeconds = 0;
            string resetText = "";
            if (root.TryGetProperty("quota_reset_date_utc", out JsonElement resetProp)
                && resetProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resetProp.GetString(), out DateTimeOffset resetAt))
            {
                TimeSpan diff = resetAt - DateTimeOffset.UtcNow;
                resetInSeconds = Math.Max(0, (long)diff.TotalSeconds);
                resetText = TimeFormatter.FormatResetText(resetInSeconds);
            }

            QuotaWindow? completionsWindow = null;
            QuotaWindow? chatWindow = null;
            QuotaWindow? premiumWindow = null;
            bool hasUnlimitedQuota = false;

            if (root.TryGetProperty("quota_snapshots", out JsonElement snapshots)
                && snapshots.ValueKind == JsonValueKind.Object)
            {
                completionsWindow = ParseFiniteQuotaWindow(
                    snapshots,
                    "completions",
                    "Code Completions",
                    resetInSeconds,
                    resetText);

                chatWindow = ParseFiniteQuotaWindow(
                    snapshots,
                    "chat",
                    "Chat",
                    resetInSeconds,
                    resetText);

                premiumWindow = ParseFiniteQuotaWindow(
                    snapshots,
                    "premium_interactions",
                    "Premium Requests",
                    resetInSeconds,
                    resetText);

                hasUnlimitedQuota = SnapshotIsUnlimited(snapshots, "completions")
                                    || SnapshotIsUnlimited(snapshots, "chat")
                                    || SnapshotIsUnlimited(snapshots, "premium_interactions");
            }

            var groups = new List<QuotaGroup>();
            var standardGroup = new QuotaGroup
            {
                GroupId = "standard",
                GroupName = isFreePlan ? "Copilot Free" : "Copilot Standard"
            };

            if (completionsWindow != null) standardGroup.Windows.Add(completionsWindow);
            if (chatWindow != null) standardGroup.Windows.Add(chatWindow);

            var premiumGroup = new QuotaGroup
            {
                GroupId = "premium",
                GroupName = "Premium Requests"
            };

            if (premiumWindow != null) premiumGroup.Windows.Add(premiumWindow);

            QuotaWindow? primaryWindow;
            if (isFreePlan)
            {
                if (standardGroup.Windows.Count > 0)
                {
                    groups.Add(standardGroup);
                }

                primaryWindow = completionsWindow ?? chatWindow;
            }
            else
            {
                if (premiumGroup.Windows.Count > 0)
                {
                    groups.Add(premiumGroup);
                }
                else if (standardGroup.Windows.Count > 0)
                {
                    groups.Add(standardGroup);
                }

                primaryWindow = premiumWindow ?? completionsWindow ?? chatWindow;
            }

            result.Groups = groups;
            result.Windows = groups.SelectMany(group => group.Windows).ToList();

            if (primaryWindow != null)
            {
                result.PrimaryRemainingPercent = primaryWindow.RemainingPercent;
                result.NextResetInSeconds = primaryWindow.ResetInSeconds;
                result.ResetText = primaryWindow.FormattedResetIn;
                result.FormattedNextResetIn = primaryWindow.FormattedResetIn;
            }
            else if (hasUnlimitedQuota)
            {
                result.PrimaryRemainingPercent = 100.0;
                result.ResetText = "Unlimited";
                result.FormattedNextResetIn = "Unlimited";
            }
            else
            {
                // A successful account response without a finite quota is still authenticated.
                // Do not invent a 100% remaining value when GitHub did not report one.
                result.PrimaryRemainingPercent = 0.0;
                result.ResetText = !string.IsNullOrEmpty(resetText) ? resetText : "No finite quota reported";
                result.FormattedNextResetIn = result.ResetText;
                result.NextResetInSeconds = resetInSeconds;
            }

            result.DetailsSubtitle = !string.IsNullOrEmpty(result.AccountEmail) ? result.AccountEmail : "GitHub";
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

    private static (string PlanType, bool IsFreePlan) ResolvePlan(JsonElement root)
    {
        string? sku = GetString(root, "access_type_sku");
        if (!string.IsNullOrEmpty(sku))
        {
            if (sku.Contains("free", StringComparison.OrdinalIgnoreCase)) return ("Free", true);
            if (sku.Contains("enterprise", StringComparison.OrdinalIgnoreCase)) return ("Enterprise", false);
            if (sku.Contains("business", StringComparison.OrdinalIgnoreCase)) return ("Business", false);
            if (sku.Contains("pro_plus", StringComparison.OrdinalIgnoreCase)
                || sku.Contains("pro-plus", StringComparison.OrdinalIgnoreCase)
                || sku.Contains("pro+", StringComparison.OrdinalIgnoreCase)) return ("Pro+", false);
            if (sku.Contains("max", StringComparison.OrdinalIgnoreCase)) return ("Max", false);
            if (sku.Contains("pro", StringComparison.OrdinalIgnoreCase)) return ("Pro", false);
        }

        string? plan = GetString(root, "copilot_plan");
        if (!string.IsNullOrEmpty(plan))
        {
            if (plan.Contains("free", StringComparison.OrdinalIgnoreCase)) return ("Free", true);
            if (plan.Contains("enterprise", StringComparison.OrdinalIgnoreCase)) return ("Enterprise", false);
            if (plan.Contains("business", StringComparison.OrdinalIgnoreCase)) return ("Business", false);
            if (plan.Contains("pro", StringComparison.OrdinalIgnoreCase)) return ("Pro", false);
            if (plan.Equals("individual", StringComparison.OrdinalIgnoreCase)) return ("Individual", false);
            return (plan, false);
        }

        return ("Unknown", false);
    }

    private static QuotaWindow? ParseFiniteQuotaWindow(
        JsonElement snapshots,
        string key,
        string displayName,
        long resetInSeconds,
        string resetText)
    {
        if (!snapshots.TryGetProperty(key, out JsonElement snapshot)
            || snapshot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetBoolean(snapshot, "has_quota", out bool hasQuota) && !hasQuota)
        {
            return null;
        }

        if (TryGetBoolean(snapshot, "unlimited", out bool unlimited) && unlimited)
        {
            return null;
        }

        long entitlement = GetInt64(snapshot, "entitlement", -1);
        if (entitlement <= 0)
        {
            return null;
        }

        long remaining = GetInt64(snapshot, "remaining", GetInt64(snapshot, "quota_remaining", 0));
        double remainingPercent = GetDouble(snapshot, "percent_remaining",
            entitlement > 0 ? (double)remaining / entitlement * 100.0 : 0.0);
        remainingPercent = Math.Clamp(remainingPercent, 0.0, 100.0);

        return new QuotaWindow
        {
            Name = $"{displayName} ({remaining:N0}/{entitlement:N0})",
            UsedPercent = Math.Clamp(100.0 - remainingPercent, 0.0, 100.0),
            RemainingPercent = remainingPercent,
            ResetInSeconds = resetInSeconds,
            FormattedResetIn = !string.IsNullOrEmpty(resetText) ? resetText : "Reset unavailable"
        };
    }

    private static bool SnapshotIsUnlimited(JsonElement snapshots, string key)
    {
        return snapshots.TryGetProperty(key, out JsonElement snapshot)
               && snapshot.ValueKind == JsonValueKind.Object
               && TryGetBoolean(snapshot, "unlimited", out bool unlimited)
               && unlimited;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out JsonElement property)) return false;
        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        return false;
    }

    private static long GetInt64(JsonElement element, string propertyName, long fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return property.TryGetInt64(out long value) ? value : fallback;
    }

    private static double GetDouble(JsonElement element, string propertyName, double fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return property.TryGetDouble(out double value) ? value : fallback;
    }

    private static string? LoadCredentials()
    {
        string? envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                           ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                           ?? Environment.GetEnvironmentVariable("COPILOT_TOKEN");
        if (!string.IsNullOrEmpty(envToken)) return CleanToken(envToken);

        if (OperatingSystem.IsWindows())
        {
            var ghCreds = Win32CredMan.EnumerateCredentials("gh:github.com*");
            foreach (var (_, secret) in ghCreds)
            {
                string cleaned = CleanToken(secret);
                if (IsValidGitHubToken(cleaned)) return cleaned;
            }

            string[] directTargets = { "gh:github.com", "gh:github.com:", "GitHub - https://api.github.com", "git:https://github.com" };
            foreach (var target in directTargets)
            {
                string? cred = Win32CredMan.ReadCredential(target);
                if (!string.IsNullOrEmpty(cred))
                {
                    string cleaned = CleanToken(cred);
                    if (IsValidGitHubToken(cleaned)) return cleaned;
                }
            }

            var allCreds = Win32CredMan.EnumerateCredentials();
            foreach (var (target, secret) in allCreds)
            {
                if (target.Contains("github", StringComparison.OrdinalIgnoreCase)
                    || target.Contains("gh:", StringComparison.OrdinalIgnoreCase))
                {
                    string cleaned = CleanToken(secret);
                    if (IsValidGitHubToken(cleaned)) return cleaned;
                }
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var paths = new List<string>
        {
            Path.Combine(userProfile, ".config", "github-copilot", "hosts.json"),
            Path.Combine(userProfile, ".config", "github-copilot", "apps.json"),
            Path.Combine(localAppData, "github-copilot", "hosts.json"),
            Path.Combine(appData, "github-copilot", "hosts.json"),
            Path.Combine(userProfile, ".config", "gh", "hosts.yml"),
            Path.Combine(appData, "gh", "hosts.yml"),
            Path.Combine(localAppData, "gh", "hosts.yml")
        };

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var distros = new List<string>();
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (key != null)
                    {
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            string? name = subKey?.GetValue("DistributionName") as string;
                            if (!string.IsNullOrEmpty(name)) distros.Add(name);
                        }
                    }
                }

                if (distros.Count == 0)
                {
                    distros.AddRange(new[] { "Ubuntu-24.04", "Ubuntu-22.04", "Ubuntu", "Debian" });
                }

                foreach (var distro in distros)
                {
                    string[] bases = { $@"\\wsl.localhost\{distro}", $@"\\wsl$\{distro}" };
                    foreach (var b in bases)
                    {
                        if (!Directory.Exists(b)) continue;

                        string home = Path.Combine(b, "home");
                        if (!Directory.Exists(home)) continue;

                        foreach (var userDir in Directory.GetDirectories(home))
                        {
                            paths.Add(Path.Combine(userDir, ".config", "gh", "hosts.yml"));
                            paths.Add(Path.Combine(userDir, ".config", "github-copilot", "hosts.json"));
                            paths.Add(Path.Combine(userDir, ".config", "github-copilot", "apps.json"));
                        }
                    }
                }
            }
            catch { }
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                string content = File.ReadAllText(path);
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(content);
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Value.TryGetProperty("oauth_token", out JsonElement tokenProperty))
                        {
                            string? value = tokenProperty.GetString();
                            if (!string.IsNullOrEmpty(value)) return CleanToken(value);
                        }

                        if (property.Value.TryGetProperty("user", out JsonElement user)
                            && user.TryGetProperty("oauth_token", out JsonElement userToken))
                        {
                            string? value = userToken.GetString();
                            if (!string.IsNullOrEmpty(value)) return CleanToken(value);
                        }
                    }
                }
                else if (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();
                        if (!trimmed.StartsWith("oauth_token:", StringComparison.Ordinal)) continue;

                        string[] parts = trimmed.Split(':', 2);
                        if (parts.Length <= 1) continue;

                        string value = parts[1].Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(value)) return CleanToken(value);
                    }
                }
            }
            catch { }
        }

        return null;
    }

    private static string CleanToken(string token)
    {
        return token.Trim('\0', ' ', '\r', '\n', '\t', '"', '\'');
    }

    private static bool IsValidGitHubToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 10) return false;

        return token.StartsWith("gho_", StringComparison.OrdinalIgnoreCase)
               || token.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase)
               || token.StartsWith("ghu_", StringComparison.OrdinalIgnoreCase)
               || token.StartsWith("ghs_", StringComparison.OrdinalIgnoreCase)
               || token.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase)
               || token.Length >= 30;
    }
}
