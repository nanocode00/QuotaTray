using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuotaTray.Core.Providers;

namespace QuotaTray.TestCli;

class Program
{
    private static readonly string[] AntigravityScopes =
    {
        "https://www.googleapis.com/auth/cloud-platform",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile",
        "https://www.googleapis.com/auth/cclog",
        "https://www.googleapis.com/auth/experimentsandconfigs"
    };

    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("oauth-poc", StringComparison.OrdinalIgnoreCase))
        {
            await RunNativeOAuthPocAsync();
            return;
        }

        var provider = new AntigravityQuotaProvider();
        var res = await provider.FetchQuotaAsync();
        Console.WriteLine($"IsSuccess: {res.IsSuccess}");
        Console.WriteLine($"AuthStatus: {res.AuthStatus}");
        Console.WriteLine($"ErrorMessage: {res.ErrorMessage}");
        Console.WriteLine($"PlanType: {res.PlanType}");
        Console.WriteLine($"AccountEmail: {res.AccountEmail}");
        Console.WriteLine($"DetailsSubtitle: {res.DetailsSubtitle}");
        Console.WriteLine($"PrimaryRemainingPercent: {res.PrimaryRemainingPercent}%");
        Console.WriteLine($"ResetText: {res.ResetText}");
        Console.WriteLine($"Windows Count: {res.Windows.Count}");
        foreach (var w in res.Windows)
        {
            Console.WriteLine($" - {w.Name}: {w.RemainingPercent}% (used: {w.UsedPercent}%, reset: {w.FormattedResetIn})");
        }
        Console.WriteLine($"Groups Count: {res.Groups.Count}");
        foreach (var g in res.Groups)
        {
            Console.WriteLine($" [Group] {g.GroupName}: {g.PrimaryRemainingPercentText}");
            foreach (var w in g.Windows)
            {
                Console.WriteLine($"   * {w.Name}: {w.RemainingPercent}% ({w.FormattedResetIn})");
            }
        }
    }

    private static async Task RunNativeOAuthPocAsync()
    {
        string? clientId = Environment.GetEnvironmentVariable("QUOTATRAY_GOOGLE_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Console.WriteLine("Missing QUOTATRAY_GOOGLE_CLIENT_ID.");
            Console.WriteLine("Create a Google OAuth Desktop client, then set its client ID in this environment variable.");
            Console.WriteLine("No OAuth client secret is used by this PoC.");
            Environment.ExitCode = 2;
            return;
        }

        int port = GetFreeLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/";
        string state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        string challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string scope = string.Join(" ", AntigravityScopes);

        string authorizeUrl =
            "https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            "&code_challenge_method=S256" +
            "&access_type=offline" +
            "&prompt=consent";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        Console.WriteLine("Native OAuth PoC");
        Console.WriteLine($"Callback: {redirectUri}");
        Console.WriteLine("Opening the Google consent page...");
        Console.WriteLine("The access/refresh tokens are never printed.");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizeUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            Console.WriteLine("Could not open a browser automatically. Open this URL manually:");
            Console.WriteLine(authorizeUrl);
        }

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3));
        }
        catch (TimeoutException)
        {
            Console.WriteLine("OAuth callback timed out.");
            Environment.ExitCode = 3;
            return;
        }

        string? returnedState = context.Request.QueryString["state"];
        string? code = context.Request.QueryString["code"];
        string? oauthError = context.Request.QueryString["error"];

        if (!string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            await WriteBrowserResponseAsync(context.Response, false, "State validation failed. You can close this tab.");
            Console.WriteLine("OAuth state validation failed.");
            Environment.ExitCode = 4;
            return;
        }

        if (!string.IsNullOrEmpty(oauthError) || string.IsNullOrEmpty(code))
        {
            await WriteBrowserResponseAsync(context.Response, false, "Authorization was not completed. You can close this tab.");
            Console.WriteLine($"OAuth authorization failed: {oauthError ?? "missing authorization code"}");
            Environment.ExitCode = 5;
            return;
        }

        await WriteBrowserResponseAsync(context.Response, true, "Authorization received. Return to QuotaTray.TestCli.");
        listener.Stop();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var initialTokens = await ExchangeAuthorizationCodeAsync(http, clientId, redirectUri, code, verifier);
        if (initialTokens == null)
        {
            Environment.ExitCode = 6;
            return;
        }

        Console.WriteLine($"Authorization-code exchange: OK (access token received, refresh token: {(string.IsNullOrEmpty(initialTokens.Value.RefreshToken) ? "NO" : "YES")})");

        string accessToken = initialTokens.Value.AccessToken;
        if (!string.IsNullOrEmpty(initialTokens.Value.RefreshToken))
        {
            var refreshedTokens = await RefreshAccessTokenAsync(http, clientId, initialTokens.Value.RefreshToken!);
            if (refreshedTokens == null)
            {
                Console.WriteLine("Standalone refresh test: FAILED");
                Environment.ExitCode = 7;
                return;
            }

            accessToken = refreshedTokens.Value.AccessToken;
            Console.WriteLine("Standalone refresh test: OK (client secret not used)");
        }
        else
        {
            Console.WriteLine("Standalone refresh test: SKIPPED because Google did not return a refresh token.");
        }

        var loadResult = await CallLoadCodeAssistAsync(http, accessToken);
        Console.WriteLine($"loadCodeAssist: HTTP {(int)loadResult.StatusCode} {loadResult.StatusCode}");

        if (loadResult.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(loadResult.ProjectId))
        {
            Console.WriteLine("Native OAuth reached Google, but Cloud Code Assist did not accept/resolve this session.");
            if (!string.IsNullOrWhiteSpace(loadResult.ErrorSnippet))
            {
                Console.WriteLine($"Response: {loadResult.ErrorSnippet}");
            }
            Environment.ExitCode = 8;
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
            Environment.ExitCode = 9;
            return;
        }

        Console.WriteLine();
        Console.WriteLine("POC SUCCESS: QuotaTray-native OAuth + PKCE can authenticate, refresh, and query Antigravity quota without running or inspecting agy.");
    }

    private static async Task<(string AccessToken, string? RefreshToken)?> ExchangeAuthorizationCodeAsync(
        HttpClient http,
        string clientId,
        string redirectUri,
        string code,
        string verifier)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri
            })
        };

        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Token exchange failed: HTTP {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine($"Response: {Truncate(body)}");
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var accessTokenProp))
        {
            Console.WriteLine("Token exchange response did not contain access_token.");
            return null;
        }

        string? accessToken = accessTokenProp.GetString();
        string? refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var refreshTokenProp)
            ? refreshTokenProp.GetString()
            : null;

        return string.IsNullOrEmpty(accessToken) ? null : (accessToken, refreshToken);
    }

    private static async Task<(string AccessToken, string? RefreshToken)?> RefreshAccessTokenAsync(
        HttpClient http,
        string clientId,
        string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken
            })
        };

        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Refresh failed: HTTP {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine($"Response: {Truncate(body)}");
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var accessTokenProp))
        {
            Console.WriteLine("Refresh response did not contain access_token.");
            return null;
        }

        string? accessToken = accessTokenProp.GetString();
        string? newRefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var refreshTokenProp)
            ? refreshTokenProp.GetString()
            : null;

        return string.IsNullOrEmpty(accessToken) ? null : (accessToken, newRefreshToken);
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

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, bool success, string message)
    {
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        string title = success ? "QuotaTray OAuth received" : "QuotaTray OAuth failed";
        string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head><body><h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Truncate(string value, int maxLength = 800)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }
}
