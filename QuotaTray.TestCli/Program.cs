using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.TestCli;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("refresh-test", StringComparison.OrdinalIgnoreCase))
        {
            await RunForcedRefreshProbeAsync();
            return;
        }

        if (args.Length > 0 && args[0].Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("GitHub Copilot provider probe");
            Console.WriteLine("This uses QuotaTray's normal credential discovery and provider path. Tokens are never printed.");
            Console.WriteLine();

            var copilotProvider = new CopilotQuotaProvider();
            ProviderQuotaResult copilotResult = await copilotProvider.FetchQuotaAsync();
            PrintResult(copilotResult);
            return;
        }

        if (args.Length > 0 && args[0].Equals("copilot-ai-credit", StringComparison.OrdinalIgnoreCase))
        {
            await RunCopilotAiCreditProbeAsync();
            return;
        }

        var provider = new AntigravityQuotaProvider();
        ProviderQuotaResult res = await provider.FetchQuotaAsync();
        PrintResult(res);
    }

    private static async Task RunCopilotAiCreditProbeAsync()
    {
        Console.WriteLine("GitHub Copilot AI credit API probe");
        Console.WriteLine("This uses QuotaTray's own GitHub credential discovery. The token is never printed.");
        Console.WriteLine();

        var provider = new CopilotQuotaProvider();
        ProviderQuotaResult providerResult = await provider.FetchQuotaAsync();
        if (!providerResult.IsSuccess || string.IsNullOrWhiteSpace(providerResult.AccountEmail))
        {
            Console.WriteLine("AI CREDIT PROBE FAILED: QuotaTray could not resolve an authenticated GitHub login first.");
            PrintResult(providerResult);
            Environment.ExitCode = 6;
            return;
        }

        MethodInfo? loadCredentials = typeof(CopilotQuotaProvider).GetMethod(
            "LoadCredentials",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (loadCredentials == null)
        {
            Console.WriteLine("AI CREDIT PROBE FAILED: provider credential loader changed; update the diagnostic probe.");
            Environment.ExitCode = 7;
            return;
        }

        string? token = loadCredentials.Invoke(null, null) as string;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("AI CREDIT PROBE FAILED: QuotaTray did not discover a GitHub token.");
            Environment.ExitCode = 8;
            return;
        }

        string login = providerResult.AccountEmail;
        string url = $"https://api.github.com/users/{Uri.EscapeDataString(login)}/settings/billing/ai_credit/usage";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.TryAddWithoutValidation("User-Agent", "QuotaTray-TestCli/1.0");

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"HTTP: {(int)response.StatusCode} {response.StatusCode}");

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using JsonDocument errorDoc = JsonDocument.Parse(body);
                if (errorDoc.RootElement.TryGetProperty("message", out JsonElement message))
                {
                    Console.WriteLine($"Message: {message.GetString()}");
                }
            }
            catch
            {
                Console.WriteLine("Message: non-JSON error response");
            }

            Console.WriteLine("AI CREDIT PROBE FAILED: QuotaTray's discovered token could not query the AI credit endpoint.");
            Environment.ExitCode = 9;
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("timePeriod", out JsonElement period))
        {
            string? year = period.TryGetProperty("year", out JsonElement y) ? y.ToString() : null;
            string? month = period.TryGetProperty("month", out JsonElement m) ? m.ToString() : null;
            Console.WriteLine($"TimePeriod: {year}-{month}");
        }

        if (root.TryGetProperty("user", out JsonElement user))
        {
            Console.WriteLine($"User: {user.GetString()}");
        }

        if (root.TryGetProperty("usageItems", out JsonElement usageItems) && usageItems.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"UsageItems Count: {usageItems.GetArrayLength()}");
            if (usageItems.GetArrayLength() > 0)
            {
                Console.WriteLine("UsageItems:");
                foreach (JsonElement item in usageItems.EnumerateArray())
                {
                    Console.WriteLine($" - {item.GetRawText()}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("AI CREDIT PROBE SUCCESS: QuotaTray's own discovered GitHub token can query the AI credit endpoint.");
    }

    private static async Task RunForcedRefreshProbeAsync()
    {
        Console.WriteLine("Antigravity forced refresh probe");
        Console.WriteLine("This bypasses the currently-valid access token and tests QuotaTray's direct refresh path.");
        Console.WriteLine("agy does not need to be running. Tokens and OAuth secrets are never printed.");
        Console.WriteLine();

        var provider = new AntigravityQuotaProvider();
        Type providerType = typeof(AntigravityQuotaProvider);

        MethodInfo? loadCredentials = providerType.GetMethod(
            "LoadStoredCredentials",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? refreshToken = providerType.GetMethod(
            "TryRefreshTokenAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? lastRefreshError = providerType.GetField(
            "_lastRefreshError",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (loadCredentials == null || refreshToken == null || lastRefreshError == null)
        {
            Console.WriteLine("REFRESH PROBE FAILED: provider internals changed; update the diagnostic probe.");
            Environment.ExitCode = 2;
            return;
        }

        loadCredentials.Invoke(provider, null);

        object? invocationResult = refreshToken.Invoke(provider, new object[] { CancellationToken.None });
        if (invocationResult is not Task<bool> refreshTask)
        {
            Console.WriteLine("REFRESH PROBE FAILED: unexpected refresh method result.");
            Environment.ExitCode = 3;
            return;
        }

        bool refreshed = await refreshTask;
        if (!refreshed)
        {
            string? error = lastRefreshError.GetValue(provider) as string;
            Console.WriteLine($"REFRESH PROBE FAILED: {error ?? "unknown refresh error"}");
            Environment.ExitCode = 4;
            return;
        }

        Console.WriteLine("Direct OAuth refresh: OK");
        Console.WriteLine("Now querying quota with the refreshed in-memory access token...");

        ProviderQuotaResult result = await provider.FetchQuotaAsync();
        PrintResult(result);

        if (result.IsSuccess)
        {
            Console.WriteLine();
            Console.WriteLine("REFRESH PROBE SUCCESS: QuotaTray refreshed and queried Antigravity without an agy process.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("REFRESH PROBE PARTIAL: refresh succeeded, but the quota query failed.");
            Environment.ExitCode = 5;
        }
    }

    private static void PrintResult(ProviderQuotaResult res)
    {
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
}
