using System.Reflection;
using System.Text.Json;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.CodexVerifier;

internal static class Program
{
    private static readonly MethodInfo ParseWindowMethod =
        typeof(CodexQuotaProvider).GetMethod("ParseRateLimitWindow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseRateLimitWindow not found.");

    private static readonly MethodInfo ParseAdditionalMethod =
        typeof(CodexQuotaProvider).GetMethod("ParseAdditionalRateLimitGroups", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseAdditionalRateLimitGroups not found.");

    private static int _failures;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("QuotaTray Codex quota verifier");
        Console.WriteLine("Tests the actual CodexQuotaProvider parser with sanitized fixtures.\n");

        RunFixture("7d-only: no synthetic 5h", VerifyWeeklyOnly);
        RunFixture("5h + 7d standard windows", VerifyFiveHourAndWeekly);
        RunFixture("Spark additional quota group", VerifySparkGroup);
        RunFixture("Unused Spark bucket is Available", VerifyUnusedSparkAvailable);
        RunFixture("Unknown future additional quota", VerifyFutureAdditionalQuota);
        RunFixture("reset_at fallback", VerifyResetAtFallback);
        RunFixture("No additional limits on normal account", VerifyNoAdditionalLimits);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "FIXTURE RESULT: PASS"
            : $"FIXTURE RESULT: FAIL ({_failures} case(s))");

        if (args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            await RunLiveProbeAsync();
        }

        return _failures == 0 ? 0 : 1;
    }

    private static void RunFixture(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {ex.Message}");
        }
    }

    private static void VerifyWeeklyOnly()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": {
              "limit_window_seconds": 604800,
              "used_percent": 40,
              "reset_after_seconds": 172800
            }
          }
        }
        """;

        var windows = ParseBaseWindows(json);
        Expect(windows.Count == 1, $"expected 1 window, got {windows.Count}");
        Expect(windows[0].Name == "7d", $"expected 7d, got {windows[0].Name}");
        Expect(Math.Abs(windows[0].RemainingPercent - 60) < 0.001, "expected 60% remaining");
        Expect(!windows.Any(w => w.Name == "5h"), "synthetic 5h window was created");
    }

    private static void VerifyFiveHourAndWeekly()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": {
              "limit_window_seconds": 18000,
              "used_percent": 12,
              "reset_after_seconds": 7200
            },
            "secondary_window": {
              "limit_window_seconds": 604800,
              "used_percent": 35,
              "reset_after_seconds": 259200
            }
          }
        }
        """;

        var windows = ParseBaseWindows(json);
        Expect(windows.Count == 2, $"expected 2 windows, got {windows.Count}");
        Expect(windows.Any(w => w.Name == "5h" && Math.Abs(w.RemainingPercent - 88) < 0.001), "5h window mismatch");
        Expect(windows.Any(w => w.Name == "7d" && Math.Abs(w.RemainingPercent - 65) < 0.001), "7d window mismatch");
    }

    private static void VerifySparkGroup()
    {
        const string json = """
        {
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "metered_feature": "codex_bengalfox",
              "rate_limit": {
                "primary_window": {
                  "limit_window_seconds": 18000,
                  "used_percent": 20,
                  "reset_after_seconds": 3600
                },
                "secondary_window": {
                  "limit_window_seconds": 604800,
                  "used_percent": 45,
                  "reset_after_seconds": 172800
                }
              }
            }
          ]
        }
        """;

        var groups = ParseAdditionalGroups(json);
        Expect(groups.Count == 1, $"expected 1 group, got {groups.Count}");
        var spark = groups[0];
        Expect(spark.GroupId == "codex_bengalfox", $"unexpected group id: {spark.GroupId}");
        Expect(spark.GroupName == "GPT-5.3-Codex-Spark", $"unexpected group name: {spark.GroupName}");
        Expect(spark.Windows.Count == 2, $"expected 2 Spark windows, got {spark.Windows.Count}");
        Expect(spark.Windows.Any(w => w.Name == "5h" && Math.Abs(w.RemainingPercent - 80) < 0.001), "Spark 5h mismatch");
        Expect(spark.Windows.Any(w => w.Name == "7d" && Math.Abs(w.RemainingPercent - 55) < 0.001), "Spark 7d mismatch");
    }

    private static void VerifyUnusedSparkAvailable()
    {
        const string json = """
        {
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "metered_feature": "codex_bengalfox",
              "rate_limit": {
                "primary_window": {
                  "limit_window_seconds": 18000,
                  "used_percent": 0,
                  "reset_after_seconds": 18000
                }
              }
            }
          ]
        }
        """;

        var groups = ParseAdditionalGroups(json);
        var window = groups.Single().Windows.Single();
        Expect(Math.Abs(window.RemainingPercent - 100) < 0.001, "expected 100% remaining");
        Expect(window.ResetInSeconds == 0, $"expected reset 0, got {window.ResetInSeconds}");
        Expect(window.FormattedResetIn == "Available", $"expected Available, got {window.FormattedResetIn}");
    }

    private static void VerifyFutureAdditionalQuota()
    {
        const string json = """
        {
          "additional_rate_limits": [
            {
              "metered_feature": "codex_future_feature",
              "rate_limit": {
                "primary_window": {
                  "limit_window_seconds": 86400,
                  "used_percent": 5,
                  "reset_after_seconds": 40000
                }
              }
            }
          ]
        }
        """;

        var groups = ParseAdditionalGroups(json);
        Expect(groups.Count == 1, $"expected 1 group, got {groups.Count}");
        Expect(groups[0].GroupId == "codex_future_feature", $"unexpected group id: {groups[0].GroupId}");
        Expect(groups[0].GroupName == "codex_future_feature", $"unexpected group name: {groups[0].GroupName}");
        Expect(groups[0].Windows.Count == 1, "future group window missing");
    }

    private static void VerifyResetAtFallback()
    {
        long resetAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        string json = $$"""
        {
          "rate_limit": {
            "primary_window": {
              "limit_window_seconds": 18000,
              "used_percent": 10,
              "reset_at": {{resetAt}}
            }
          }
        }
        """;

        var windows = ParseBaseWindows(json);
        Expect(windows.Count == 1, "reset_at fixture did not produce a window");
        long remaining = windows[0].ResetInSeconds;
        Expect(remaining >= 3590 && remaining <= 3600, $"expected about 3600s, got {remaining}s");
    }

    private static void VerifyNoAdditionalLimits()
    {
        const string json = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "primary_window": {
              "limit_window_seconds": 604800,
              "used_percent": 10,
              "reset_after_seconds": 86400
            }
          }
        }
        """;

        var groups = ParseAdditionalGroups(json);
        Expect(groups.Count == 0, $"expected no additional groups, got {groups.Count}");
    }

    private static List<QuotaWindow> ParseBaseWindows(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("rate_limit", out JsonElement rateLimit))
        {
            return new List<QuotaWindow>();
        }

        var windows = new List<QuotaWindow>();
        InvokeParseWindow(rateLimit, "primary_window", windows, false);
        InvokeParseWindow(rateLimit, "secondary_window", windows, false);
        windows.Sort((a, b) => a.LimitWindowSeconds.CompareTo(b.LimitWindowSeconds));
        return windows;
    }

    private static List<QuotaGroup> ParseAdditionalGroups(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        object? result = ParseAdditionalMethod.Invoke(null, new object[] { doc.RootElement });
        return result as List<QuotaGroup>
               ?? throw new InvalidOperationException("ParseAdditionalRateLimitGroups returned an unexpected value.");
    }

    private static void InvokeParseWindow(JsonElement rateLimit, string propertyName, List<QuotaWindow> windows, bool markUnusedAsAvailable)
    {
        ParseWindowMethod.Invoke(null, new object[] { rateLimit, propertyName, windows, markUnusedAsAvailable });
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RunLiveProbeAsync()
    {
        Console.WriteLine("LIVE PROBE: current Codex login");
        Console.WriteLine("No token or account identifier is printed.");

        var provider = new CodexQuotaProvider();
        ProviderQuotaResult result = await provider.FetchQuotaAsync();
        if (!result.IsSuccess)
        {
            Console.WriteLine($"[FAIL] live query: {result.ErrorMessage ?? "unknown error"}");
            return;
        }

        Console.WriteLine($"[PASS] live query | plan={result.PlanType ?? "unknown"}");

        if (result.Windows.Count == 0)
        {
            Console.WriteLine("  shared windows: none returned");
        }
        else
        {
            Console.WriteLine("  shared windows:");
            foreach (var window in result.Windows)
            {
                Console.WriteLine($"    - {window.Name}: {window.RemainingPercent:F0}% left | {window.FormattedResetIn}");
            }
        }

        if (result.Groups.Count == 0)
        {
            Console.WriteLine("  additional quota groups: none");
        }
        else
        {
            Console.WriteLine("  quota groups:");
            foreach (var group in result.Groups)
            {
                Console.WriteLine($"    - {group.GroupName} ({group.GroupId})");
                foreach (var window in group.Windows)
                {
                    Console.WriteLine($"      {window.Name}: {window.RemainingPercent:F0}% left | {window.FormattedResetIn}");
                }
            }
        }
    }
}
