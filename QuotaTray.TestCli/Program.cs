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

        if (args.Length > 0 && args[0].Equals("copilot-schema", StringComparison.OrdinalIgnoreCase))
        {
            await RunCopilotSchemaProbeAsync();
            return;
        }

        var provider = new AntigravityQuotaProvider();
        ProviderQuotaResult res = await provider.FetchQuotaAsync();
        PrintResult(res);
    }

    private static string? LoadCopilotTokenForDiagnostic()
    {
        MethodInfo? loadCredentials = typeof(CopilotQuotaProvider).GetMethod(
            "LoadCredentials",
            BindingFlags.Static | BindingFlags.NonPublic);
        return loadCredentials?.Invoke(null, null) as string;
    }

    private static async Task RunCopilotSchemaProbeAsync()
    {
        Console.WriteLine("GitHub Copilot sanitized response-shape probe");
        Console.WriteLine("Field names, JSON types, selected quota numbers, and model IDs may be printed.");
        Console.WriteLine("Tokens and arbitrary string payloads are never printed.");
        Console.WriteLine();

        string? token = LoadCopilotTokenForDiagnostic();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("SCHEMA PROBE FAILED: QuotaTray did not discover a GitHub token.");
            Environment.ExitCode = 10;
            return;
        }

        var provider = new CopilotQuotaProvider();
        ProviderQuotaResult providerResult = await provider.FetchQuotaAsync();
        if (!providerResult.IsSuccess || string.IsNullOrWhiteSpace(providerResult.AccountEmail))
        {
            Console.WriteLine("SCHEMA PROBE FAILED: provider could not resolve the authenticated GitHub login.");
            PrintResult(providerResult);
            Environment.ExitCode = 11;
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        await ProbeJsonEndpointAsync(
            httpClient,
            "copilot_internal/user",
            "https://api.github.com/copilot_internal/user",
            token,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.0");
            },
            printSelectedValues: PrintCopilotInternalSelectedValues);

        await ProbeJsonEndpointAsync(
            httpClient,
            "api.githubcopilot.com/models",
            "https://api.githubcopilot.com/models",
            token,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.0");
                request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.24.1");
            },
            printSelectedValues: PrintModelCatalogSummary);

        string login = providerResult.AccountEmail;
        await ProbeJsonEndpointAsync(
            httpClient,
            "AI credit usage",
            $"https://api.github.com/users/{Uri.EscapeDataString(login)}/settings/billing/ai_credit/usage",
            token,
            request => request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10"),
            printSelectedValues: PrintAiCreditSummary);

        Console.WriteLine();
        Console.WriteLine("SCHEMA PROBE COMPLETE");
    }

    private static async Task ProbeJsonEndpointAsync(
        HttpClient httpClient,
        string label,
        string url,
        string token,
        Action<HttpRequestMessage>? configure,
        Action<JsonElement>? printSelectedValues)
    {
        Console.WriteLine($"=== {label} ===");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "QuotaTray-TestCli/1.0");
        configure?.Invoke(request);

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
            Console.WriteLine();
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            Console.WriteLine("Shape:");
            PrintJsonShape(doc.RootElement, "  ", 0, 4);
            printSelectedValues?.Invoke(doc.RootElement);
        }
        catch (JsonException)
        {
            Console.WriteLine("Body: non-JSON success response (content suppressed)");
        }

        Console.WriteLine();
    }

    private static void PrintJsonShape(JsonElement element, string indent, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            Console.WriteLine($"{indent}<max depth>");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    Console.WriteLine($"{indent}{prop.Name}: {DescribeJsonKind(prop.Value)}");
                    if ((prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array) && depth < maxDepth)
                    {
                        PrintJsonShape(prop.Value, indent + "  ", depth + 1, maxDepth);
                    }
                }
                break;

            case JsonValueKind.Array:
                int count = element.GetArrayLength();
                Console.WriteLine($"{indent}count={count}");
                if (count > 0 && depth < maxDepth)
                {
                    Console.WriteLine($"{indent}first-item:");
                    PrintJsonShape(element[0], indent + "  ", depth + 1, maxDepth);
                }
                break;
        }
    }

    private static string DescribeJsonKind(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => $"array[{value.GetArrayLength()}]",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => value.ValueKind.ToString()
        };
    }

    private static void PrintCopilotInternalSelectedValues(JsonElement root)
    {
        Console.WriteLine("Selected safe values:");
        PrintStringIfPresent(root, "copilot_plan");
        PrintStringIfPresent(root, "access_type_sku");
        PrintStringIfPresent(root, "quota_reset_date_utc");

        if (!root.TryGetProperty("quota_snapshots", out JsonElement snapshots) || snapshots.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty snapshot in snapshots.EnumerateObject())
        {
            if (snapshot.Value.ValueKind != JsonValueKind.Object) continue;
            Console.WriteLine($"  quota_snapshots.{snapshot.Name}:");
            PrintNumberIfPresent(snapshot.Value, "percent_remaining", "    ");
            PrintNumberIfPresent(snapshot.Value, "remaining", "    ");
            PrintNumberIfPresent(snapshot.Value, "entitlement", "    ");
        }
    }

    private static void PrintModelCatalogSummary(JsonElement root)
    {
        JsonElement models = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            models = data;
        }

        if (models.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("Model summary: no top-level model array detected.");
            return;
        }

        Console.WriteLine($"Model summary: {models.GetArrayLength()} entries");
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object) continue;
            string? id = GetString(model, "id") ?? GetString(model, "name");
            if (!string.IsNullOrWhiteSpace(id)) Console.WriteLine($"  - {id}");
        }
    }

    private static void PrintAiCreditSummary(JsonElement root)
    {
        if (root.TryGetProperty("timePeriod", out JsonElement period) && period.ValueKind == JsonValueKind.Object)
        {
            string year = period.TryGetProperty("year", out JsonElement y) ? y.ToString() : "?";
            string month = period.TryGetProperty("month", out JsonElement m) ? m.ToString() : "?";
            Console.WriteLine($"AI credit period: {year}-{month}");
        }

        if (root.TryGetProperty("usageItems", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"AI credit usageItems: {items.GetArrayLength()}");
        }
    }

    private static void PrintStringIfPresent(JsonElement obj, string name, string indent = "  ")
    {
        if (obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine($"{indent}{name}: {value.GetString()}");
        }
    }

    private static void PrintNumberIfPresent(JsonElement obj, string name, string indent = "  ")
    {
        if (obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
        {
            Console.WriteLine($"{indent}{name}: {value}");
        }
    }

    private static string? GetString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
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

        string? token = LoadCopilotTokenForDiagnostic();
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
                Console.WriteLine("UsageItems present; raw items suppressed. Use copilot-schema to inspect their field shape safely.");
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
