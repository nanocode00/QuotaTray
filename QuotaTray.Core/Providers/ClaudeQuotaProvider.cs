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

public class ClaudeQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "claude";
    public string ProviderTitle => "Claude Code";
    public string IconLetter => "Cl";
    public string IconColorHex => "#D97706";
    public string IconBgColorHex => "#331E05";
    public string AuthMethod => "API Key / OAuth";
    public string? CliLoginCommand => "claude";
    public bool RequiresApiKey => true;

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private readonly HttpClient _httpClient;
    private string? _customApiKey;

    public ClaudeQuotaProvider(HttpClient? httpClient = null)
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
                result.ErrorMessage = "No credentials found. Set API Key in Settings or run 'claude'";
                return result;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = resp.StatusCode == HttpStatusCode.Unauthorized;
                result.AuthStatus = resp.StatusCode == HttpStatusCode.Unauthorized ? ProviderAuthStatus.Expired : ProviderAuthStatus.Error;
                result.ErrorMessage = resp.StatusCode == HttpStatusCode.Unauthorized
                    ? "Authentication expired. Check API Key or run 'claude'"
                    : $"API Error ({(int)resp.StatusCode})";
                return result;
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var windows = new List<QuotaWindow>();

            if (root.TryGetProperty("five_hour", out var fiveHourProp) && fiveHourProp.ValueKind == JsonValueKind.Object)
            {
                ParseWindow(fiveHourProp, "5h", 18000, windows);
            }

            if (root.TryGetProperty("seven_day", out var sevenDayProp) && sevenDayProp.ValueKind == JsonValueKind.Object)
            {
                ParseWindow(sevenDayProp, "7d", 604800, windows);
            }

            result.Windows = windows;
            if (windows.Count > 0)
            {
                var tightest = windows[0];
                foreach (var w in windows)
                {
                    if (w.RemainingPercent < tightest.RemainingPercent)
                    {
                        tightest = w;
                    }
                }

                result.PrimaryRemainingPercent = tightest.RemainingPercent;
                result.NextResetInSeconds = tightest.ResetInSeconds;
                result.FormattedNextResetIn = tightest.FormattedResetIn;
                result.ResetText = tightest.FormattedResetIn;
            }

            if (root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            {
                result.PlanType = pt.GetString();
            }
            else if (root.TryGetProperty("tier", out var tierProp) && tierProp.ValueKind == JsonValueKind.String)
            {
                result.PlanType = tierProp.GetString();
            }
            else if (root.TryGetProperty("subscription_type", out var stProp) && stProp.ValueKind == JsonValueKind.String)
            {
                result.PlanType = stProp.GetString();
            }
            else if (!string.IsNullOrEmpty(token) && token.StartsWith("sk-ant-api"))
            {
                result.PlanType = "API";
            }
            else
            {
                result.PlanType = "Pro";
            }

            result.DetailsSubtitle = "Anthropic";
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

    private static string? LoadCredentials()
    {
        string? envToken = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                           ?? Environment.GetEnvironmentVariable("CLAUDE_CODE_TOKEN");
        if (!string.IsNullOrEmpty(envToken)) return envToken;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string>
        {
            Path.Combine(userProfile, ".claude", ".credentials.json"),
            Path.Combine(userProfile, ".claude", "credentials.json"),
            Path.Combine(userProfile, ".claude.json"),
            Path.Combine(userProfile, ".config", "claude", "credentials.json")
        };

        // Automatic WSL Distro Discovery
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
                        if (Directory.Exists(b))
                        {
                            string home = Path.Combine(b, "home");
                            if (Directory.Exists(home))
                            {
                                foreach (var userDir in Directory.GetDirectories(home))
                                {
                                    paths.Add(Path.Combine(userDir, ".claude", ".credentials.json"));
                                    paths.Add(Path.Combine(userDir, ".claude", "credentials.json"));
                                    paths.Add(Path.Combine(userDir, ".claude.json"));
                                    paths.Add(Path.Combine(userDir, ".config", "claude", "credentials.json"));
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                try
                {
                    string json = File.ReadAllText(p);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("claudeAiOauth", out var oauth) && oauth.TryGetProperty("accessToken", out var at1))
                    {
                        return at1.GetString();
                    }
                    if (root.TryGetProperty("accessToken", out var at2))
                    {
                        return at2.GetString();
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static void ParseWindow(JsonElement element, string name, long defaultLimitSecs, List<QuotaWindow> list)
    {
        double util = 0;
        if (element.TryGetProperty("utilization", out var uProp) && uProp.TryGetDouble(out var u))
        {
            util = u;
        }

        long resetInSecs = 0;
        if (element.TryGetProperty("resets_at", out var rProp) && rProp.ValueKind == JsonValueKind.String)
        {
            string? rStr = rProp.GetString();
            if (!string.IsNullOrEmpty(rStr) && DateTimeOffset.TryParse(rStr, out var dto))
            {
                var diff = dto - DateTimeOffset.UtcNow;
                resetInSecs = Math.Max(0, (long)diff.TotalSeconds);
            }
        }

        double remaining = Math.Max(0.0, Math.Min(100.0, 100.0 - util));

        list.Add(new QuotaWindow
        {
            Name = name,
            LimitWindowSeconds = defaultLimitSecs,
            UsedPercent = util,
            RemainingPercent = remaining,
            ResetInSeconds = resetInSecs,
            FormattedResetIn = TimeFormatter.FormatResetText(resetInSecs)
        });
    }
}
