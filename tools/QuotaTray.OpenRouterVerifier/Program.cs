using System.Net;
using System.Net.Http;
using System.Text;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.OpenRouterVerifier;

internal static class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        Console.WriteLine("QuotaTray OpenRouter provider verifier");
        Console.WriteLine("Exercises the real OpenRouterQuotaProvider with sanitized HTTP fixtures.\n");

        await RunAsync("PAYG account with credits", VerifyPaygWithCreditsAsync);
        await RunAsync("Free tier without credits access", VerifyFreeTierWithoutCreditsAsync);
        await RunAsync("Key spending limit is a real quota", VerifyKeyLimitAsync);
        await RunAsync("Credits parse failure is optional", VerifyMalformedCreditsAsync);
        await RunAsync("Unauthorized /key expires auth", VerifyUnauthorizedKeyAsync);
        await RunAsync("Uses /api/v1/key, not legacy /auth/key", VerifyCurrentKeyEndpointAsync);

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

    private static async Task VerifyPaygWithCreditsAsync()
    {
        var handler = new FixtureHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/key" => Json(HttpStatusCode.OK, """
            {
              "data": {
                "is_free_tier": false,
                "limit": null,
                "limit_remaining": null,
                "limit_reset": null,
                "usage": 0.00240817,
                "usage_daily": 0,
                "usage_weekly": 0,
                "usage_monthly": 0.00240817,
                "expires_at": null
              }
            }
            """),
            "/api/v1/credits" => Json(HttpStatusCode.OK, """
            {
              "data": {
                "total_credits": 10,
                "total_usage": 0.00240817
              }
            }
            """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await FetchAsync(handler);

        Expect(result.IsSuccess, "provider should succeed");
        Expect(result.PlanType == "PAYG", $"expected PAYG, got {result.PlanType}");
        Expect(result.BalanceAmount.HasValue && Math.Abs(result.BalanceAmount.Value - 9.99759183) < 0.000001,
            $"unexpected balance {result.BalanceAmount}");
        Expect(result.BalanceFormatted == "$10.00", $"unexpected formatted balance {result.BalanceFormatted}");
        Expect(result.DetailsSubtitle == "Free 1k/day · 20 RPM",
            $"entitlement should occupy its own subtitle line: {result.DetailsSubtitle}");
        Expect(result.Windows.Count == 1, $"expected only credits window, got {result.Windows.Count}");
        Expect(result.Windows[0].Name.Contains("$9.9976", StringComparison.Ordinal),
            $"remaining credits should retain precision: {result.Windows[0].Name}");
        Expect(result.Windows[0].FormattedResetIn.Contains("Month $0.0024 used", StringComparison.Ordinal),
            $"monthly usage should be separated onto the credits row: {result.Windows[0].FormattedResetIn}");
        Expect(result.Windows.All(w => !w.Name.Contains("Free models", StringComparison.OrdinalIgnoreCase)),
            "synthetic free-model progress bar was created");
    }

    private static async Task VerifyFreeTierWithoutCreditsAsync()
    {
        var handler = new FixtureHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/key" => Json(HttpStatusCode.OK, """
            {
              "data": {
                "is_free_tier": true,
                "usage": 0.25,
                "usage_daily": 0.05,
                "usage_weekly": 0.10,
                "usage_monthly": 0.25
              }
            }
            """),
            "/api/v1/credits" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await FetchAsync(handler);

        Expect(result.IsSuccess, "credits failure must not fail a valid /key response");
        Expect(result.PlanType == "Free", $"expected Free, got {result.PlanType}");
        Expect(result.IsBalanceProvider, "OpenRouter should retain balance-style card rendering");
        Expect(result.BalanceFormatted == "Active", $"expected Active fallback, got {result.BalanceFormatted}");
        Expect(result.DetailsSubtitle == "Free 50/day · 20 RPM",
            $"free-tier entitlement should occupy its own subtitle line: {result.DetailsSubtitle}");
        Expect(result.Windows.Count == 0, "free-model policy must not become a quota window");
    }

    private static async Task VerifyKeyLimitAsync()
    {
        var handler = new FixtureHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/key" => Json(HttpStatusCode.OK, """
            {
              "data": {
                "is_free_tier": false,
                "limit": 100,
                "limit_remaining": 75,
                "limit_reset": "monthly",
                "usage": 25,
                "usage_monthly": 25
              }
            }
            """),
            "/api/v1/credits" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await FetchAsync(handler);

        Expect(result.IsSuccess, "provider should succeed");
        Expect(result.Windows.Count == 1, $"expected one key-limit window, got {result.Windows.Count}");
        QuotaWindow window = result.Windows.Single();
        Expect(window.Name.StartsWith("Key limit", StringComparison.Ordinal), $"unexpected window {window.Name}");
        Expect(Math.Abs(window.RemainingPercent - 75) < 0.001, $"expected 75%, got {window.RemainingPercent}");
        Expect(window.FormattedResetIn.StartsWith("Reset in ", StringComparison.Ordinal),
            $"monthly reset was not converted to countdown: {window.FormattedResetIn}");
        Expect(result.Windows.All(w => !w.Name.Contains("Free models", StringComparison.OrdinalIgnoreCase)),
            "synthetic free-model window was created");
    }

    private static async Task VerifyMalformedCreditsAsync()
    {
        var handler = new FixtureHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/key" => Json(HttpStatusCode.OK, """
            {
              "data": {
                "is_free_tier": false,
                "usage": 3.5,
                "usage_monthly": 3.5
              }
            }
            """),
            "/api/v1/credits" => Json(HttpStatusCode.OK, "{\"data\":{}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await FetchAsync(handler);

        Expect(result.IsSuccess, "malformed optional credits data must not fail provider");
        Expect(result.BalanceFormatted == "Active", $"expected Active fallback, got {result.BalanceFormatted}");
        Expect(result.Windows.Count == 0, "malformed credits should not create a fake credits window");
    }

    private static async Task VerifyUnauthorizedKeyAsync()
    {
        var handler = new FixtureHandler(request => request.RequestUri?.AbsolutePath == "/api/v1/key"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK));

        var result = await FetchAsync(handler);

        Expect(!result.IsSuccess, "unauthorized /key should fail provider");
        Expect(result.IsAuthMissing, "unauthorized /key should require auth");
        Expect(result.AuthStatus == ProviderAuthStatus.Expired, $"expected Expired, got {result.AuthStatus}");
        Expect(handler.RequestedPaths.SequenceEqual(new[] { "/api/v1/key" }),
            "provider should stop before calling /credits when /key is unauthorized");
    }

    private static async Task VerifyCurrentKeyEndpointAsync()
    {
        var handler = new FixtureHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/key" => Json(HttpStatusCode.OK, "{\"data\":{\"is_free_tier\":false,\"usage\":0}}"),
            "/api/v1/credits" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "/api/v1/auth/key" => throw new InvalidOperationException("legacy /auth/key endpoint was called"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await FetchAsync(handler);

        Expect(result.IsSuccess, "provider should succeed with current key endpoint");
        Expect(handler.RequestedPaths.Contains("/api/v1/key"), "current /api/v1/key was not requested");
        Expect(!handler.RequestedPaths.Contains("/api/v1/auth/key"), "legacy /auth/key was requested");
    }

    private static async Task<ProviderQuotaResult> FetchAsync(FixtureHandler handler)
    {
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var provider = new OpenRouterQuotaProvider(httpClient);
        provider.SetCustomApiKey("sk-or-v1-sanitized-fixture-key");
        return await provider.FetchQuotaAsync();
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<string> RequestedPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(_respond(request));
        }
    }
}
