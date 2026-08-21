using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public string AuthMethod => "Antigravity CLI / OAuth";
    public string? CliLoginCommand => "agy auth login";
    public bool RequiresApiKey => false;

    private static readonly string[] BaseUrls =
    {
        "https://cloudcode-pa.googleapis.com",
        "https://daily-cloudcode-pa.sandbox.googleapis.com",
        "https://autopush-cloudcode-pa.sandbox.googleapis.com"
    };

    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
    private const string GoKeyringBase64Prefix = "go-keyring-base64:";

    private readonly HttpClient _httpClient;
    private string? _cachedAccessToken;
    private string? _cachedRefreshToken;
    private string? _cachedProjectId;
    private string? _customApiKey;
    private DateTimeOffset? _cachedExpiry;
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
            bool refreshAttempted = false;

            if (string.IsNullOrEmpty(tokenToUse))
            {
                LoadStoredCredentials(resetRefreshError: true);
                tokenToUse = _cachedAccessToken;

                // agy access tokens are short-lived. Refresh shortly before expiry so
                // the app's periodic polling does not routinely hit a 401 first.
                if (!string.IsNullOrEmpty(_cachedRefreshToken) && ShouldRefreshToken())
                {
                    refreshAttempted = true;
                    if (await TryRefreshTokenAsync(cancellationToken) && !string.IsNullOrEmpty(_cachedAccessToken))
                    {
                        tokenToUse = _cachedAccessToken;
                    }
                }
            }

            if (string.IsNullOrEmpty(tokenToUse) && string.IsNullOrEmpty(_cachedRefreshToken))
            {
                result.IsSuccess = false;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.IsAuthMissing = true;
                result.ErrorMessage = "No credentials found. Run 'agy auth login'";
                return result;
            }

            if (string.IsNullOrEmpty(tokenToUse) && !string.IsNullOrEmpty(_cachedRefreshToken))
            {
                refreshAttempted = true;
                await TryRefreshTokenAsync(cancellationToken);
                tokenToUse = _cachedAccessToken;
            }

            if (string.IsNullOrEmpty(tokenToUse))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = _lastRefreshError ?? "No Antigravity credentials found";
                return result;
            }

            string? lastError = null;

            foreach (var baseUrl in BaseUrls)
            {
                string loadUrl = $"{baseUrl}/v1internal:loadCodeAssist";
                string quotaSummaryUrl = $"{baseUrl}/v1internal:retrieveUserQuotaSummary";

                var (loadStatus, loadDoc) = await PostJsonAsync(
                    loadUrl,
                    tokenToUse,
                    "antigravity/windows/amd64",
                    "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                    cancellationToken);

                // If the stored access token was stale, ask agy to refresh its own
                // credentials (or use explicit OAuth env vars), reload them, and retry once.
                if (loadStatus == HttpStatusCode.Unauthorized
                    && !refreshAttempted
                    && !string.IsNullOrEmpty(_cachedRefreshToken)
                    && string.IsNullOrEmpty(_customApiKey))
                {
                    loadDoc?.Dispose();
                    loadDoc = null;
                    refreshAttempted = true;

                    if (await TryRefreshTokenAsync(cancellationToken) && !string.IsNullOrEmpty(_cachedAccessToken))
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

                if (loadStatus == HttpStatusCode.Unauthorized)
                {
                    loadDoc?.Dispose();
                    result.IsSuccess = false;
                    result.AuthStatus = ProviderAuthStatus.Error;
                    result.ErrorMessage = _lastRefreshError ?? "Antigravity session expired. Run 'agy auth login' again.";
                    return result;
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

                result.DetailsSubtitle = "Gemini";
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

    private void LoadStoredCredentials(bool resetRefreshError)
    {
        _cachedAccessToken = null;
        _cachedRefreshToken = null;
        _cachedExpiry = null;

        if (resetRefreshError)
        {
            _lastRefreshError = null;
        }

        // Explicit environment variables have highest priority for headless/CI use.
        _cachedAccessToken = Environment.GetEnvironmentVariable("ANTIGRAVITY_ACCESS_TOKEN")
                             ?? Environment.GetEnvironmentVariable("GEMINI_ACCESS_TOKEN");
        _cachedRefreshToken = Environment.GetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKEN")
                              ?? Environment.GetEnvironmentVariable("GEMINI_REFRESH_TOKEN");

        if (!string.IsNullOrEmpty(_cachedAccessToken) || !string.IsNullOrEmpty(_cachedRefreshToken))
        {
            return;
        }

        // Desktop agy uses the OS keyring by default. On Windows it is stored as
        // service=gemini/account=antigravity, i.e. target "gemini:antigravity".
        if (OperatingSystem.IsWindows())
        {
            string? cred = Win32CredMan.ReadCredential("gemini:antigravity")
                           ?? Win32CredMan.ReadCredential("antigravity:oauth")
                           ?? Win32CredMan.ReadCredential("google:cloudcode");
            if (!string.IsNullOrEmpty(cred) && ExtractTokensFromStoredSecret(cred))
            {
                return;
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Current agy headless/file-storage location first, then older compatibility paths.
        var candidatePaths = new List<string>
        {
            Environment.GetEnvironmentVariable("AGY_OAUTH_TOKEN_FILE") ?? string.Empty,
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

        foreach (var path in candidatePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (ExtractTokensFromStoredSecret(File.ReadAllText(path)))
                {
                    return;
                }
            }
            catch
            {
                // Try the next supported location.
            }
        }
    }

    private bool ExtractTokensFromStoredSecret(string rawSecret)
    {
        string json = rawSecret.Trim();

        if (json.StartsWith(GoKeyringBase64Prefix, StringComparison.Ordinal))
        {
            try
            {
                byte[] decoded = Convert.FromBase64String(json[GoKeyringBase64Prefix.Length..]);
                json = Encoding.UTF8.GetString(decoded);
            }
            catch
            {
                return false;
            }
        }

        return ExtractTokensFromJson(json);
    }

    private bool ExtractTokensFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var at)) _cachedAccessToken = at.GetString();
            if (root.TryGetProperty("refresh_token", out var rt)) _cachedRefreshToken = rt.GetString();
            if (root.TryGetProperty("project_id", out var pid)) _cachedProjectId = pid.GetString();
            if (root.TryGetProperty("projectId", out var pid2)) _cachedProjectId = pid2.GetString();
            if (root.TryGetProperty("expiry", out var rootExpiry)) TrySetExpiry(rootExpiry);
            if (root.TryGetProperty("expiry_date", out var rootExpiryDate)) TrySetExpiry(rootExpiryDate);

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
                    if (tok.TryGetProperty("expiry", out var expiry)) TrySetExpiry(expiry);
                    if (tok.TryGetProperty("expiry_date", out var expiryDate)) TrySetExpiry(expiryDate);
                }
            }

            if (root.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object)
            {
                if (oauth.TryGetProperty("access_token", out var oat)) _cachedAccessToken = oat.GetString();
                if (oauth.TryGetProperty("refresh_token", out var ort)) _cachedRefreshToken = ort.GetString();
                if (oauth.TryGetProperty("expiry", out var oauthExpiry)) TrySetExpiry(oauthExpiry);
                if (oauth.TryGetProperty("expiry_date", out var oauthExpiryDate)) TrySetExpiry(oauthExpiryDate);
            }

            return !string.IsNullOrEmpty(_cachedAccessToken) || !string.IsNullOrEmpty(_cachedRefreshToken);
        }
        catch
        {
            return false;
        }
    }

    private void TrySetExpiry(JsonElement expiryElement)
    {
        if (expiryElement.ValueKind == JsonValueKind.String)
        {
            string? raw = expiryElement.GetString();
            if (!string.IsNullOrEmpty(raw) && TryParseOAuthExpiry(raw, out var parsed))
            {
                _cachedExpiry = parsed;
            }
        }
        else if (expiryElement.ValueKind == JsonValueKind.Number && expiryElement.TryGetInt64(out long value))
        {
            _cachedExpiry = value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
    }

    private static bool TryParseOAuthExpiry(string raw, out DateTimeOffset parsed)
    {
        if (DateTimeOffset.TryParse(raw, out parsed))
        {
            return true;
        }

        // agy (Go) can write RFC3339Nano timestamps with 8-9 fractional digits,
        // while DateTimeOffset accepts at most 7. Trim only the excess precision.
        string normalized = Regex.Replace(raw, @"(\.\d{7})\d+", "$1");
        return DateTimeOffset.TryParse(normalized, out parsed);
    }

    private bool ShouldRefreshToken()
    {
        return _cachedExpiry.HasValue
               && _cachedExpiry.Value <= DateTimeOffset.UtcNow.AddMinutes(2);
    }

    private async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_cachedRefreshToken))
        {
            _lastRefreshError = "No Antigravity refresh token is available. Run 'agy auth login'.";
            return false;
        }

        // Advanced/headless override: allow a caller to supply the installed-app
        // OAuth client without shipping public OAuth credentials in this repository.
        if (await TryDirectRefreshFromEnvironmentAsync(cancellationToken))
        {
            return true;
        }

        // Normal desktop path: let agy refresh its own credential store, then read
        // the updated token back. This keeps QuotaTray decoupled from agy's OAuth client.
        if (await TryRefreshViaAgyAsync(cancellationToken))
        {
            return true;
        }

        _lastRefreshError ??= "Antigravity token refresh failed. Run 'agy auth login' again.";
        return false;
    }

    private async Task<bool> TryDirectRefreshFromEnvironmentAsync(CancellationToken cancellationToken)
    {
        string? clientId = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID");
        string? clientSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrEmpty(_cachedRefreshToken))
        {
            return false;
        }

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

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            string json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _lastRefreshError = BuildRefreshError(resp.StatusCode, json);
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("access_token", out var at) || string.IsNullOrEmpty(at.GetString()))
            {
                _lastRefreshError = "OAuth token refresh returned no access token.";
                return false;
            }

            _cachedAccessToken = at.GetString();
            long expiresIn = 3600;
            if (doc.RootElement.TryGetProperty("expires_in", out var expires)
                && expires.ValueKind == JsonValueKind.Number
                && expires.TryGetInt64(out long parsedExpiresIn))
            {
                expiresIn = parsedExpiresIn;
            }

            _cachedExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            _lastRefreshError = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastRefreshError = $"OAuth token refresh failed ({ex.GetType().Name}).";
            return false;
        }
    }

    private async Task<bool> TryRefreshViaAgyAsync(CancellationToken cancellationToken)
    {
        string agyExecutable = Environment.GetEnvironmentVariable("AGY_BIN") ?? "agy";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = agyExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("status");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                _lastRefreshError = "Could not start agy to refresh Antigravity credentials.";
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _lastRefreshError = "agy credential refresh timed out.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                _lastRefreshError = "agy could not refresh the Antigravity session. Run 'agy auth login'.";
                return false;
            }

            // auth status initializes agy's auth stack; reload whatever it persisted.
            LoadStoredCredentials(resetRefreshError: false);
            if (!string.IsNullOrEmpty(_cachedAccessToken))
            {
                _lastRefreshError = null;
                return true;
            }

            _lastRefreshError = "agy completed but no refreshed Antigravity token was found.";
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _lastRefreshError = "agy CLI was not available for credential refresh. Run 'agy auth login' or set AGY_BIN.";
            return false;
        }
    }

    private static string BuildRefreshError(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                string? code = error.GetString();
                if (!string.IsNullOrEmpty(code))
                {
                    return $"OAuth token refresh failed ({code}). Run 'agy auth login' if this persists.";
                }
            }
        }
        catch
        {
            // Do not expose arbitrary response bodies in the UI.
        }

        return $"OAuth token refresh failed ({(int)statusCode} {statusCode}).";
    }

    private static string? ExtractProjectId(JsonElement root)
    {
        if (root.TryGetProperty("project", out var p)) return p.GetString();
        if (root.TryGetProperty("projectId", out var p2)) return p2.GetString();
        if (root.TryGetProperty("cloudaicompanionProject", out var cac))
        {
            if (cac.ValueKind == JsonValueKind.String) return cac.GetString();
            if (cac.ValueKind == JsonValueKind.Object && cac.TryGetProperty("id", out var id)) return id.GetString();
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
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
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
