using System.Net;
using System.Text;
using System.Text.Json;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.ProviderAccuracyVerifier;

internal static class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        Console.WriteLine("QuotaTray provider accuracy verifier");
        Console.WriteLine("No real provider credentials are used by this test.\n");

        await RunAsync("Antigravity synthetic 100% fallback is rejected", VerifyAntigravityFallbackRejectedAsync);
        await RunAsync("Antigravity hard-coded model roster is removed", VerifyAntigravityModelsRemovedAsync);
        await RunAsync("Antigravity payload keeps only verified buckets", VerifyAntigravityPayloadSanitizerAsync);
        await RunAsync("Copilot incomplete finite quota is removed", VerifyCopilotPayloadSanitizerAsync);
        await RunAsync("Codex empty quota response is not reported as 100%", VerifyCodexEmptyRejectedAsync);
        await RunAsync("OpenRouter free plan keeps confirmed 50/day cap", VerifyOpenRouterFreeCapAsync);
        await RunAsync("OpenRouter unconfirmed PAYG cap is hidden", VerifyOpenRouterUnknownPaidCapAsync);
        await RunAsync("OpenRouter $10+ credits confirms 1,000/day cap", VerifyOpenRouterConfirmedPaidCapAsync);

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

    private static async Task VerifyAntigravityFallbackRejectedAsync()
    {
        ProviderQuotaResult fixture = Success("antigravity");
        fixture.Groups = new List<QuotaGroup>
        {
            FallbackGroup("gemini"),
            FallbackGroup("claude_gpt")
        };
        fixture.Windows = fixture.Groups.SelectMany(group => group.Windows).ToList();

        ProviderQuotaResult result = await Guard("antigravity", fixture).FetchQuotaAsync();
        Expect(!result.IsSuccess, "synthetic fallback should fail closed");
        Expect(result.Windows.Count == 0 && result.Groups.Count == 0, "synthetic values were not cleared");
    }

    private static async Task VerifyAntigravityModelsRemovedAsync()
    {
        ProviderQuotaResult fixture = Success("antigravity");
        fixture.Groups = new List<QuotaGroup>
        {
            new()
            {
                GroupId = "gemini",
                GroupName = "Gemini Models",
                ModelsList = new List<string> { "hard-coded-model" },
                Windows = new List<QuotaWindow>
                {
                    new()
                    {
                        Name = "5h",
                        LimitWindowSeconds = 18000,
                        RemainingPercent = 63,
                        UsedPercent = 37,
                        ResetInSeconds = 1200,
                        FormattedResetIn = "20m"
                    }
                }
            }
        };
        fixture.Windows = fixture.Groups.SelectMany(group => group.Windows).ToList();

        ProviderQuotaResult result = await Guard("antigravity", fixture).FetchQuotaAsync();
        Expect(result.IsSuccess, "real quota should stay healthy");
        Expect(result.Groups.Single().ModelsList.Count == 0, "hard-coded models should not be exposed");
    }

    private static async Task VerifyAntigravityPayloadSanitizerAsync()
    {
        string reset = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
        string json = $$"""
        {
          "groups": [
            {"displayName":"Gemini","buckets":[{"window":"weekly","bucketId":"gemini-weekly","remainingFraction":0.5,"resetTime":"{{reset}}"}]},
            {"displayName":"Other Future Pool","buckets":[{"window":"weekly","bucketId":"other-weekly","remainingFraction":0.7,"resetTime":"{{reset}}"}]},
            {"displayName":"Claude","buckets":[{"window":"five_hour","bucketId":"claude-5h","resetTime":"{{reset}}"}]},
            {"displayName":"GPT","buckets":[{"window":"five_hour","bucketId":"gpt-5h","remainingFraction":0.8,"resetTime":"{{reset}}"}]}
          ]
        }
        """;

        var inner = new StaticJsonHandler(json);
        using var client = new HttpClient(new AntigravityAccuracyHandler(inner));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary");
        request.Headers.TryAddWithoutValidation("User-Agent", "antigravity/1.11.5 windows/amd64");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement groups = doc.RootElement.GetProperty("groups");

        Expect(groups.GetArrayLength() == 2, $"expected 2 verified groups, got {groups.GetArrayLength()}");
        Expect(inner.LastUserAgent.StartsWith("antigravity/", StringComparison.Ordinal), "runtime Antigravity user-agent missing");
        Expect(!inner.LastUserAgent.Contains("1.11.5", StringComparison.Ordinal), "hard-coded agy version was preserved");
        Expect(!inner.LastUserAgent.Contains("windows/amd64", StringComparison.Ordinal) || OperatingSystem.IsWindows(),
            "Windows-only runtime header leaked on a non-Windows runtime");
    }

    private static async Task VerifyCopilotPayloadSanitizerAsync()
    {
        const string json = """
        {
          "quota_snapshots": {
            "completions": {"has_quota":true,"entitlement":2000},
            "chat": {"has_quota":true,"entitlement":200,"remaining":150},
            "premium_interactions": {"unlimited":true}
          }
        }
        """;

        using var client = new HttpClient(new CopilotAccuracyHandler(new StaticJsonHandler(json)));
        using HttpResponseMessage response = await client.GetAsync("https://api.github.com/copilot_internal/user");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement snapshots = doc.RootElement.GetProperty("quota_snapshots");

        Expect(!snapshots.TryGetProperty("completions", out _), "incomplete completions quota should be removed");
        Expect(snapshots.TryGetProperty("chat", out _), "complete chat quota should remain");
        Expect(snapshots.TryGetProperty("premium_interactions", out _), "explicit unlimited quota should remain");
    }

    private static async Task VerifyCodexEmptyRejectedAsync()
    {
        ProviderQuotaResult fixture = Success("codex");
        fixture.PrimaryRemainingPercent = 100;
        fixture.ResetText = "Available";

        ProviderQuotaResult result = await Guard("codex", fixture).FetchQuotaAsync();
        Expect(!result.IsSuccess, "empty Codex result should fail closed");
        Expect(result.ResetText == "Unavailable", "empty Codex result still looks available");
    }

    private static async Task VerifyOpenRouterFreeCapAsync()
    {
        ProviderQuotaResult fixture = OpenRouterResult("Free", null, 12, 50);
        ProviderQuotaResult result = await Guard("openrouter", fixture).FetchQuotaAsync();
        QuotaWindow free = result.Windows.Single(window => window.Name.StartsWith("Free models ("));
        Expect(free.Name.Contains("12 / 50 today", StringComparison.Ordinal), $"unexpected free cap: {free.Name}");
    }

    private static async Task VerifyOpenRouterUnknownPaidCapAsync()
    {
        ProviderQuotaResult fixture = OpenRouterResult("PAYG", null, 20, 1000);
        ProviderQuotaResult result = await Guard("openrouter", fixture).FetchQuotaAsync();
        Expect(result.Windows.All(window => !window.Name.StartsWith("Free models (", StringComparison.OrdinalIgnoreCase)),
            "unconfirmed 1,000/day progress bar should be hidden");
        Expect(result.DetailsSubtitle.Contains("cap unknown", StringComparison.OrdinalIgnoreCase),
            "unknown cap should be stated explicitly");
    }

    private static async Task VerifyOpenRouterConfirmedPaidCapAsync()
    {
        ProviderQuotaResult fixture = OpenRouterResult("PAYG", 10.0, 20, 1000);
        ProviderQuotaResult result = await Guard("openrouter", fixture).FetchQuotaAsync();
        QuotaWindow free = result.Windows.Single(window => window.Name.StartsWith("Free models ("));
        Expect(free.Name.Contains("20 / 1,000 today", StringComparison.Ordinal), $"unexpected paid cap: {free.Name}");
        Expect(Math.Abs(free.RemainingPercent - 98.0) < 0.001, $"expected 98%, got {free.RemainingPercent}");
    }

    private static QuotaAccuracyGuardProvider Guard(string key, ProviderQuotaResult result)
        => new(new FixtureProvider(key, result));

    private static ProviderQuotaResult OpenRouterResult(string plan, double? totalCredits, long used, int providerCap)
    {
        ProviderQuotaResult result = Success("openrouter");
        result.PlanType = plan;
        result.IsBalanceProvider = true;
        result.DetailsSubtitle = providerCap == 1000 ? "Free 1k/day · 20 RPM" : "Free 50/day · 20 RPM";
        if (totalCredits.HasValue)
        {
            result.Windows.Add(new QuotaWindow
            {
                Name = $"Credits ($9.00 / ${totalCredits.Value:F2})",
                RemainingPercent = 90,
                UsedPercent = 10,
                FormattedResetIn = "Month $1.00 used"
            });
        }
        result.Windows.Add(new QuotaWindow
        {
            Name = $"Free models ({used:N0} / {providerCap:N0} today)",
            RemainingPercent = Math.Clamp((providerCap - used) / (double)providerCap * 100.0, 0, 100),
            UsedPercent = Math.Clamp(used / (double)providerCap * 100.0, 0, 100),
            FormattedResetIn = "20 RPM"
        });
        return result;
    }

    private static ProviderQuotaResult Success(string key) => new()
    {
        ProviderKey = key,
        ProviderTitle = key,
        IconLetter = key[..1].ToUpperInvariant(),
        IconColorHex = "#FFFFFF",
        IconBgColorHex = "#000000",
        IsSuccess = true,
        AuthStatus = ProviderAuthStatus.Connected
    };

    private static QuotaGroup FallbackGroup(string id) => new()
    {
        GroupId = id,
        GroupName = id,
        ModelsList = new List<string> { "invented-model" },
        Windows = new List<QuotaWindow>
        {
            new() { Name = "5h", RemainingPercent = 100, FormattedResetIn = "Available" },
            new() { Name = "Weekly", RemainingPercent = 100, FormattedResetIn = "Available" }
        }
    };

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureProvider : IQuotaProvider
    {
        private readonly ProviderQuotaResult _result;
        public FixtureProvider(string key, ProviderQuotaResult result) { ProviderKey = key; _result = result; }
        public string ProviderKey { get; }
        public string ProviderTitle => ProviderKey;
        public string IconLetter => "F";
        public string IconColorHex => "#FFFFFF";
        public string IconBgColorHex => "#000000";
        public string AuthMethod => "fixture";
        public string? CliLoginCommand => null;
        public bool RequiresApiKey => false;
        public void SetCustomApiKey(string? apiKey) { }
        public Task<ProviderQuotaResult> FetchQuotaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;
        public string LastUserAgent { get; private set; } = string.Empty;
        public StaticJsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUserAgent = string.Join(" ", request.Headers.UserAgent.Select(value => value.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
