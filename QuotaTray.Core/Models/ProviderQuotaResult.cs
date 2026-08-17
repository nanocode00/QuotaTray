using System;
using System.Collections.Generic;

namespace QuotaTray.Core.Models;

public enum ProviderAuthStatus
{
    NotConfigured,
    Connected,
    Expired,
    Error
}

public class ProviderQuotaResult
{
    public string ProviderKey { get; set; } = "";
    public string ProviderTitle { get; set; } = "";
    public string IconLetter { get; set; } = "?";
    public string IconColorHex { get; set; } = "#10B981";
    public string IconBgColorHex { get; set; } = "#1A2E26";

    public bool IsSuccess { get; set; }
    public bool IsAuthMissing { get; set; }
    public ProviderAuthStatus AuthStatus { get; set; } = ProviderAuthStatus.NotConfigured;
    public string? ErrorMessage { get; set; }
    public string? PlanType { get; set; }
    public string? AccountEmail { get; set; }
    public string DetailsSubtitle { get; set; } = "";

    // Auth info
    public string AuthMethod { get; set; } = "OAuth";
    public string? CliLoginCommand { get; set; }
    public bool RequiresApiKey { get; set; }

    // Multi-group quota pools (Antigravity: Gemini Pool & Claude/GPT Pool)
    public List<QuotaGroup> Groups { get; set; } = new();
    public bool HasGroups => Groups.Count > 0;

    // For single-group 5h/7d windows (Codex, Claude, Copilot, OpenRouter)
    public List<QuotaWindow> Windows { get; set; } = new();
    public bool HasWindows => Windows.Count > 0 && !HasGroups;

    // For Antigravity per-model breakdown (optional)
    public List<ModelQuotaInfo> Models { get; set; } = new();
    public int ModelCount => Models.Count;
    public ModelQuotaInfo? TightestModel { get; set; }

    // For Balance / Credits (OpenRouter, Kimi, Z.AI)
    public bool IsBalanceProvider { get; set; }
    public double? BalanceAmount { get; set; }
    public string? BalanceCurrency { get; set; } = "$";
    public string? BalanceFormatted { get; set; }

    // Summary remaining & reset
    public double PrimaryRemainingPercent { get; set; }
    public long NextResetInSeconds { get; set; }
    public string FormattedNextResetIn { get; set; } = "";
    public string ResetText { get; set; } = "";

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}
