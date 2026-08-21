using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public string AuthMethod => "Antigravity OAuth";
    public string? CliLoginCommand => "agy auth login";
    public bool RequiresApiKey => false;

    private static readonly string[] BaseUrls =
    {
        "https://cloudcode-pa.googleapis.com",
        "https://daily-cloudcode-pa.sandbox.googleapis.com"
    };

    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
    private const string KeyringBase64Prefix = "go-keyring-base64:";

    private static readonly Regex ClientIdRegex = new(
        @"([0-9]{6,}-[0-9A-Za-z._-]+\.apps\.googleusercontent\.com)",
        RegexOptions.Compiled);

    private static readonly Regex ClientSecretRegex = new(
        @"(GOCSPX-[0-9A-Za-z_-]{20,64})",
        RegexOptions.Compiled);

    private static readonly object OAuthDiscoveryLock = new();
    private static List<(string ClientId, string ClientSecret)>? _cachedBinaryOAuthClients;

    private readonly HttpClient _httpClient;
    private string? _cachedAccessToken;
    private string? _cachedRefreshToken;
    private string? _cachedProjectId;
    private string? _cachedEmail;
    private string? _customApiKey;
    private string? _lastRefreshError;

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
        var result = CreateBaseResult();

        try
        {
            string? tokenToUse = _customApiKey;

            if (string.IsNullOrWhiteSpace(tokenToUse))
            {
                LoadStoredCredentials();
                tokenToUse = _cachedAccessToken;
            }

            if (string.IsNullOrWhiteSpace(tokenToUse) && string.IsNullOrWhiteSpace(_cachedRefreshToken))
            {
                return Fail(result, ProviderAuthStatus.NotConfigured, "No credentials found. Run 'agy auth login'", authMissing: true);
            }

            if (string.IsNullOrWhiteSpace(tokenToUse) && CanRefresh())
            {
                if (await TryRefreshTokenAsync(cancellationToken))
                {
                    tokenToUse = _cachedAccessToken;
                }
                else
                {
                    return Fail(result, ProviderAuthStatus.Error, _lastRefreshError ?? "Could not refresh the Antigravity OAuth session");
                }
            }

            if (string.IsNullOrWhiteSpace(tokenToUse))
            {
                return Fail(result, ProviderAuthStatus.NotConfigured, "No Antigravity credentials found", authMissing: true);
            }

            string? lastError = null;

            foreach (string baseUrl in BaseUrls)
            {
                string loadUrl = $"{baseUrl}/v1internal:loadCodeAssist";
                string quotaSummaryUrl = $"{baseUrl}/v1internal:retrieveUserQuotaSummary";

                var (loadStatus, loadDoc) = await PostJsonAsync(
                    loadUrl,
                    tokenToUse,
                    "antigravity/windows/amd64",
                    "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                    cancellationToken);

                if (loadStatus == HttpStatusCode.Unauthorized && CanRefresh())
                {
                    loadDoc?.Dispose();
                    loadDoc = null;

                    if (await TryRefreshTokenAsync(cancellationToken) && !string.IsNullOrWhiteSpace(_cachedAccessToken))
                    {
                        tokenToUse = _cachedAccessToken;
                        (loadStatus, loadDoc) = await PostJsonAsync(
                            loadUrl,
                            tokenToUse,
                            "antigravity/windows/amd64",
                            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                            cancellationToken);
                    }
                    else
                    {
                        lastError = _lastRefreshError ?? "Antigravity OAuth refresh failed";
                    }
                }

                if (loadStatus != HttpStatusCode.OK || loadDoc == null)
                {
                    lastError ??= $"loadCodeAssist failed ({loadStatus})";
                    loadDoc?.Dispose();
                    continue;
                }

                using (loadDoc)
                {
                    _cachedProjectId = ExtractProjectId(loadDoc.RootElement);
                    result.PlanType = ExtractPlanType(loadDoc.RootElement);
                }

                if (string.IsNullOrWhiteSpace(_cachedProjectId))
                {
                    lastError = "Could not find Cloud Code Assist project";
                    continue;
                }

                var (summaryStatus, summaryDoc) = await PostJsonAsync(
                    quotaSummaryUrl,
                    tokenToUse,
                    "antigravity/1.11.5 windows/amd64",
                    JsonSerializer.Serialize(new { project = _cachedProjectId }),
                    cancellationToken);

                if (summaryStatus == HttpStatusCode.Unauthorized && CanRefresh())
                {
                    summaryDoc?.Dispose();
                    summaryDoc = null;

                    if (await TryRefreshTokenAsync(cancellationToken) && !string.IsNullOrWhiteSpace(_cachedAccessToken))
                    {
                        tokenToUse = _cachedAccessToken;
                        (summaryStatus, summaryDoc) = await PostJsonAsync(
                            quotaSummaryUrl,
                            tokenToUse,
                            "antigravity/1.11.5 windows/amd64",
                            JsonSerializer.Serialize(new { project = _cachedProjectId }),
                            cancellationToken);
                    }
                    else
                    {
                        lastError = _lastRefreshError ?? "Antigravity OAuth refresh failed";
                    }
                }

                if (summaryStatus != HttpStatusCode.OK || summaryDoc == null)
                {
                    lastError ??= $"retrieveUserQuotaSummary failed ({summaryStatus})";
                    summaryDoc?.Dispose();
                    continue;
                }

                List<QuotaGroup> groups;
                using (summaryDoc)
                {
                    groups = ParseQuotaGroups(summaryDoc.RootElement);
                }

                if (groups.Count == 0)
                {
                    groups = CreateFallbackGroups();
                }

                ApplyQuotaResult(result, groups);
                result.AuthStatus = ProviderAuthStatus.Connected;
                result.IsSuccess = true;
                return result;
            }

            return Fail(result, ProviderAuthStatus.Error, lastError ?? "Failed to fetch Antigravity quota");
        }
        catch (OperationCanceledException)
        {
            return Fail(result, ProviderAuthStatus.Error, "Request timed out");
        }
        catch (Exception ex)
        {
            return Fail(result, ProviderAuthStatus.Error, ex.Message);
        }
    }

    private ProviderQuotaResult CreateBaseResult()
    {
        return new ProviderQuotaResult
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
    }

    private static ProviderQuotaResult Fail(
        ProviderQuotaResult result,
        ProviderAuthStatus status,
        string message,
        bool authMissing = false)
    {
        result.IsSuccess = false;
        result.AuthStatus = status;
        result.IsAuthMissing = authMissing;
        result.ErrorMessage = message;
        return result;
    }

    private bool CanRefresh()
    {
        return !string.IsNullOrWhiteSpace(_cachedRefreshToken) && string.IsNullOrWhiteSpace(_customApiKey);
    }

    private void LoadStoredCredentials()
    {
        _cachedAccessToken = Environment.GetEnvironmentVariable("ANTIGRAVITY_ACCESS_TOKEN")
                             ?? Environment.GetEnvironmentVariable("GEMINI_ACCESS_TOKEN");
        _cachedRefreshToken = Environment.GetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKEN")
                              ?? Environment.GetEnvironmentVariable("GEMINI_REFRESH_TOKEN");

        if (!string.IsNullOrWhiteSpace(_cachedRefreshToken))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            string? credential = Win32CredMan.ReadCredential("gemini:antigravity")
                                 ?? Win32CredMan.ReadCredential("antigravity:oauth")
                                 ?? Win32CredMan.ReadCredential("google:cloudcode");

            if (!string.IsNullOrWhiteSpace(credential))
            {
                ExtractTokensFromJson(NormalizeCredentialPayload(credential));
                if (!string.IsNullOrWhiteSpace(_cachedRefreshToken))
                {
                    return;
                }
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidatePaths =
        {
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(userProfile, ".config", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(userProfile, ".antigravity", "auth.json"),
            Path.Combine(userProfile, ".antigravity", "credentials.json"),
            Path.Combine(userProfile, ".config", "antigravity", "auth.json"),
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "auth.json"),
            Path.Combine(userProfile, ".gemini", "oauth.json"),
            Path.Combine(appData, "antigravity", "auth.json"),
            Path.Combine(localAppData, "antigravity", "auth.json")
        };

        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                ExtractTokensFromJson(NormalizeCredentialPayload(File.ReadAllText(path)));
                if (!string.IsNullOrWhiteSpace(_cachedAccessToken) || !string.IsNullOrWhiteSpace(_cachedRefreshToken))
                {
                    return;
                }
            }
            catch
            {
                // Ignore malformed or unreadable credential candidates.
            }
        }
    }

    private static string NormalizeCredentialPayload(string raw)
    {
        string trimmed = raw.Trim();
        if (!trimmed.StartsWith(KeyringBase64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        string encoded = trimmed[KeyringBase64Prefix.Length..].Trim();
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return trimmed;
        }
    }

    private void ExtractTokensFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            ReadTokenObject(root);

            if (root.TryGetProperty("token", out JsonElement token))
            {
                if (token.ValueKind == JsonValueKind.String)
                {
                    _cachedAccessToken = token.GetString();
                }
                else if (token.ValueKind == JsonValueKind.Object)
                {
                    ReadTokenObject(token);
                }
            }

            if (root.TryGetProperty("oauth", out JsonElement oauth) && oauth.ValueKind == JsonValueKind.Object)
            {
                ReadTokenObject(oauth);
            }

            if (root.TryGetProperty("project_id", out JsonElement projectId)) _cachedProjectId = projectId.GetString();
            if (root.TryGetProperty("projectId", out JsonElement projectId2)) _cachedProjectId = projectId2.GetString();

            if (root.TryGetProperty("id_token", out JsonElement idToken))
            {
                TryUpdateEmail(idToken.GetString());
            }

            if (string.IsNullOrWhiteSpace(_cachedEmail))
            {
                TryUpdateEmail(_cachedAccessToken);
            }
        }
        catch
        {
            // Ignore malformed credential payloads.
        }
    }

    private void ReadTokenObject(JsonElement element)
    {
        if (element.TryGetProperty("access_token", out JsonElement accessToken))
        {
            _cachedAccessToken = accessToken.GetString();
        }

        if (element.TryGetProperty("refresh_token", out JsonElement refreshToken))
        {
            _cachedRefreshToken = refreshToken.GetString();
        }

        if (element.TryGetProperty("id_token", out JsonElement idToken))
        {
            TryUpdateEmail(idToken.GetString());
        }
    }

    private void TryUpdateEmail(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return;
        }

        string? email = ExtractEmailFromJwt(jwt);
        if (!string.IsNullOrWhiteSpace(email))
        {
            _cachedEmail = email;
        }
    }

    private async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
    {
        _lastRefreshError = null;

        if (string.IsNullOrWhiteSpace(_cachedRefreshToken))
        {
            _lastRefreshError = "No Antigravity refresh token is available";
            return false;
        }

        List<(string ClientId, string ClientSecret)> clients = GetOAuthClients();
        if (clients.Count == 0)
        {
            _lastRefreshError = "Could not read Antigravity OAuth client configuration from the installed agy binary. agy does not need to be running, but it must be installed.";
            return false;
        }

        string? lastError = null;

        foreach ((string clientId, string clientSecret) in clients)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, GoogleTokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["refresh_token"] = _cachedRefreshToken
                    })
                };

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    lastError = DescribeOAuthError(response.StatusCode, body);
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out JsonElement accessToken))
                {
                    lastError = "OAuth refresh response did not contain an access token";
                    continue;
                }

                _cachedAccessToken = accessToken.GetString();
                if (string.IsNullOrWhiteSpace(_cachedAccessToken))
                {
                    lastError = "OAuth refresh response contained an empty access token";
                    continue;
                }

                if (root.TryGetProperty("refresh_token", out JsonElement newRefreshToken))
                {
                    string? value = newRefreshToken.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _cachedRefreshToken = value;
                    }
                }

                if (root.TryGetProperty("id_token", out JsonElement idToken))
                {
                    TryUpdateEmail(idToken.GetString());
                }

                // Antigravity owns its credential entry. QuotaTray refreshes independently
                // but deliberately does not overwrite gemini:antigravity.
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = $"Antigravity OAuth refresh failed: {ex.Message}";
            }
        }

        _lastRefreshError = lastError ?? "Antigravity OAuth refresh failed";
        return false;
    }

    private static List<(string ClientId, string ClientSecret)> GetOAuthClients()
    {
        var result = new List<(string ClientId, string ClientSecret)>();

        string? envClientId = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID")
                              ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID");
        string? envClientSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET")
                                  ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_SECRET");

        if (!string.IsNullOrWhiteSpace(envClientId) && !string.IsNullOrWhiteSpace(envClientSecret))
        {
            result.Add((envClientId, envClientSecret));
        }

        lock (OAuthDiscoveryLock)
        {
            _cachedBinaryOAuthClients ??= DiscoverClientsFromLocalAgyBinary();
            foreach (var pair in _cachedBinaryOAuthClients)
            {
                if (!result.Contains(pair))
                {
                    result.Add(pair);
                }
            }
        }

        return result;
    }

    private static List<(string ClientId, string ClientSecret)> DiscoverClientsFromLocalAgyBinary()
    {
        var results = new List<(string ClientId, string ClientSecret)>();

        foreach (string path in GetCandidateAgyPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var clientIds = new HashSet<string>(StringComparer.Ordinal);
                var clientSecrets = new HashSet<string>(StringComparer.Ordinal);

                foreach (Encoding encoding in new[] { Encoding.Latin1, Encoding.Unicode })
                {
                    string text = encoding.GetString(bytes);
                    foreach (Match match in ClientIdRegex.Matches(text))
                    {
                        clientIds.Add(match.Groups[1].Value);
                    }
                    foreach (Match match in ClientSecretRegex.Matches(text))
                    {
                        clientSecrets.Add(match.Groups[1].Value);
                    }
                }

                foreach (string clientId in clientIds)
                {
                    foreach (string clientSecret in clientSecrets)
                    {
                        results.Add((clientId, clientSecret));
                    }
                }

                if (results.Count > 0)
                {
                    break;
                }
            }
            catch
            {
                // Keep searching other installed agy candidates.
            }
        }

        return results;
    }

    private static IEnumerable<string> GetCandidateAgyPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy.exe"));
        }
        else
        {
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy"));
            candidates.Add("/usr/local/bin/agy");
            candidates.Add("/usr/bin/agy");
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            char separator = OperatingSystem.IsWindows() ? ';' : ':';
            string executableName = OperatingSystem.IsWindows() ? "agy.exe" : "agy";
            foreach (string directory in pathValue.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(directory, executableName));
                }
                catch
                {
                }
            }
        }

        foreach (string candidate in candidates)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static string DescribeOAuthError(HttpStatusCode statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString()
                : null;
            string? description = doc.RootElement.TryGetProperty("error_description", out JsonElement descriptionElement)
                ? descriptionElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(description))
            {
                return $"OAuth refresh failed ({statusCode}): {error}: {description}";
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return $"OAuth refresh failed ({statusCode}): {error}";
            }
        }
        catch
        {
        }

        return $"OAuth refresh failed ({statusCode})";
    }

    private void ApplyQuotaResult(ProviderQuotaResult result, List<QuotaGroup> groups)
    {
        result.Groups = groups;
        result.Windows = groups.SelectMany(group => group.Windows).ToList();

        QuotaGroup? geminiGroup = groups.FirstOrDefault(group =>
            group.GroupId == "gemini" || group.GroupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        QuotaGroup? claudeGroup = groups.FirstOrDefault(group =>
            group.GroupId == "claude_gpt"
            || group.GroupName.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            || group.GroupName.Contains("GPT", StringComparison.OrdinalIgnoreCase));

        QuotaWindow? geminiWeekly = geminiGroup?.Windows.FirstOrDefault(window =>
            window.Name.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
        QuotaWindow? claudeWeekly = claudeGroup?.Windows.FirstOrDefault(window =>
            window.Name.Equals("Weekly", StringComparison.OrdinalIgnoreCase));

        QuotaWindow? chosenWeekly = null;
        if (geminiWeekly != null && claudeWeekly != null)
        {
            chosenWeekly = geminiWeekly.RemainingPercent <= claudeWeekly.RemainingPercent
                ? geminiWeekly
                : claudeWeekly;
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
            QuotaGroup tightestGroup = groups.OrderBy(group => group.PrimaryRemainingPercent).First();
            result.PrimaryRemainingPercent = tightestGroup.PrimaryRemainingPercent;
            result.ResetText = tightestGroup.ResetText;
            result.FormattedNextResetIn = tightestGroup.ResetText;
        }
        else
        {
            result.PrimaryRemainingPercent = 100.0;
            result.ResetText = "Available";
        }

        if (!string.IsNullOrWhiteSpace(_cachedEmail))
        {
            result.AccountEmail = _cachedEmail;
            result.DetailsSubtitle = _cachedEmail;
        }
        else
        {
            result.DetailsSubtitle = "Gemini";
        }
    }

    private static List<QuotaGroup> ParseQuotaGroups(JsonElement root)
    {
        var resultGroups = new List<QuotaGroup>();
        if (!root.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return resultGroups;
        }

        foreach (JsonElement groupElement in groups.EnumerateArray())
        {
            string rawName = groupElement.TryGetProperty("displayName", out JsonElement displayName)
                ? displayName.GetString() ?? string.Empty
                : string.Empty;
            bool isGemini = rawName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);

            var group = new QuotaGroup
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

            if (groupElement.TryGetProperty("buckets", out JsonElement buckets) && buckets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement bucket in buckets.EnumerateArray())
                {
                    string windowType = bucket.TryGetProperty("window", out JsonElement window)
                        ? window.GetString() ?? string.Empty
                        : string.Empty;
                    string bucketId = bucket.TryGetProperty("bucketId", out JsonElement bucketIdElement)
                        ? bucketIdElement.GetString() ?? string.Empty
                        : string.Empty;
                    double remainingFraction = bucket.TryGetProperty("remainingFraction", out JsonElement fraction)
                        ? fraction.GetDouble()
                        : 1.0;

                    long resetInSeconds = 0;
                    if (bucket.TryGetProperty("resetTime", out JsonElement resetTime) && resetTime.ValueKind == JsonValueKind.String)
                    {
                        string? resetText = resetTime.GetString();
                        if (!string.IsNullOrWhiteSpace(resetText) && DateTimeOffset.TryParse(resetText, out DateTimeOffset resetAt))
                        {
                            resetInSeconds = Math.Max(0, (long)(resetAt - DateTimeOffset.UtcNow).TotalSeconds);
                        }
                    }

                    double remainingPercent = Math.Clamp(Math.Round(remainingFraction * 100.0), 0.0, 100.0);
                    string name = windowType.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                                  || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase)
                        ? "Weekly"
                        : "5h";

                    if (group.Windows.All(existing => existing.Name != name))
                    {
                        group.Windows.Add(new QuotaWindow
                        {
                            Name = name,
                            LimitWindowSeconds = name == "Weekly" ? 604800 : 18000,
                            UsedPercent = Math.Max(0.0, 100.0 - remainingPercent),
                            RemainingPercent = remainingPercent,
                            ResetInSeconds = resetInSeconds,
                            FormattedResetIn = TimeFormatter.FormatResetText(resetInSeconds)
                        });
                    }
                }
            }

            group.Windows.Sort((left, right) => left.LimitWindowSeconds.CompareTo(right.LimitWindowSeconds));
            resultGroups.Add(group);
        }

        return resultGroups;
    }

    private static List<QuotaGroup> CreateFallbackGroups()
    {
        return new List<QuotaGroup>
        {
            new()
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
                    new() { Name = "5h", RemainingPercent = 100.0, FormattedResetIn = "Available" },
                    new() { Name = "Weekly", RemainingPercent = 100.0, FormattedResetIn = "Available" }
                }
            },
            new()
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
                    new() { Name = "5h", RemainingPercent = 100.0, FormattedResetIn = "Available" },
                    new() { Name = "Weekly", RemainingPercent = 100.0, FormattedResetIn = "Available" }
                }
            }
        };
    }

    private static string? ExtractEmailFromJwt(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
            }

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("email", out JsonElement email)
                ? email.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractProjectId(JsonElement root)
    {
        if (root.TryGetProperty("project", out JsonElement project)) return project.GetString();
        if (root.TryGetProperty("projectId", out JsonElement projectId)) return projectId.GetString();

        if (root.TryGetProperty("cloudaicompanionProject", out JsonElement companionProject))
        {
            if (companionProject.ValueKind == JsonValueKind.String) return companionProject.GetString();
            if (companionProject.ValueKind == JsonValueKind.Object && companionProject.TryGetProperty("id", out JsonElement id))
            {
                return id.GetString();
            }
        }

        return null;
    }

    private static string ExtractPlanType(JsonElement root)
    {
        if (root.TryGetProperty("paidTier", out JsonElement paidTier) && paidTier.ValueKind == JsonValueKind.Object)
        {
            string id = paidTier.TryGetProperty("id", out JsonElement paidId) ? paidId.GetString() ?? string.Empty : string.Empty;
            string name = paidTier.TryGetProperty("name", out JsonElement paidName) ? paidName.GetString() ?? string.Empty : string.Empty;

            if (id.Contains("pro", StringComparison.OrdinalIgnoreCase) || name.Contains("pro", StringComparison.OrdinalIgnoreCase))
            {
                return "Pro";
            }

            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        if (root.TryGetProperty("currentTier", out JsonElement currentTier) && currentTier.ValueKind == JsonValueKind.Object)
        {
            string id = currentTier.TryGetProperty("id", out JsonElement currentId) ? currentId.GetString() ?? string.Empty : string.Empty;
            string name = currentTier.TryGetProperty("name", out JsonElement currentName) ? currentName.GetString() ?? string.Empty : string.Empty;

            if (id.Contains("pro", StringComparison.OrdinalIgnoreCase) || name.Contains("pro", StringComparison.OrdinalIgnoreCase))
            {
                return "Pro";
            }

            if (id.Contains("standard", StringComparison.OrdinalIgnoreCase))
            {
                return "Standard";
            }

            if (!string.IsNullOrWhiteSpace(name)) return name;
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
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "antigravity/1.11.5 windows/amd64");
            request.Headers.TryAddWithoutValidation("x-client-metadata", clientMetadata);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (response.StatusCode, null);
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.StatusCode, JsonDocument.Parse(responseJson));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (HttpStatusCode.InternalServerError, null);
        }
    }
}
