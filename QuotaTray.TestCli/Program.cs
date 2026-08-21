using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuotaTray.Core.Providers;
using QuotaTray.Core.Security;

namespace QuotaTray.TestCli;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("refresh-poc", StringComparison.OrdinalIgnoreCase))
        {
            await RunExistingSessionRefreshPocAsync();
            return;
        }

        var provider = new AntigravityQuotaProvider();
        var result = await provider.FetchQuotaAsync();
        Console.WriteLine($"IsSuccess: {result.IsSuccess}");
        Console.WriteLine($"AuthStatus: {result.AuthStatus}");
        Console.WriteLine($"ErrorMessage: {result.ErrorMessage}");
        Console.WriteLine($"PrimaryRemainingPercent: {result.PrimaryRemainingPercent}%");
        Console.WriteLine($"ResetText: {result.ResetText}");
    }

    private static async Task RunExistingSessionRefreshPocAsync()
    {
        string? clientId = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID");
        string? clientSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(clientId))
        {
            Console.WriteLine("Missing ANTIGRAVITY_OAUTH_CLIENT_ID.");
            Console.WriteLine("The PoC bootstrap could not discover the Antigravity OAuth client ID.");
            Environment.ExitCode = 2;
            return;
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            Console.WriteLine("Missing ANTIGRAVITY_OAUTH_CLIENT_SECRET.");
            Console.WriteLine("Google rejected the previous client-id-only test with 'client_secret is missing'.");
            Console.WriteLine("For this maintainer-only PoC, provide the OAuth client secret through the local environment.");
            Console.WriteLine("Do not commit the value to the repository.");
            Environment.ExitCode = 3;
            return;
        }

        string? credentialJson = TryLoadAntigravityCredential();
        if (string.IsNullOrWhiteSpace(credentialJson))
        {
            Console.WriteLine("Could not find an existing Antigravity credential.");
            Console.WriteLine("Sign in to Antigravity/agy once, then rerun this PoC.");
            Environment.ExitCode = 4;
            return;
        }

        string? refreshToken = ExtractRefreshToken(credentialJson);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            Console.WriteLine("Existing Antigravity credential was found, but it did not contain a refresh_token.");
            Environment.ExitCode = 5;
            return;
        }

        Console.WriteLine("Existing-session refresh PoC");
        Console.WriteLine("Credential source: existing Antigravity login");
        Console.WriteLine("agy process: NOT used");
        Console.WriteLine("OAuth client ID: auto-discovered for PoC bootstrap");
        Console.WriteLine("OAuth client secret: supplied locally; never printed");
        Console.WriteLine("Access/refresh tokens: never printed");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string? accessToken = await RefreshAccessTokenAsync(http, clientId, clientSecret, refreshToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Console.WriteLine();
            Console.WriteLine("POC FAILED: Google did not accept the existing Antigravity refresh session.");
            Environment.ExitCode = 6;
            return;
        }

        Console.WriteLine("Refresh: OK");

        var loadResult = await CallLoadCodeAssistAsync(http, accessToken);
        Console.WriteLine($"loadCodeAssist: HTTP {(int)loadResult.StatusCode} {loadResult.StatusCode}");
        if (loadResult.StatusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(loadResult.ProjectId))
        {
            if (!string.IsNullOrWhiteSpace(loadResult.ErrorSnippet))
            {
                Console.WriteLine($"Response: {loadResult.ErrorSnippet}");
            }

            Console.WriteLine();
            Console.WriteLine("POC PARTIAL: refresh worked, but Cloud Code Assist did not accept/resolve the refreshed session.");
            Environment.ExitCode = 7;
            return;
        }

        Console.WriteLine("loadCodeAssist project discovery: OK");

        var quotaResult = await CallQuotaSummaryAsync(http, accessToken, loadResult.ProjectId);
        Console.WriteLine($"retrieveUserQuotaSummary: HTTP {(int)quotaResult.StatusCode} {quotaResult.StatusCode}");
        if (quotaResult.StatusCode != HttpStatusCode.OK)
        {
            if (!string.IsNullOrWhiteSpace(quotaResult.ErrorSnippet))
            {
                Console.WriteLine($"Response: {quotaResult.ErrorSnippet}");
            }
            Environment.ExitCode = 8;
            return;
        }

        Console.WriteLine();
        Console.WriteLine("POC SUCCESS: QuotaTray can refresh and query an existing Antigravity session without running agy.");
    }

    private static string? TryLoadAntigravityCredential()
    {
        string? raw = null;

        if (OperatingSystem.IsWindows())
        {
            raw = Win32CredMan.ReadCredential("gemini:antigravity")
                  ?? Win32CredMan.ReadCredential("antigravity:oauth")
                  ?? Win32CredMan.ReadCredential("google:cloudcode");
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            return DecodeGoKeyringPayload(raw);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidatePaths =
        {
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(userProfile, ".config", "antigravity-cli", "antigravity-oauth-token")
        };

        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                return DecodeGoKeyringPayload(File.ReadAllText(path));
            }
            catch
            {
            }
        }

        return null;
    }

    private static string DecodeGoKeyringPayload(string raw)
    {
        const string prefix = "go-keyring-base64:";
        string trimmed = raw.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return trimmed;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(trimmed[prefix.Length..].Trim()));
        }
        catch
        {
            return trimmed;
        }
    }

    private static string? ExtractRefreshToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? refreshToken = ReadString(root, "refresh_token");
            if (root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.Object)
            {
                refreshToken ??= ReadString(token, "refresh_token");
            }
            if (root.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object)
            {
                refreshToken ??= ReadString(oauth, "refresh_token");
            }

            return refreshToken;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static async Task<string?> RefreshAccessTokenAsync(
        HttpClient http,
        string clientId,
        string clientSecret,
        string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken
            })
        };

        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Refresh failed: HTTP {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine($"Response: {SafeOAuthError(body)}");
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("access_token", out var accessTokenProp)
            ? accessTokenProp.GetString()
            : null;
    }

    private static async Task<(HttpStatusCode StatusCode, string? ProjectId, string? ErrorSnippet)> CallLoadCodeAssistAsync(
        HttpClient http,
        string accessToken)
    {
        const string url = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
        using var request = CreateCloudCodeRequest(HttpMethod.Post, url, accessToken);
        request.Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null, Truncate(body));
        }

        using var doc = JsonDocument.Parse(body);
        return (response.StatusCode, ExtractProjectId(doc.RootElement), null);
    }

    private static async Task<(HttpStatusCode StatusCode, string? ErrorSnippet)> CallQuotaSummaryAsync(
        HttpClient http,
        string accessToken,
        string projectId)
    {
        const string url = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
        using var request = CreateCloudCodeRequest(HttpMethod.Post, url, accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { project = projectId }),
            Encoding.UTF8,
            "application/json");

        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return response.IsSuccessStatusCode
            ? (response.StatusCode, null)
            : (response.StatusCode, Truncate(body));
    }

    private static HttpRequestMessage CreateCloudCodeRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", "antigravity/1.11.5 windows/amd64");
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Client", "google-cloud-sdk vscode_cloudshelleditor/0.1");
        request.Headers.TryAddWithoutValidation("Client-Metadata", "{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"WINDOWS\",\"pluginType\":\"GEMINI\"}");
        return request;
    }

    private static string? ExtractProjectId(JsonElement root)
    {
        if (root.TryGetProperty("project", out var project)) return project.GetString();
        if (root.TryGetProperty("projectId", out var projectId)) return projectId.GetString();
        if (root.TryGetProperty("cloudaicompanionProject", out var companionProject))
        {
            if (companionProject.ValueKind == JsonValueKind.String) return companionProject.GetString();
            if (companionProject.ValueKind == JsonValueKind.Object && companionProject.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        return null;
    }

    private static string SafeOAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? error = ReadString(doc.RootElement, "error");
            string? description = ReadString(doc.RootElement, "error_description");
            if (!string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(description))
            {
                return $"{error}: {description}";
            }
            if (!string.IsNullOrWhiteSpace(error)) return error;
        }
        catch
        {
        }

        return $"HTTP error response ({body.Length} bytes; body hidden)";
    }

    private static string Truncate(string value, int maxLength = 800)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }
}
