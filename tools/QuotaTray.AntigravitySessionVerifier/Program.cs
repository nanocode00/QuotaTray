using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuotaTray.Core.Providers;
using Snapshot = QuotaTray.Core.Providers.AntigravitySessionCoordinatorHandler.AntigravityCredentialSnapshot;

namespace QuotaTray.AntigravitySessionVerifier;

internal static class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        Console.WriteLine("QuotaTray Antigravity session coordinator verifier");
        Console.WriteLine("No real Google or Antigravity credentials are used by this test.\n");

        await RunAsync("External credential refresh wins over QuotaTray refresh", VerifyExternalCredentialWinsAsync);
        await RunAsync("QuotaTray refreshed token survives stale stored access token", VerifySelfRefreshCacheAsync);
        await RunAsync("Later external credential update replaces QuotaTray cache", VerifyLaterExternalUpdateAsync);
        await RunAsync("Explicit custom bearer token is never rewritten", VerifyCustomTokenUntouchedAsync);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "FIXTURE RESULT: PASS"
            : $"FIXTURE RESULT: FAIL ({_failures} case(s))");

        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {ex.Message}");
        }
    }

    private static async Task VerifyExternalCredentialWinsAsync()
    {
        Snapshot snapshot = new("stored-A", "refresh-R");
        var inner = new RecordingHandler
        {
            CloudResponder = token => token == "stored-A"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json(HttpStatusCode.OK, "{}"),
            RefreshResponder = _ => Json(HttpStatusCode.OK, "{\"access_token\":\"self-B\",\"expires_in\":3600}")
        };

        using var coordinator = new AntigravitySessionCoordinatorHandler(
            inner,
            () => snapshot,
            TimeSpan.Zero);
        using var client = new HttpClient(coordinator);

        using HttpResponseMessage first = await SendCloudAsync(client, "stored-A");
        Expect(first.StatusCode == HttpStatusCode.Unauthorized, "initial stored token should be rejected");

        // Simulate agy (Windows, WSL, or another local credential writer) updating the shared store
        // between the 401 and QuotaTray's attempted OAuth refresh.
        snapshot = new Snapshot("external-C", "refresh-R");

        using HttpResponseMessage refresh = await SendRefreshAsync(client, "refresh-R");
        string refreshBody = await refresh.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(refreshBody);
        string? access = document.RootElement.GetProperty("access_token").GetString();

        Expect(refresh.IsSuccessStatusCode, "synthetic external refresh response should succeed");
        Expect(access == "external-C", $"expected external-C, got {access}");
        Expect(inner.GoogleRefreshCount == 0, "Google OAuth refresh should not be called when external credential changed");
    }

    private static async Task VerifySelfRefreshCacheAsync()
    {
        Snapshot snapshot = new("stored-A", "refresh-R");
        var inner = new RecordingHandler
        {
            CloudResponder = token => token switch
            {
                "stored-A" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                "self-B" => Json(HttpStatusCode.OK, "{}"),
                _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            },
            RefreshResponder = refreshToken =>
            {
                Expect(refreshToken == "refresh-R", $"unexpected refresh token {refreshToken}");
                return Json(HttpStatusCode.OK, "{\"access_token\":\"self-B\",\"expires_in\":3600}");
            }
        };

        using var coordinator = new AntigravitySessionCoordinatorHandler(
            inner,
            () => snapshot,
            TimeSpan.Zero);
        using var client = new HttpClient(coordinator);

        using HttpResponseMessage first = await SendCloudAsync(client, "stored-A");
        Expect(first.StatusCode == HttpStatusCode.Unauthorized, "stored-A should trigger refresh path");

        using HttpResponseMessage refresh = await SendRefreshAsync(client, "refresh-R");
        Expect(refresh.IsSuccessStatusCode, "QuotaTray OAuth refresh should succeed");
        Expect(inner.GoogleRefreshCount == 1, $"expected one Google refresh, got {inner.GoogleRefreshCount}");

        // The provider will reload stored-A on its next polling cycle. The coordinator must
        // transparently keep using self-B rather than allowing another 401 + refresh loop.
        using HttpResponseMessage second = await SendCloudAsync(client, "stored-A");
        using HttpResponseMessage third = await SendCloudAsync(client, "stored-A");

        Expect(second.IsSuccessStatusCode && third.IsSuccessStatusCode, "cached self-B should keep quota calls healthy");
        Expect(inner.CloudBearerTokens.SequenceEqual(new[] { "stored-A", "self-B", "self-B" }),
            $"unexpected bearer sequence: {string.Join(", ", inner.CloudBearerTokens)}");
        Expect(inner.GoogleRefreshCount == 1, "stale stored token caused a repeated Google refresh");
    }

    private static async Task VerifyLaterExternalUpdateAsync()
    {
        Snapshot snapshot = new("stored-A", "refresh-R");
        var inner = new RecordingHandler
        {
            CloudResponder = token => token == "stored-A"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json(HttpStatusCode.OK, "{}"),
            RefreshResponder = _ => Json(HttpStatusCode.OK, "{\"access_token\":\"self-B\",\"expires_in\":3600}")
        };

        using var coordinator = new AntigravitySessionCoordinatorHandler(inner, () => snapshot, TimeSpan.Zero);
        using var client = new HttpClient(coordinator);

        using (await SendCloudAsync(client, "stored-A")) { }
        using (await SendRefreshAsync(client, "refresh-R")) { }
        using (await SendCloudAsync(client, "stored-A")) { }

        snapshot = new Snapshot("external-C", "refresh-R");
        using HttpResponseMessage external = await SendCloudAsync(client, "external-C");

        Expect(external.IsSuccessStatusCode, "external-C should be accepted");
        Expect(inner.CloudBearerTokens.Last() == "external-C",
            $"external credential should replace self cache, got {inner.CloudBearerTokens.LastOrDefault()}");
    }

    private static async Task VerifyCustomTokenUntouchedAsync()
    {
        Snapshot snapshot = new("stored-A", "refresh-R");
        var inner = new RecordingHandler
        {
            CloudResponder = _ => Json(HttpStatusCode.OK, "{}")
        };

        using var coordinator = new AntigravitySessionCoordinatorHandler(inner, () => snapshot, TimeSpan.Zero);
        using var client = new HttpClient(coordinator);

        using HttpResponseMessage response = await SendCloudAsync(client, "custom-X");

        Expect(response.IsSuccessStatusCode, "custom token request should succeed");
        Expect(inner.CloudBearerTokens.Single() == "custom-X",
            $"custom bearer was rewritten to {inner.CloudBearerTokens.SingleOrDefault()}");
    }

    private static async Task<HttpResponseMessage> SendCloudAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRefreshAsync(HttpClient client, string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = "fixture-client",
                ["client_secret"] = "fixture-secret",
                ["refresh_token"] = refreshToken
            })
        };
        return await client.SendAsync(request);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<string?, HttpResponseMessage>? CloudResponder { get; init; }
        public Func<string?, HttpResponseMessage>? RefreshResponder { get; init; }
        public List<string?> CloudBearerTokens { get; } = new();
        public List<string?> RefreshTokens { get; } = new();
        public int GoogleRefreshCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("missing request URI");

            if (uri.Host.Equals("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                GoogleRefreshCount++;
                string? refreshToken = await ReadFormValueAsync(request.Content, "refresh_token", cancellationToken);
                RefreshTokens.Add(refreshToken);
                return RefreshResponder?.Invoke(refreshToken)
                       ?? new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (uri.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                string? token = request.Headers.Authorization?.Parameter;
                CloudBearerTokens.Add(token);
                return CloudResponder?.Invoke(token) ?? Json(HttpStatusCode.OK, "{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static async Task<string?> ReadFormValueAsync(
            HttpContent? content,
            string key,
            CancellationToken cancellationToken)
        {
            if (content == null)
            {
                return null;
            }

            string body = await content.ReadAsStringAsync(cancellationToken);
            foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                string rawKey = separator >= 0 ? pair[..separator] : pair;
                string rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
                string decodedKey = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                if (decodedKey == key)
                {
                    return Uri.UnescapeDataString(rawValue.Replace('+', ' '));
                }
            }
            return null;
        }
    }
}
