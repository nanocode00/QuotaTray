#if DEBUG
using System.Reflection;
using System.Text.Json;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.Desktop.Diagnostics;

internal sealed class CodexSparkMockQuotaProvider : IQuotaProvider
{
    private static readonly MethodInfo ParseWindowMethod =
        typeof(CodexQuotaProvider).GetMethod("ParseRateLimitWindow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseRateLimitWindow not found.");

    private static readonly MethodInfo ParseAdditionalMethod =
        typeof(CodexQuotaProvider).GetMethod("ParseAdditionalRateLimitGroups", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseAdditionalRateLimitGroups not found.");

    public string ProviderKey => "codex";
    public string ProviderTitle => "Codex";
    public string IconLetter => "C";
    public string IconColorHex => "#10A37F";
    public string IconBgColorHex => "#0F372E";
    public string AuthMethod => "Mock (Spark UI verification)";
    public string? CliLoginCommand => null;
    public bool RequiresApiKey => false;

    public void SetCustomApiKey(string? apiKey)
    {
    }

    public Task<ProviderQuotaResult> FetchQuotaAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string json = """
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": {
              "limit_window_seconds": 604800,
              "used_percent": 42,
              "reset_after_seconds": 259200
            }
          },
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

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var sharedWindows = new List<QuotaWindow>();
        JsonElement rateLimit = root.GetProperty("rate_limit");
        ParseWindow(rateLimit, "primary_window", sharedWindows, false);
        ParseWindow(rateLimit, "secondary_window", sharedWindows, false);
        sharedWindows.Sort((a, b) => a.LimitWindowSeconds.CompareTo(b.LimitWindowSeconds));

        var additionalGroups = ParseAdditionalGroups(root);
        var groups = new List<QuotaGroup>();

        if (sharedWindows.Count > 0)
        {
            groups.Add(new QuotaGroup
            {
                GroupId = "codex-shared",
                GroupName = "Codex",
                ModelsList = new List<string> { "Shared Codex allowance" },
                Windows = new List<QuotaWindow>(sharedWindows)
            });
        }

        groups.AddRange(additionalGroups);

        QuotaWindow? representative = sharedWindows.OrderByDescending(w => w.LimitWindowSeconds).FirstOrDefault();

        return Task.FromResult(new ProviderQuotaResult
        {
            ProviderKey = ProviderKey,
            ProviderTitle = ProviderTitle,
            IconLetter = IconLetter,
            IconColorHex = IconColorHex,
            IconBgColorHex = IconBgColorHex,
            AuthMethod = AuthMethod,
            RequiresApiKey = false,
            IsSuccess = true,
            AuthStatus = ProviderAuthStatus.Connected,
            PlanType = "Pro (Mock)",
            DetailsSubtitle = "Spark UI verification fixture",
            Windows = sharedWindows,
            Groups = groups,
            PrimaryRemainingPercent = representative?.RemainingPercent ?? 100,
            NextResetInSeconds = representative?.ResetInSeconds ?? 0,
            FormattedNextResetIn = representative?.FormattedResetIn ?? "",
            ResetText = representative?.FormattedResetIn ?? "Available",
            FetchedAt = DateTimeOffset.UtcNow
        });
    }

    private static void ParseWindow(JsonElement rateLimit, string propertyName, List<QuotaWindow> windows, bool markUnusedAsAvailable)
    {
        ParseWindowMethod.Invoke(null, new object[] { rateLimit, propertyName, windows, markUnusedAsAvailable });
    }

    private static List<QuotaGroup> ParseAdditionalGroups(JsonElement root)
    {
        return ParseAdditionalMethod.Invoke(null, new object[] { root }) as List<QuotaGroup>
            ?? throw new InvalidOperationException("Additional rate-limit parser returned no result.");
    }
}
#endif
