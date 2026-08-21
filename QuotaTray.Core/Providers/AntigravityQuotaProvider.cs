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

public class AntigravityQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "antigravity";
    public string ProviderTitle => "Antigravity";
    public string IconLetter => "A";
    public string IconColorHex => "#A855F7";
    public string IconBgColorHex => "#2E1065";
    public string AuthMethod => "Antigravity CLI / OAuth";
    public string? CliLoginCommand => "agy auth login";
    public bool RequiresApiKey => false;

    private static readonly string[] BaseUrls =
    {
        "https://cloudcode-pa.googleapis.com",
        "https://daily-cloudcode-pa.sandbox.googleapis.com"
    };

    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";

    private static List<(string ClientId, string ClientSecret)>? _discoveredClients;
    private static readonly object _discoveryLock = new();

    private readonly HttpClient _httpClient;
    private string? _cachedAccessToken;
    private string? _cachedRefreshToken;
    private string? _cachedProjectId;
    private string? _cachedEmail;
    private string? _customApiKey;

    public AntigravityQuotaProvider(HttpClient? httpClient = null)
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
            string? tokenToUse = _customApiKey;

            if (string.IsNullOrEmpty(tokenToUse))
            {
                LoadStoredCredentials();
                tokenToUse = _cachedAccessToken;
            }

            if (string.IsNullOrEmpty(tokenToUse) && string.IsNullOrEmpty(_cachedRefreshToken))
            {
                result.IsSuccess = false;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.IsAuthMissing = true;
                result.ErrorMessage = "No credentials found. Run 'agy auth login'";
                return result;
            }

            // Always ensure we have a valid access_token
            if (string.IsNullOrEmpty(tokenToUse) && !string.IsNullOrEmpty(_cachedRefreshToken) && string.IsNullOrEmpty(_customApiKey))
            {
                bool refreshed = await TryRefreshTokenAsync(cancellationToken);
                if (refreshed)
                {
                    tokenToUse = _cachedAccessToken;
                }
            }

            if (string.IsNullOrEmpty(tokenToUse))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = "No Antigravity credentials found";
                return result;
            }

            string? lastError = null;

            foreach (var baseUrl in BaseUrls)
            {
                string loadUrl = $"{baseUrl}/v1internal:loadCodeAssist";
                string quotaSummaryUrl = $"{baseUrl}/v1internal:retrieveUserQuotaSummary";

                // Step 1: loadCodeAssist
                var (loadStatus, loadDoc) = await PostJsonAsync(
                    loadUrl,
                    tokenToUse,
                    "antigravity/windows/amd64",
                    "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                    cancellationToken);

                if (loadStatus == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_cachedRefreshToken) && string.IsNullOrEmpty(_customApiKey))
                {
                    bool refreshed = await TryRefreshTokenAsync(cancellationToken);
                    if (refreshed && !string.IsNullOrEmpty(_cachedAccessToken))
                    {
                        tokenToUse = _cachedAccessToken;
                        (loadStatus, loadDoc) = await PostJsonAsync(
                            loadUrl,
                            tokenToUse,
                            "antigravity/windows/amd64",
                            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                            cancellationToken);
                    }
                }

                if (loadStatus != HttpStatusCode.OK || loadDoc == null)
                {
                    lastError = $"loadCodeAssist failed ({loadStatus})";
                    loadDoc?.Dispose();
                    continue;
                }

                using (loadDoc)
                {
                    _cachedProjectId = ExtractProjectId(loadDoc.RootElement);
                    result.PlanType = ExtractPlanType(loadDoc.RootElement);
                }

                if (string.IsNullOrEmpty(_cachedProjectId))
                {
                    lastError = "Could not find Cloud Code Assist project";
                    continue;
                }

                // Step 2: retrieveUserQuotaSummary (Official /usage endpoint with 2 Quota Pools)
                var (summaryStatus, summaryDoc) = await PostJsonAsync(
                    quotaSummaryUrl,
                    tokenToUse,
                    "antigravity/1.11.5 windows/amd64",
                    $"{{\"project\":\"{_cachedProjectId}\"}}",
                    cancellationToken);

                if (summaryStatus == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_cachedRefreshToken) && string.IsNullOrEmpty(_customApiKey))
                {
                    bool refreshed = await TryRefreshTokenAsync(cancellationToken);
                    if (refreshed && !string.IsNullOrEmpty(_cachedAccessToken))
                    {
                        tokenToUse = _cachedAccessToken;
                        (summaryStatus, summaryDoc) = await PostJsonAsync(
                            quotaSummaryUrl,
                            tokenToUse,
                            "antigravity/1.11.5 windows/amd64",
                            $"{{\"project\":\"{_cachedProjectId}\"}}",
                            cancellationToken);
                    }
                }

                var groups = new List<QuotaGroup>();

                if (summaryStatus == HttpStatusCode.OK && summaryDoc != null)
                {
                    using (summaryDoc)
                    {
                        groups = ParseQuotaGroups(summaryDoc.RootElement);
                    }
                }

                if (groups.Count == 0)
                {
                    // Fallback default structure if API fails
                    groups = CreateFallbackGroups();
                }

                result.Groups = groups;

                // Flatten windows for compatibility
                var allWindows = groups.SelectMany(g => g.Windows).ToList();
                result.Windows = allWindows;

                // Primary remaining percent: Select whichever is lower between Gemini Weekly and Claude/GPT Weekly; 5h is detailed only
                var geminiGroup = groups.FirstOrDefault(g => g.GroupId == "gemini" || g.GroupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
                var claudeGroup = groups.FirstOrDefault(g => g.GroupId == "claude" || g.GroupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || g.GroupName.Contains("GPT", StringComparison.OrdinalIgnoreCase));

                var geminiWeekly = geminiGroup?.Windows.FirstOrDefault(w => w.Name.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
                var claudeWeekly = claudeGroup?.Windows.FirstOrDefault(w => w.Name.Equals("Weekly", StringComparison.OrdinalIgnoreCase));

                QuotaWindow? chosenWeekly = null;
                if (geminiWeekly != null && claudeWeekly != null)
                {
                    chosenWeekly = geminiWeekly.RemainingPercent <= claudeWeekly.RemainingPercent ? geminiWeekly : claudeWeekly;
                }
                else
                {
                    chosenWeekly = geminiWeekly ?? claudeWeekly;
                }

                if (chosenWeekly != null)
                {
                    result.PrimaryRemainingPercent = chosenWeekly.RemainingPercent;
                    result.ResetText = chosenWeekly.FormattedResetIn;
                    result.FormattedNextResetIn = chosenWeekly.FormattedResetIn;
                }
                else if (groups.Count > 0)
                {
                    var tightestGroup = groups.OrderBy(g => g.PrimaryRemainingPercent).First();
                    result.PrimaryRemainingPercent = tightestGroup.PrimaryRemainingPercent;
                    result.ResetText = tightestGroup.ResetText;
                    result.FormattedNextResetIn = tightestGroup.ResetText;
                }
                else
                {
                    result.PrimaryRemainingPercent = 100.0;
                    result.ResetText = "Available";
                }

                if (!string.IsNullOrEmpty(_cachedEmail))
                {
                    result.AccountEmail = _cachedEmail;
                    result.DetailsSubtitle = _cachedEmail;
                }
                else
                {
                    result.DetailsSubtitle = "Gemini";
                }

                result.AuthStatus = ProviderAuthStatus.Connected;
                result.IsSuccess = true;
                return result;
            }

            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = lastError ?? "Failed to fetch Antigravity quota";
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

    private static List<QuotaGroup> ParseQuotaGroups(JsonElement root)
    {
        var resultGroups = new List<QuotaGroup>();

        if (!root.TryGetProperty("groups", out var groupsProp) || groupsProp.ValueKind != JsonValueKind.Array)
        {
            return resultGroups;
        }

        foreach (var groupEl in groupsProp.EnumerateArray())
        {
            string rawName = groupEl.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            bool isGemini = rawName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);

            var qg = new QuotaGroup
            {
                GroupId = isGemini ? "gemini" : "claude_gpt",
                GroupName = isGemini ? "Gemini Models" : "Claude & GPT Models",
                ModelsList = isGemini
                    ? new List<string>
                    {
                        "Gemini 3.1 Pro (High / Low)",
                        "Gemini 3 Flash",
                        "Gemini 3.5 Flash (High / Med / Low)",
                        "Gemini 3.6 Flash (High / Med / Low)",
                        "Gemini 2.5 Pro",
                        "Gemini 3.1 Flash Lite",
                        "Gemini 3.1 Flash Image"
                    }
                    : new List<string>
                    {
                        "Claude Opus 4.6 (Thinking)",
                        "Claude Sonnet 4.6 (Thinking)",
                        "GPT-OSS 120B (Medium)"
                    }
            };

            if (groupEl.TryGetProperty("buckets", out var bucketsProp) && bucketsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var bucket in bucketsProp.EnumerateArray())
                {
                    string windowType = bucket.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
                    string bucketId = bucket.TryGetProperty("bucketId", out var bid) ? bid.GetString() ?? "" : "";
                    double remainingFraction = bucket.TryGetProperty("remainingFraction", out var rf) ? rf.GetDouble() : 1.0;

                    long resetInSecs = 0;
                    if (bucket.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String)
                    {
                        string? rtStr = rt.GetString();
                        if (!string.IsNullOrEmpty(rtStr) && DateTimeOffset.TryParse(rtStr, out var dto))
                        {
                            resetInSecs = Math.Max(0, (long)(dto - DateTimeOffset.UtcNow).TotalSeconds);
                        }
                    }

                    double remainingPercent = Math.Max(0.0, Math.Min(100.0, Math.Round(remainingFraction * 100.0)));
                    string name = windowType.Equals("weekly", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase)
                        ? "Weekly"
                        : "5h";

                    if (!qg.Windows.Any(x => x.Name == name))
                    {
                        qg.Windows.Add(new QuotaWindow
                        {
                            Name = name,
                            LimitWindowSeconds = name == "Weekly" ? 604800 : 18000,
                            UsedPercent = Math.Max(0.0, 100.0 - remainingPercent),
                            RemainingPercent = remainingPercent,
                            ResetInSeconds = resetInSecs,
                            FormattedResetIn = TimeFormatter.FormatResetText(resetInSecs)
                        });
                    }
                }
            }

            // Sort 5h first, then Weekly
            qg.Windows.Sort((a, b) => a.LimitWindowSeconds.CompareTo(b.LimitWindowSeconds));
            resultGroups.Add(qg);
        }

        return resultGroups;
    }

    private static List<QuotaGroup> CreateFallbackGroups()
    {
        return new List<QuotaGroup>
        {
            new QuotaGroup
            {
                GroupId = "gemini",
                GroupName = "Gemini Models",
                ModelsList = new List<string>
                {
                    "Gemini 3.1 Pro (High / Low)",
                    "Gemini 3 Flash",
                    "Gemini 3.5 Flash",
                    "Gemini 2.5 Pro",
                    "Gemini 3.1 Flash Lite"
                },
                Windows = new List<QuotaWindow>
                {
                    new QuotaWindow { Name = "5h", RemainingPercent = 100.0, FormattedResetIn = "Available" },
                    new QuotaWindow { Name = "Weekly", RemainingPercent = 100.0, FormattedResetIn = "Available" }
                }
            },
            new QuotaGroup
            {
                GroupId = "claude_gpt",
                GroupName = "Claude & GPT Models",
                ModelsList = new List<string>
                {
                    "Claude Opus 4.6 (Thinking)",
                    "Claude Sonnet 4.6 (Thinking)",
                    "GPT-OSS 120B (Medium)"
                },
                Windows = new List<QuotaWindow>
                {
                    new QuotaWindow { Name = "5h", RemainingPercent = 100.0, FormattedResetIn = "Available" },
                    new QuotaWindow { Name = "Weekly", RemainingPercent = 100.0, FormattedResetIn = "Available" }
                }
            }
        };
    }

    private void LoadStoredCredentials()
    {
        // Clear previous cached tokens
        _cachedAccessToken = null;
        _cachedRefreshToken = null;
        
        // Environment variables
        _cachedAccessToken = Environment.GetEnvironmentVariable("ANTIGRAVITY_ACCESS_TOKEN")
                             ?? Environment.GetEnvironmentVariable("GEMINI_ACCESS_TOKEN");
        _cachedRefreshToken = Environment.GetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKEN")
                              ?? Environment.GetEnvironmentVariable("GEMINI_REFRESH_TOKEN");

        // If we already have a refresh token from env, we're good
        if (!string.IsNullOrEmpty(_cachedRefreshToken)) return;
        // If we have an access token but no refresh token, still try other sources for refresh token

        // Windows Credential Manager
        if (OperatingSystem.IsWindows())
        {
            string? cred = Win32CredMan.ReadCredential("gemini:antigravity")
                           ?? Win32CredMan.ReadCredential("antigravity:oauth")
                           ?? Win32CredMan.ReadCredential("google:cloudcode");
            if (!string.IsNullOrEmpty(cred))
            {
                ExtractTokensFromJson(cred);
                // Prioritize refresh_token - if we found one, it's more valuable than a possibly stale access_token
                if (!string.IsNullOrEmpty(_cachedRefreshToken)) return;
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidatePaths = new List<string>
        {
            Path.Combine(userProfile, ".antigravity", "auth.json"),
            Path.Combine(userProfile, ".antigravity", "credentials.json"),
            Path.Combine(userProfile, ".config", "antigravity", "auth.json"),
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "auth.json"),
            Path.Combine(userProfile, ".gemini", "oauth.json"),
            Path.Combine(appData, "antigravity", "auth.json"),
            Path.Combine(appData, "gcloud", "application_default_credentials.json"),
            Path.Combine(localAppData, "antigravity", "auth.json")
        };

        foreach (var p in candidatePaths)
        {
            if (File.Exists(p))
            {
                try
                {
                    string json = File.ReadAllText(p);
                    ExtractTokensFromJson(json);
                    if (!string.IsNullOrEmpty(_cachedAccessToken) || !string.IsNullOrEmpty(_cachedRefreshToken)) return;
                }
                catch { }
            }
        }
    }

    private void ExtractTokensFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var at)) _cachedAccessToken = at.GetString();
            if (root.TryGetProperty("refresh_token", out var rt)) _cachedRefreshToken = rt.GetString();
            if (root.TryGetProperty("project_id", out var pid)) _cachedProjectId = pid.GetString();
            if (root.TryGetProperty("projectId", out var pid2)) _cachedProjectId = pid2.GetString();

            if (root.TryGetProperty("token", out var tok))
            {
                if (tok.ValueKind == JsonValueKind.String)
                {
                    _cachedAccessToken = tok.GetString();
                }
                else if (tok.ValueKind == JsonValueKind.Object)
                {
                    if (tok.TryGetProperty("access_token", out var at2)) _cachedAccessToken = at2.GetString();
                    if (tok.TryGetProperty("refresh_token", out var rt2)) _cachedRefreshToken = rt2.GetString();
                }
            }

            if (root.TryGetProperty("oauth", out var oauth))
            {
                if (oauth.TryGetProperty("access_token", out var oat)) _cachedAccessToken = oat.GetString();
                if (oauth.TryGetProperty("refresh_token", out var ort)) _cachedRefreshToken = ort.GetString();
            }

            if (root.TryGetProperty("id_token", out var idt))
            {
                string? idtStr = idt.GetString();
                if (!string.IsNullOrEmpty(idtStr))
                {
                    string? email = ExtractEmailFromJwt(idtStr);
                    if (!string.IsNullOrEmpty(email)) _cachedEmail = email;
                }
            }

            if (string.IsNullOrEmpty(_cachedEmail) && !string.IsNullOrEmpty(_cachedAccessToken))
            {
                string? email = ExtractEmailFromJwt(_cachedAccessToken);
                if (!string.IsNullOrEmpty(email)) _cachedEmail = email;
            }
        }
        catch { }
    }

    private async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_cachedRefreshToken)) return false;

        var clients = GetOAuthClients();
        foreach (var (clientId, clientSecret) in clients)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, GoogleTokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["refresh_token"] = _cachedRefreshToken
                    })
                };

                var resp = await _httpClient.SendAsync(req, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("access_token", out var at))
                    {
                        _cachedAccessToken = at.GetString();

                        if (root.TryGetProperty("refresh_token", out var newRt))
                        {
                            string? newRtStr = newRt.GetString();
                            if (!string.IsNullOrEmpty(newRtStr))
                            {
                                _cachedRefreshToken = newRtStr;
                            }
                        }

                        if (root.TryGetProperty("id_token", out var idTokProp))
                        {
                            string? idTok = idTokProp.GetString();
                            if (!string.IsNullOrEmpty(idTok))
                            {
                                string? email = ExtractEmailFromJwt(idTok);
                                if (!string.IsNullOrEmpty(email))
                                {
                                    _cachedEmail = email;
                                }
                            }
                        }

                        PersistRefreshedTokens();
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private static List<(string ClientId, string ClientSecret)> GetOAuthClients()
    {
        lock (_discoveryLock)
        {
            if (_discoveredClients != null) return _discoveredClients;

            var list = new List<(string ClientId, string ClientSecret)>();

            // 1. Environment variable override
            string? envClientId = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID");
            string? envClientSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET");
            if (!string.IsNullOrEmpty(envClientId) && !string.IsNullOrEmpty(envClientSecret))
            {
                list.Add((envClientId, envClientSecret));
            }

            // 2. Discover dynamically from local agy binary installation
            var binaryClients = DiscoverClientsFromLocalAgyBinary();
            list.AddRange(binaryClients);

            _discoveredClients = list;
            return _discoveredClients;
        }
    }

    private static List<(string ClientId, string ClientSecret)> DiscoverClientsFromLocalAgyBinary()
    {
        var results = new List<(string ClientId, string ClientSecret)>();

        var candidatePaths = new List<string>();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsWindows())
        {
            candidatePaths.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
            candidatePaths.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy.exe"));
        }
        else
        {
            candidatePaths.Add(Path.Combine(userProfile, ".local", "bin", "agy"));
            candidatePaths.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy"));
            candidatePaths.Add("/usr/local/bin/agy");
            candidatePaths.Add("/usr/bin/agy");
        }

        // Search in system PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            string exeName = OperatingSystem.IsWindows() ? "agy.exe" : "agy";
            foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (!candidatePaths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        candidatePaths.Add(candidate);
                    }
                }
                catch { }
            }
        }

        var clientRegex = new System.Text.RegularExpressions.Regex(@"([0-9]{11,14}-[0-9a-z]{32}\.apps\.googleusercontent\.com)", System.Text.RegularExpressions.RegexOptions.Compiled);
        var secretRegex = new System.Text.RegularExpressions.Regex(@"(GOCSPX-[0-9a-zA-Z\-_]{28})", System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    string str = System.Text.Encoding.Latin1.GetString(bytes);

                    var clientMatches = clientRegex.Matches(str);
                    var secretMatches = secretRegex.Matches(str);

                    var clientIds = clientMatches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                    var secrets = secretMatches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

                    foreach (var cid in clientIds)
                    {
                        foreach (var sec in secrets)
                        {
                            if (!results.Any(r => r.ClientId == cid && r.ClientSecret == sec))
                            {
                                results.Add((cid, sec));
                            }
                        }
                    }

                    if (results.Count > 0) break;
                }
                catch { }
            }
        }

        return results;
    }

    private void PersistRefreshedTokens()
    {
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(_cachedAccessToken))
        {
            try
            {
                var tokenDict = new Dictionary<string, object>
                {
                    ["access_token"] = _cachedAccessToken,
                    ["token_type"] = "Bearer"
                };
                if (!string.IsNullOrEmpty(_cachedRefreshToken))
                {
                    tokenDict["refresh_token"] = _cachedRefreshToken;
                }
                tokenDict["expiry"] = DateTime.UtcNow.AddHours(1).ToString("o");

                var rootDict = new Dictionary<string, object>
                {
                    ["token"] = tokenDict,
                    ["auth_method"] = "oauth"
                };

                string json = JsonSerializer.Serialize(rootDict);
                Win32CredMan.WriteCredential("gemini:antigravity", json);
            }
            catch { }
        }
    }

    private static string? ExtractEmailFromJwt(string jwt)
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
                if (root.TryGetProperty("email", out var emailProp))
                {
                    return emailProp.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractProjectId(JsonElement root)
    {
        if (root.TryGetProperty("project", out var p)) return p.GetString();
        if (root.TryGetProperty("projectId", out var p2)) return p2.GetString();
        if (root.TryGetProperty("cloudaicompanionProject", out var cac))
        {
            if (cac.ValueKind == JsonValueKind.String) return cac.GetString();
            if (cac.TryGetProperty("id", out var id)) return id.GetString();
        }
        return null;
    }

    private static string ExtractPlanType(JsonElement root)
    {
        if (root.TryGetProperty("paidTier", out var pt) && pt.ValueKind == JsonValueKind.Object)
        {
            string id = pt.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
            string name = pt.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
            if (id.Contains("pro", StringComparison.OrdinalIgnoreCase) || name.Contains("pro", StringComparison.OrdinalIgnoreCase))
            {
                return "Pro";
            }
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (root.TryGetProperty("currentTier", out var ct) && ct.ValueKind == JsonValueKind.Object)
        {
            string id = ct.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
            string name = ct.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
            if (id.Contains("pro", StringComparison.OrdinalIgnoreCase) || name.Contains("pro", StringComparison.OrdinalIgnoreCase))
            {
                return "Pro";
            }
            if (id.Contains("standard", StringComparison.OrdinalIgnoreCase))
            {
                return "Standard";
            }
        }

        return "Pro";
    }

    private async Task<(HttpStatusCode StatusCode, JsonDocument? Document)> PostJsonAsync(
        string url,
        string token,
        string clientMetadata,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/1.11.5 windows/amd64");
            req.Headers.TryAddWithoutValidation("x-client-metadata", clientMetadata);
            req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                string respJson = await resp.Content.ReadAsStringAsync(cancellationToken);
                return (resp.StatusCode, JsonDocument.Parse(respJson));
            }

            return (resp.StatusCode, null);
        }
        catch
        {
            return (HttpStatusCode.InternalServerError, null);
        }
    }
}
