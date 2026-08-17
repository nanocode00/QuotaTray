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
    private const string CopilotModelsUrl = "https://api.githubcopilot.com/models";
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

            // Step 1: Attempt copilot_internal/user for fine-grained snapshots
            QuotaWindow? completionsWindow = null;
            QuotaWindow? chatWindow = null;
            QuotaWindow? premiumWindow = null;
            bool isFreePlan = true; // Default for public personal tokens unless verified paid

            try
            {
                using var reqInternal = new HttpRequestMessage(HttpMethod.Get, InternalUsageUrl);
                reqInternal.Headers.Add("Authorization", $"Bearer {token}");
                reqInternal.Headers.Add("Accept", "application/json");
                reqInternal.Headers.TryAddWithoutValidation("User-Agent", "QuotaTray/1.0");
                reqInternal.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.0");

                var respInternal = await _httpClient.SendAsync(reqInternal, cancellationToken);
                if (respInternal.IsSuccessStatusCode)
                {
                    string json = await respInternal.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("login", out var loginProp)) result.AccountEmail = loginProp.GetString();
                    if (root.TryGetProperty("copilot_plan", out var planProp))
                    {
                        string? planStr = planProp.GetString();
                        if (!string.IsNullOrEmpty(planStr))
                        {
                            if (planStr.Contains("free", StringComparison.OrdinalIgnoreCase))
                            {
                                isFreePlan = true;
                                result.PlanType = "Free";
                            }
                            else if (planStr.Contains("business", StringComparison.OrdinalIgnoreCase))
                            {
                                isFreePlan = false;
                                result.PlanType = "Business";
                            }
                            else if (planStr.Contains("enterprise", StringComparison.OrdinalIgnoreCase))
                            {
                                isFreePlan = false;
                                result.PlanType = "Enterprise";
                            }
                            else
                            {
                                isFreePlan = false;
                                result.PlanType = "Pro";
                            }
                        }
                    }

                    if (root.TryGetProperty("access_type_sku", out var skuProp))
                    {
                        string? sku = skuProp.GetString();
                        if (!string.IsNullOrEmpty(sku))
                        {
                            if (sku.Contains("free", StringComparison.OrdinalIgnoreCase))
                            {
                                isFreePlan = true;
                                result.PlanType = "Free";
                            }
                            else if (sku.Contains("business", StringComparison.OrdinalIgnoreCase))
                            {
                                isFreePlan = false;
                                result.PlanType = "Business";
                            }
                            else if (sku.Contains("enterprise", StringComparison.OrdinalIgnoreCase))
                            {
                                isFreePlan = false;
                                result.PlanType = "Enterprise";
                            }
                            else
                            {
                                isFreePlan = false;
                                result.PlanType = "Pro";
                            }
                        }
                    }

                    long resetInSecs = 0;
                    string formattedReset = "";
                    if (root.TryGetProperty("quota_reset_date_utc", out var rDateProp) && rDateProp.ValueKind == JsonValueKind.String)
                    {
                        if (DateTimeOffset.TryParse(rDateProp.GetString(), out var resetDto))
                        {
                            var diff = resetDto - DateTimeOffset.UtcNow;
                            resetInSecs = Math.Max(0, (long)diff.TotalSeconds);
                            formattedReset = TimeFormatter.FormatResetText(resetInSecs);
                        }
                    }

                    if (root.TryGetProperty("quota_snapshots", out var snapshotsProp) && snapshotsProp.ValueKind == JsonValueKind.Object)
                    {
                        if (snapshotsProp.TryGetProperty("completions", out var compProp) && compProp.ValueKind == JsonValueKind.Object)
                        {
                            double remainingPct = compProp.TryGetProperty("percent_remaining", out var pr) ? pr.GetDouble() : 100.0;
                            int remaining = compProp.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0;
                            int entitlement = compProp.TryGetProperty("entitlement", out var ent) ? ent.GetInt32() : 2000;

                            completionsWindow = new QuotaWindow
                            {
                                Name = entitlement > 0 ? $"Code Completions ({remaining:N0}/{entitlement:N0})" : "Code Completions",
                                UsedPercent = Math.Max(0, 100.0 - remainingPct),
                                RemainingPercent = remainingPct,
                                ResetInSeconds = resetInSecs,
                                FormattedResetIn = !string.IsNullOrEmpty(formattedReset) ? formattedReset : "월 2,000회 제공"
                            };
                        }

                        if (snapshotsProp.TryGetProperty("chat", out var chatProp) && chatProp.ValueKind == JsonValueKind.Object)
                        {
                            double remainingPct = chatProp.TryGetProperty("percent_remaining", out var pr) ? pr.GetDouble() : 100.0;
                            int remaining = chatProp.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0;
                            int entitlement = chatProp.TryGetProperty("entitlement", out var ent) ? ent.GetInt32() : 50;

                            chatWindow = new QuotaWindow
                            {
                                Name = entitlement > 0 ? $"Chat ({remaining:N0}/{entitlement:N0})" : "Chat",
                                UsedPercent = Math.Max(0, 100.0 - remainingPct),
                                RemainingPercent = remainingPct,
                                ResetInSeconds = resetInSecs,
                                FormattedResetIn = !string.IsNullOrEmpty(formattedReset) ? formattedReset : "월 50회 제공"
                            };
                        }

                        if (snapshotsProp.TryGetProperty("premium_interactions", out var premProp) && premProp.ValueKind == JsonValueKind.Object)
                        {
                            double remainingPct = premProp.TryGetProperty("percent_remaining", out var pr) ? pr.GetDouble() : 100.0;
                            int remaining = premProp.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0;
                            int entitlement = premProp.TryGetProperty("entitlement", out var ent) ? ent.GetInt32() : 0;

                            premiumWindow = new QuotaWindow
                            {
                                Name = entitlement > 0 ? $"Premium Requests ({remaining:N0}/{entitlement:N0})" : "Premium Requests",
                                UsedPercent = Math.Max(0, 100.0 - remainingPct),
                                RemainingPercent = remainingPct,
                                ResetInSeconds = resetInSecs,
                                FormattedResetIn = !string.IsNullOrEmpty(formattedReset) ? formattedReset : "Active"
                            };
                        }
                    }
                }
            }
            catch { }

            // Step 2: Query official api.githubcopilot.com/models to verify live subscription and model catalog
            using var reqModels = new HttpRequestMessage(HttpMethod.Get, CopilotModelsUrl);
            reqModels.Headers.Add("Authorization", $"Bearer {token}");
            reqModels.Headers.Add("Accept", "application/json");
            reqModels.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.24.1");
            reqModels.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.0");

            var respModels = await _httpClient.SendAsync(reqModels, cancellationToken);
            if (respModels.IsSuccessStatusCode)
            {
                var groups = new List<QuotaGroup>();

                if (string.IsNullOrEmpty(result.PlanType))
                {
                    result.PlanType = isFreePlan ? "Free" : "Pro";
                }

                // Track 1: Standard Models (OpenAI 범용)
                var standardGroup = new QuotaGroup
                {
                    GroupId = "standard",
                    GroupName = isFreePlan ? "기본 모델 (Copilot Free)" : "Standard Models (OpenAI 범용)",
                    ModelsList = new List<string>
                    {
                        "GPT-4o",
                        "GPT-4.1",
                        "GPT-5 mini",
                        "GPT-4o mini",
                        "GPT 3.5 Turbo"
                    }
                };

                if (chatWindow != null || completionsWindow != null)
                {
                    if (completionsWindow != null) standardGroup.Windows.Add(completionsWindow);
                    if (chatWindow != null) standardGroup.Windows.Add(chatWindow);
                }
                else
                {
                    if (isFreePlan)
                    {
                        standardGroup.Windows.Add(new QuotaWindow
                        {
                            Name = "코드 완성 (월 2,000회 한도)",
                            UsedPercent = 0.0,
                            RemainingPercent = 100.0,
                            ResetInSeconds = 0,
                            FormattedResetIn = "월 2,000회"
                        });
                        standardGroup.Windows.Add(new QuotaWindow
                        {
                            Name = "채팅 메시지 (월 50회 한도)",
                            UsedPercent = 0.0,
                            RemainingPercent = 100.0,
                            ResetInSeconds = 0,
                            FormattedResetIn = "월 50회"
                        });
                    }
                    else
                    {
                        standardGroup.Windows.Add(new QuotaWindow
                        {
                            Name = "채팅 및 코드 완성",
                            UsedPercent = 0.0,
                            RemainingPercent = 100.0,
                            ResetInSeconds = 0,
                            FormattedResetIn = "무제한 활성"
                        });
                    }
                }
                // Track 2: Premium Models (Claude, Gemini, 고급 추론)
                var premiumGroup = new QuotaGroup
                {
                    GroupId = "premium",
                    GroupName = "Premium Models (Claude · Gemini · 추론)",
                    ModelsList = new List<string>
                    {
                        "Claude Opus 5 (Thinking)",
                        "Claude Sonnet 5 (Thinking)",
                        "Claude Haiku 4.5",
                        "Gemini 3.1 Pro",
                        "Kimi K3"
                    }
                };

                if (premiumWindow != null)
                {
                    premiumGroup.Windows.Add(premiumWindow);
                }
                else
                {
                    premiumGroup.Windows.Add(new QuotaWindow
                    {
                        Name = isFreePlan ? "프리미엄 모델 풀" : "프리미엄 요청 (월간 공유 풀)",
                        UsedPercent = 0.0,
                        RemainingPercent = 100.0,
                        ResetInSeconds = 0,
                        FormattedResetIn = isFreePlan ? "Pro 요금제 권장" : "Active"
                    });
                }

                if (isFreePlan)
                {
                    // Free 플랜: Premium Requests는 숨기고, 기본 모델(Code Completions & Chat)만 표시
                    groups.Add(standardGroup);

                    var primaryWin = completionsWindow ?? standardGroup.Windows.FirstOrDefault();
                    result.PrimaryRemainingPercent = primaryWin?.RemainingPercent ?? 100.0;
                    result.ResetText = primaryWin?.FormattedResetIn ?? "월 2,000회";
                    result.FormattedNextResetIn = result.ResetText;
                }
                else
                {
                    // 유료 플랜: 어차피 무제한인 기본 모델(Standard)은 숨기고, 유한한 Premium Requests만 표시
                    groups.Add(premiumGroup);

                    var primaryWin = premiumWindow ?? premiumGroup.Windows.FirstOrDefault();
                    result.PrimaryRemainingPercent = primaryWin?.RemainingPercent ?? 100.0;
                    result.ResetText = primaryWin?.FormattedResetIn ?? "Active";
                    result.FormattedNextResetIn = result.ResetText;
                }

                result.Groups = groups;
                result.Windows = groups.SelectMany(g => g.Windows).ToList();

                result.DetailsSubtitle = "GitHub";
                result.AuthStatus = ProviderAuthStatus.Connected;
                result.IsSuccess = true;
                return result;
            }
            else if (respModels.StatusCode == HttpStatusCode.Unauthorized || respModels.StatusCode == HttpStatusCode.Forbidden)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.Expired;
                result.ErrorMessage = "GitHub Copilot 인증이 만료되었거나 구독이 활성화되지 않았습니다.";
                return result;
            }

            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = $"API Error ({(int)respModels.StatusCode})";
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
        string? envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                           ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                           ?? Environment.GetEnvironmentVariable("COPILOT_TOKEN");
        if (!string.IsNullOrEmpty(envToken)) return envToken;

        // Windows Credential Manager check
        if (OperatingSystem.IsWindows())
        {
            string? cred = Win32CredMan.ReadCredential("gh:github.com")
                           ?? Win32CredMan.ReadCredential("GitHub - https://api.github.com")
                           ?? Win32CredMan.ReadCredential("git:https://github.com");
            if (!string.IsNullOrEmpty(cred)) return cred;
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
                                    paths.Add(Path.Combine(userDir, ".config", "gh", "hosts.yml"));
                                    paths.Add(Path.Combine(userDir, ".config", "github-copilot", "hosts.json"));
                                    paths.Add(Path.Combine(userDir, ".config", "github-copilot", "apps.json"));
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
                    string content = File.ReadAllText(p);
                    if (p.EndsWith(".json"))
                    {
                        using var doc = JsonDocument.Parse(content);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.TryGetProperty("oauth_token", out var tok)) return tok.GetString();
                            if (prop.Value.TryGetProperty("user", out var usr) && usr.TryGetProperty("oauth_token", out var ut)) return ut.GetString();
                        }
                    }
                    else if (p.EndsWith(".yml") || p.EndsWith(".yaml"))
                    {
                        foreach (var line in File.ReadAllLines(p))
                        {
                            string trimmed = line.Trim();
                            if (trimmed.StartsWith("oauth_token:"))
                            {
                                string[] parts = trimmed.Split(':', 2);
                                if (parts.Length > 1)
                                {
                                    string val = parts[1].Trim().Trim('"', '\'');
                                    if (!string.IsNullOrEmpty(val)) return val;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        return null;
    }
}
