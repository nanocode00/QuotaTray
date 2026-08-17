using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using QuotaTray.Core.Models;
using QuotaTray.Desktop.Services;

namespace QuotaTray.Desktop.ViewModels;

public class ProviderItemViewModel : INotifyPropertyChanged
{
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));

    private static readonly IBrush GreenBg = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
    private static readonly IBrush OrangeBg = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11));
    private static readonly IBrush RedBg = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
    private static readonly IBrush MutedBg = new SolidColorBrush(Color.FromArgb(30, 148, 163, 184));

    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string IconLetter { get; set; } = "?";
    public IBrush IconColor { get; set; } = GreenBrush;
    public IBrush IconBgColor { get; set; } = GreenBg;

    public IImage? IconImage => ProviderIconService.GetIcon(Key);

    public string IconSvgData => Key.ToLowerInvariant() switch
    {
        "codex" => "M14.949 6.547a3.94 3.94 0 0 0-.348-3.273 4.11 4.11 0 0 0-4.4-1.934A4.1 4.1 0 0 0 8.423.2 4.15 4.15 0 0 0 6.305.086a4.1 4.1 0 0 0-1.891.948 4.04 4.04 0 0 0-1.158 1.753 4.1 4.1 0 0 0-1.563.679A4 4 0 0 0 .554 4.72a3.99 3.99 0 0 0 .502 4.731 3.94 3.94 0 0 0 .346 3.274 4.11 4.11 0 0 0 4.402 1.933c.382.425.852.764 1.377.995.526.231 1.095.35 1.67.346 1.78.002 3.358-1.132 3.901-2.804a4.1 4.1 0 0 0 1.563-.68 4 4 0 0 0 1.14-1.253 3.99 3.99 0 0 0-.506-4.716m-6.097 8.406a3.05 3.05 0 0 1-1.945-.694l.096-.054 3.23-1.838a.53.53 0 0 0 .265-.455v-4.49l1.366.778q.02.011.025.035v3.722c-.003 1.653-1.361 2.992-3.037 2.996m-6.53-2.75a2.95 2.95 0 0 1-.36-2.01l.095.057L5.29 12.09a.53.53 0 0 0 .527 0l3.949-2.246v1.555a.05.05 0 0 1-.022.041L6.473 13.3c-1.454.826-3.311.335-4.15-1.098m-.85-6.94A3.02 3.02 0 0 1 3.07 3.949v3.785a.51.51 0 0 0 .262.451l3.93 2.237-1.366.779a.05.05 0 0 1-.048 0L2.585 9.342a2.98 2.98 0 0 1-1.113-4.094zm11.216 2.571L8.747 5.576l1.362-.776a.05.05 0 0 1 .048 0l3.265 1.86a3 3 0 0 1 1.173 1.207 2.96 2.96 0 0 1-.27 3.2 3.05 3.05 0 0 1-1.36.997V8.279a.52.52 0 0 0-.276-.445m1.36-2.015-.097-.057-3.226-1.855a.53.53 0 0 0-.53 0L6.249 6.153V4.598a.04.04 0 0 1 .019-.04L9.533 2.7a3.07 3.07 0 0 1 3.257.139c.474.325.843.778 1.066 1.303.223.526.289 1.103.191 1.664zM5.503 8.575 4.139 7.8a.05.05 0 0 1-.026-.037V4.049c0-.57.166-1.127.476-1.607s.752-.864 1.275-1.105a3.08 3.08 0 0 1 3.234.41l-.096.054-3.23 1.838a.53.53 0 0 0-.265.455zm.742-1.577 1.758-1 1.762 1v2l-1.755 1-1.762-1z",
        "antigravity" => "M12 2C12 7.52 7.52 12 2 12C7.52 12 12 16.48 12 22C12 16.48 16.48 12 22 12C16.48 12 12 7.52 12 2Z",
        "copilot" => "M23.922 16.997C23.061 18.492 18.063 22.02 12 22.02 5.937 22.02.939 18.492.078 16.997A.641.641 0 0 1 0 16.741v-2.869a.883.883 0 0 1 .053-.22c.372-.935 1.347-2.292 2.605-2.656.167-.429.414-1.055.644-1.517a10.098 10.098 0 0 1-.052-1.086c0-1.331.282-2.499 1.132-3.368.397-.406.89-.717 1.474-.952C7.255 2.937 9.248 1.98 11.978 1.98c2.731 0 4.767.957 6.166 2.093.584.235 1.077.546 1.474.952.85.869 1.132 2.037 1.132 3.368 0 .368-.014.733-.052 1.086.23.462.477 1.088.644 1.517 1.258.364 2.233 1.721 2.605 2.656a.841.841 0 0 1 .053.22v2.869a.641.641 0 0 1-.078.256Zm-11.75-5.992h-.344a4.359 4.359 0 0 1-.355.508c-.77.947-1.918 1.492-3.508 1.492-1.725 0-2.989-.359-3.782-1.259a2.137 2.137 0 0 1-.085-.104L4 11.746v6.585c1.435.779 4.514 2.179 8 2.179 3.486 0 6.565-1.4 8-2.179v-6.585l-.034.029a2.137 2.137 0 0 1-.085.104c-.793.9-2.057 1.259-3.782 1.259-1.59 0-2.738-.545-3.508-1.492a4.359 4.359 0 0 1-.355-.508h-.344a.434.434 0 0 1-.41-.434V9.52a.434.434 0 0 1 .41-.434h.344a4.359 4.359 0 0 1 .355-.508c.77-.947 1.918-1.492 3.508-1.492 1.725 0 2.989.359 3.782 1.259.03.034.058.069.085.104L20 8.479V1.894C18.565 1.115 15.486-.285 12-.285c-3.486 0-6.565 1.4-8 2.179v6.585l.034-.029c.027-.035.055-.07.085-.104.793-.9 2.057-1.259 3.782-1.259 1.59 0 2.738.545 3.508 1.492.128.158.247.33.355.508h.344a.434.434 0 0 1 .41.434v1.045a.434.434 0 0 1-.41.434Z",
        "claude" => "M13.5 2h-3v6.34L6.02 3.86 3.9 5.98l4.48 4.48H2v3h6.38l-4.48 4.48 2.12 2.12L10.5 15.66V22h3v-6.34l4.48 4.48 2.12-2.12-4.48-4.48H22v-3h-6.38l4.48-4.48-2.12-2.12-4.48 4.48V2z",
        "openrouter" => "M18.17 1.4c2.56 0 4.63 2.07 4.63 4.63s-2.07 4.63-4.63 4.63l4.59 4.6c.58.58.17 1.58-.65 1.58H8.9c-4.26 0-7.72-3.46-7.72-7.72S4.64 1.4 8.9 1.4h9.27ZM8.9 4.49c-2.56 0-4.63 2.07-4.63 4.63s2.07 4.63 4.63 4.63 4.63-2.07 4.63-4.63-2.07-4.63-4.63-4.63Z",
        "kimi" => "M21.846 0a1.923 1.923 0 110 3.846H20.15a.226.226 0 01-.227-.226V1.923C19.923.861 20.784 0 21.846 0z M11.065 11.199l7.257-7.2c.137-.136.06-.41-.116-.41H14.3a.164.164 0 00-.117.051l-7.82 7.756c-.122.12-.302.013-.302-.179V3.82c0-.127-.083-.23-.185-.23H4.07a.164.164 0 00-.117.051L.051 7.42A.164.164 0 000 7.537v15.398c0 .588.477 1.065 1.065 1.065h4.156a.164.164 0 00.117-.051l5.727-5.68 5.727 5.68a.164.164 0 00.117.051h3.948c.176 0 .253-.274.116-.41l-7.257-7.2a.164.164 0 010-.232z",
        "zai" => "M3 4h18l-10 9h10v7H3l10-9H3V4z",
        "synthetic" => "M12 2L3 7.2v9.6L12 22l9-5.2V7.2L12 2zm0 3.5l5.8 3.3-5.8 3.4-5.8-3.4L12 5.5zM5 10.5l5.8 3.4v6.6L5 17.1v-6.6zm8.2 10v-6.6l5.8-3.4v6.6l-5.8 3.4z",
        _ => "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z"
    };

    public string AuthMethod { get; set; } = "OAuth";
    public string? CliLoginCommand { get; set; }
    public bool RequiresApiKey { get; set; }
    public bool HasCliLoginCommand => !string.IsNullOrEmpty(CliLoginCommand);

    private bool _isSuccess;
    private bool _isAuthMissing;
    private string _errorMessage = "";
    private string _planType = "";
    private string _detailsSubtitle = "";
    private double _primaryRemainingPercent;
    private string _primaryRemainingPercentText = "--";
    private string _resetText = "";
    private bool _isModelsExpanded;
    private bool _isBalanceProvider;
    private string _balanceFormatted = "";

    public ObservableCollection<QuotaGroupViewModel> Groups { get; } = new();
    public ObservableCollection<QuotaWindow> Windows { get; } = new();
    public ObservableCollection<ModelQuotaInfo> Models { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSuccess
    {
        get => _isSuccess;
        set => SetField(ref _isSuccess, value);
    }

    public bool IsAuthMissing
    {
        get => _isAuthMissing;
        set => SetField(ref _isAuthMissing, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public string PlanType
    {
        get => _planType;
        set => SetField(ref _planType, value);
    }

    public string DetailsSubtitle
    {
        get => _detailsSubtitle;
        set => SetField(ref _detailsSubtitle, value);
    }

    public double PrimaryRemainingPercent
    {
        get => _primaryRemainingPercent;
        set
        {
            if (SetField(ref _primaryRemainingPercent, value))
            {
                OnPropertyChanged(nameof(RemainingBrush));
                OnPropertyChanged(nameof(RemainingBgBrush));
            }
        }
    }

    public string PrimaryRemainingPercentText
    {
        get => _primaryRemainingPercentText;
        set => SetField(ref _primaryRemainingPercentText, value);
    }

    public string ResetText
    {
        get => _resetText;
        set => SetField(ref _resetText, value);
    }

    public bool IsModelsExpanded
    {
        get => _isModelsExpanded;
        set
        {
            if (SetField(ref _isModelsExpanded, value))
            {
                OnPropertyChanged(nameof(ModelsToggleText));
            }
        }
    }

    public string ModelsToggleText => IsModelsExpanded ? "▼ 모델 목록 접기" : (Models.Count > 0 ? $"▶ 전체 {Models.Count}개 모델 보기" : "▶ 모델 보기");

    private string _primaryWindowName = "Quota";
    public string PrimaryWindowName
    {
        get => _primaryWindowName;
        set => SetField(ref _primaryWindowName, value);
    }

    private string _compactMetaText = "";
    public string CompactMetaText
    {
        get => _compactMetaText;
        set
        {
            if (SetField(ref _compactMetaText, value))
            {
                OnPropertyChanged(nameof(CompactResetText));
            }
        }
    }

    public string CompactResetText => CompactMetaText;

    public static string ShortenWindowName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Quota";
        string s = raw.Trim();
        if (s.StartsWith("Credits", StringComparison.OrdinalIgnoreCase)) return "Credits";
        if (s.StartsWith("Premium", StringComparison.OrdinalIgnoreCase)) return "Premium";
        if (s.Contains("Weekly", StringComparison.OrdinalIgnoreCase) || s.Contains("Week", StringComparison.OrdinalIgnoreCase)) return "Weekly";
        if (s.Contains("7 Day", StringComparison.OrdinalIgnoreCase) || s.Equals("7d", StringComparison.OrdinalIgnoreCase)) return "7d";
        if (s.Contains("5 Hour", StringComparison.OrdinalIgnoreCase) || s.Equals("5h", StringComparison.OrdinalIgnoreCase)) return "5h";
        if (s.Contains("Daily", StringComparison.OrdinalIgnoreCase) || s.Contains("Day", StringComparison.OrdinalIgnoreCase)) return "Daily";
        if (s.Contains("Monthly", StringComparison.OrdinalIgnoreCase) || s.Contains("Month", StringComparison.OrdinalIgnoreCase)) return "Monthly";
        if (s.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)) return "Sonnet";
        if (s.Contains("Haiku", StringComparison.OrdinalIgnoreCase)) return "Haiku";
        if (s.Contains("Opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (s.Contains("Flash", StringComparison.OrdinalIgnoreCase)) return "Flash";
        if (s.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return "Pro";
        if (s.Contains("Token", StringComparison.OrdinalIgnoreCase)) return "Tokens";
        if (s.Contains("Request", StringComparison.OrdinalIgnoreCase)) return "Requests";
        if (s.Contains("Balance", StringComparison.OrdinalIgnoreCase)) return "Credits";

        int pIdx = s.IndexOf('(');
        if (pIdx > 0) s = s.Substring(0, pIdx).Trim();

        return s.Length > 8 ? s.Substring(0, 8) : s;
    }

    public static string ShortenMetaText(string? raw, bool isBalanceProvider)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return isBalanceProvider ? "$0 used" : "";
        }
        string s = raw.Trim();
        if (s.StartsWith("Used", StringComparison.OrdinalIgnoreCase))
        {
            string amount = s.Replace("Used", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (amount.StartsWith("$0.00") || amount.Equals("$0")) return "$0 used";
            return $"{amount} used";
        }
        if (s.Contains("used", StringComparison.OrdinalIgnoreCase))
        {
            return s;
        }

        string cleaned = s.Replace("Reset in ", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("Reset: ", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("Available", "즉시", StringComparison.OrdinalIgnoreCase)
                          .Trim();

        if (string.IsNullOrEmpty(cleaned) || cleaned.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return isBalanceProvider ? "$0 used" : "";
        }

        if (cleaned.StartsWith("↻")) return cleaned;
        return $"↻ {cleaned}";
    }

    public bool HasGroups => Groups.Count > 0;
    public bool HasWindows => Windows.Count > 0 && !HasGroups;
    public bool HasModels => Models.Count > 0;
    public bool HasModelsOnly => HasModels && !HasWindows && !HasGroups;

    public bool IsBalanceProvider
    {
        get => _isBalanceProvider;
        set => SetField(ref _isBalanceProvider, value);
    }

    public string BalanceFormatted
    {
        get => _balanceFormatted;
        set => SetField(ref _balanceFormatted, value);
    }

    public IBrush RemainingBrush
    {
        get
        {
            if (!_isSuccess) return MutedBrush;
            if (_primaryRemainingPercent > 30.0) return GreenBrush;
            if (_primaryRemainingPercent >= 15.0) return OrangeBrush;
            return RedBrush;
        }
    }

    public IBrush RemainingBgBrush
    {
        get
        {
            if (!_isSuccess) return MutedBg;
            if (_primaryRemainingPercent > 30.0) return GreenBg;
            if (_primaryRemainingPercent >= 15.0) return OrangeBg;
            return RedBg;
        }
    }

    public void ToggleModels()
    {
        IsModelsExpanded = !IsModelsExpanded;
    }

    public void UpdateFromResult(ProviderQuotaResult res)
    {
        Key = res.ProviderKey;
        Title = res.ProviderTitle;
        IconLetter = res.IconLetter;
        AuthMethod = res.AuthMethod;
        CliLoginCommand = res.CliLoginCommand;
        RequiresApiKey = res.RequiresApiKey;

        try
        {
            if (Color.TryParse(res.IconColorHex, out var iconColor))
            {
                IconColor = new SolidColorBrush(iconColor);
            }
            if (Color.TryParse(res.IconBgColorHex, out var iconBgColor))
            {
                IconBgColor = new SolidColorBrush(iconBgColor);
            }
        }
        catch { }

        IsSuccess = res.IsSuccess;
        IsAuthMissing = res.IsAuthMissing;
        ErrorMessage = res.ErrorMessage ?? "";
        PlanType = res.PlanType ?? "";
        DetailsSubtitle = res.DetailsSubtitle ?? "";
        IsBalanceProvider = res.IsBalanceProvider;
        BalanceFormatted = res.BalanceFormatted ?? "";

        // Update Groups
        Groups.Clear();
        if (res.Groups != null)
        {
            foreach (var g in res.Groups)
            {
                Groups.Add(QuotaGroupViewModel.FromModel(g));
            }
        }

        // Update Windows
        Windows.Clear();
        if (res.Windows != null)
        {
            foreach (var w in res.Windows)
            {
                Windows.Add(w);
            }
        }

        // Update Models
        Models.Clear();
        if (res.Models != null)
        {
            foreach (var m in res.Models)
            {
                Models.Add(m);
            }
        }

        // Apply provider-specific representative quota resolution rules
        string cleanKey = Key.ToLowerInvariant();

        if (cleanKey.Equals("codex"))
        {
            // Codex: Use longest duration window (e.g. 7d / Weekly / Monthly) as representative value; 5h is detailed only
            var longestWindow = Windows.OrderByDescending(w => w.LimitWindowSeconds).FirstOrDefault()
                                ?? Windows.FirstOrDefault();

            if (longestWindow != null)
            {
                PrimaryWindowName = ShortenWindowName(longestWindow.Name);
                PrimaryRemainingPercent = longestWindow.RemainingPercent;
                PrimaryRemainingPercentText = res.IsSuccess ? $"{longestWindow.RemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                CompactMetaText = ShortenMetaText(!string.IsNullOrEmpty(longestWindow.FormattedResetIn) ? longestWindow.FormattedResetIn : res.FormattedNextResetIn, false);
            }
            else
            {
                PrimaryWindowName = "Quota";
                PrimaryRemainingPercent = res.PrimaryRemainingPercent;
                PrimaryRemainingPercentText = res.IsSuccess ? $"{res.PrimaryRemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                CompactMetaText = ShortenMetaText(res.ResetText ?? res.FormattedNextResetIn, false);
            }
        }
        else if (cleanKey.Equals("antigravity"))
        {
            // Antigravity: Find all Weekly windows across all returned groups and select the lowest remaining percent; 5h is detailed only
            var weeklyWindows = Groups.SelectMany(g => g.Windows)
                                      .Concat(Windows)
                                      .Where(w => w.Name.Equals("Weekly", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("Week", StringComparison.OrdinalIgnoreCase) || w.LimitWindowSeconds >= 86400 * 5)
                                      .Distinct()
                                      .ToList();

            QuotaWindow? lowestWeekly = weeklyWindows.OrderBy(w => w.RemainingPercent).FirstOrDefault()
                                       ?? Groups.SelectMany(g => g.Windows).Concat(Windows).OrderBy(w => w.RemainingPercent).FirstOrDefault();

            PrimaryWindowName = "Weekly";
            if (lowestWeekly != null)
            {
                PrimaryRemainingPercent = lowestWeekly.RemainingPercent;
                PrimaryRemainingPercentText = res.IsSuccess ? $"{lowestWeekly.RemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                CompactMetaText = ShortenMetaText(!string.IsNullOrEmpty(lowestWeekly.FormattedResetIn) ? lowestWeekly.FormattedResetIn : res.FormattedNextResetIn, false);
            }
            else
            {
                PrimaryRemainingPercent = res.PrimaryRemainingPercent;
                PrimaryRemainingPercentText = res.IsSuccess ? $"{res.PrimaryRemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                CompactMetaText = ShortenMetaText(res.ResetText ?? res.FormattedNextResetIn, false);
            }
        }
        else if (cleanKey.Equals("copilot"))
        {
            // GitHub Copilot: Free plan uses Code Completions; Paid plans use Premium Requests; Chat is secondary
            bool isFree = !string.IsNullOrEmpty(PlanType) && PlanType.Contains("Free", StringComparison.OrdinalIgnoreCase);

            var allCopilotWindows = Groups.SelectMany(g => g.Windows).Concat(Windows).ToList();
            var completionsWin = allCopilotWindows.FirstOrDefault(w => w.Name.Contains("Completion", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("코드 완성") || w.Name.Contains("완성"));
            var premiumWin = allCopilotWindows.FirstOrDefault(w => w.Name.Contains("Premium", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("프리미엄"));

            if (isFree)
            {
                var rep = completionsWin ?? allCopilotWindows.FirstOrDefault();
                PrimaryWindowName = "Code";
                if (rep != null)
                {
                    PrimaryRemainingPercent = rep.RemainingPercent;
                    PrimaryRemainingPercentText = res.IsSuccess ? $"{rep.RemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                    CompactMetaText = ShortenMetaText(!string.IsNullOrEmpty(rep.FormattedResetIn) ? rep.FormattedResetIn : res.FormattedNextResetIn, false);
                }
                else
                {
                    PrimaryRemainingPercent = res.PrimaryRemainingPercent;
                    PrimaryRemainingPercentText = res.IsSuccess ? $"{res.PrimaryRemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                    CompactMetaText = ShortenMetaText(res.ResetText ?? res.FormattedNextResetIn, false);
                }
            }
            else
            {
                var rep = premiumWin ?? allCopilotWindows.FirstOrDefault();
                PrimaryWindowName = "Premium";
                if (rep != null)
                {
                    PrimaryRemainingPercent = rep.RemainingPercent;
                    PrimaryRemainingPercentText = res.IsSuccess ? $"{rep.RemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                    CompactMetaText = ShortenMetaText(!string.IsNullOrEmpty(rep.FormattedResetIn) ? rep.FormattedResetIn : res.FormattedNextResetIn, false);
                }
                else
                {
                    PrimaryRemainingPercent = res.PrimaryRemainingPercent;
                    PrimaryRemainingPercentText = res.IsSuccess ? $"{res.PrimaryRemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                    CompactMetaText = ShortenMetaText(res.ResetText ?? res.FormattedNextResetIn, false);
                }
            }
        }
        else if (res.IsBalanceProvider || cleanKey.Equals("openrouter"))
        {
            // OpenRouter & Balance Providers: Representative value is remaining account credit balance ($10.00)
            PrimaryWindowName = "Credits";
            PrimaryRemainingPercent = 100.0;
            if (!string.IsNullOrEmpty(res.BalanceFormatted))
            {
                PrimaryRemainingPercentText = res.BalanceFormatted;
            }
            else
            {
                PrimaryRemainingPercentText = res.IsSuccess ? "$0.00" : (res.IsAuthMissing ? "Auth Required" : "Error");
            }

            var allWins = Groups.SelectMany(g => g.Windows).Concat(Windows).ToList();
            var creditWin = allWins.FirstOrDefault(w => w.Name.Contains("Credit", StringComparison.OrdinalIgnoreCase)) ?? allWins.FirstOrDefault();

            if (creditWin != null && !string.IsNullOrEmpty(creditWin.FormattedResetIn))
            {
                CompactMetaText = ShortenMetaText(creditWin.FormattedResetIn, true);
            }
            else if (!string.IsNullOrEmpty(res.ResetText))
            {
                CompactMetaText = ShortenMetaText(res.ResetText, true);
            }
            else
            {
                CompactMetaText = "$0 used";
            }
        }
        else
        {
            // General providers (Claude Code, Kimi, Z.AI, Synthetic, etc.)
            QuotaWindow? mostConstrained = null;
            if (Windows.Count > 0)
            {
                mostConstrained = Windows.OrderBy(w => w.RemainingPercent).FirstOrDefault();
            }
            else if (Groups.Count > 0)
            {
                mostConstrained = Groups.SelectMany(g => g.Windows).OrderBy(w => w.RemainingPercent).FirstOrDefault();
            }

            if (mostConstrained != null)
            {
                PrimaryWindowName = ShortenWindowName(mostConstrained.Name);
                PrimaryRemainingPercent = mostConstrained.RemainingPercent;
                PrimaryRemainingPercentText = res.IsSuccess ? $"{mostConstrained.RemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");

                if (!string.IsNullOrEmpty(mostConstrained.FormattedResetIn))
                {
                    CompactMetaText = ShortenMetaText(mostConstrained.FormattedResetIn, false);
                }
                else
                {
                    CompactMetaText = ShortenMetaText(res.ResetText ?? res.FormattedNextResetIn, false);
                }
            }
            else
            {
                PrimaryWindowName = "Quota";
                PrimaryRemainingPercent = res.PrimaryRemainingPercent;
                PrimaryRemainingPercentText = res.IsSuccess ? $"{res.PrimaryRemainingPercent:F0}% left" : (res.IsAuthMissing ? "Auth Required" : "Error");
                CompactMetaText = ShortenMetaText(res.ResetText ?? res.FormattedNextResetIn, false);
            }
        }

        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(HasModelsOnly));
        OnPropertyChanged(nameof(HasCliLoginCommand));
        OnPropertyChanged(nameof(ModelsToggleText));
        OnPropertyChanged(nameof(PrimaryWindowName));
        OnPropertyChanged(nameof(CompactMetaText));
        OnPropertyChanged(nameof(CompactResetText));
        OnPropertyChanged(nameof(IconImage));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
