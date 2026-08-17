using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using QuotaTray.Core.Models;
using QuotaTray.Core.Platform;
using QuotaTray.Core.Services;
using QuotaTray.Core.Utils;
using QuotaTray.Desktop.Models;

namespace QuotaTray.Desktop.ViewModels;

public class SettingOptionItem
{
    public string Label { get; set; } = "";
    public int Value { get; set; }

    public SettingOptionItem(string label, int value)
    {
        Label = label;
        Value = value;
    }

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is SettingOptionItem other && Value == other.Value;
    }

    public override int GetHashCode() => Value.GetHashCode();
}

public class QuotaViewModel : INotifyPropertyChanged
{
    public List<SettingOptionItem> RefreshIntervalOptions { get; } = new()
    {
        new("1분", 1),
        new("3분", 3),
        new("5분", 5),
        new("10분", 10),
        new("15분", 15),
        new("30분", 30)
    };

    public List<SettingOptionItem> WarningThresholdOptions { get; } = new()
    {
        new("5% 이하", 5),
        new("10% 이하", 10),
        new("15% 이하", 15),
        new("20% 이하", 20),
        new("30% 이하", 30),
        new("50% 이하", 50)
    };

    private readonly QuotaService _quotaService;
    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly AppSettings _settings;
    private readonly IPlatformHelper _platformHelper;
    private readonly IAutoStartupService _autoStartupService;

    private bool _isRefreshing;
    private bool _isRefreshFailed;
    private string _statusMessage = "조회 중...";
    private DateTimeOffset? _lastUpdated;
    private List<ProviderQuotaResult> _lastResults = new();

    private readonly HashSet<string> _notifiedLowProviders = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProviderItemViewModel> ActiveProviders { get; } = new();
    public ObservableCollection<ProviderToggleOption> AvailableProviders { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RefreshCompleted;
    public event Action<AppSettings>? SettingsChanged;
    public event Action<string, string>? ShowToastNotification;

    public QuotaViewModel(
        QuotaService? quotaService = null,
        IPlatformHelper? platformHelper = null,
        IAutoStartupService? autoStartupService = null)
    {
        _quotaService = quotaService ?? new QuotaService();
        _platformHelper = platformHelper ?? new CrossPlatformHelper();
        _autoStartupService = autoStartupService ?? new CrossPlatformAutoStartup();
        _settings = AppSettings.Load();

        var allMetas = _quotaService.GetAvailableProviders();
        foreach (var meta in allMetas)
        {
            var opt = new ProviderToggleOption
            {
                Key = meta.Key,
                Title = meta.Title,
                IconLetter = meta.IconLetter,
                IconColorHex = meta.IconColorHex,
                AuthMethod = meta.AuthMethod,
                CliLoginCommand = meta.CliLoginCommand,
                RequiresApiKey = meta.RequiresApiKey,
                ApiKey = _settings.GetApiKey(meta.Key) ?? "",
                IsChecked = _settings.EnabledProviders.Contains(meta.Key, StringComparer.OrdinalIgnoreCase)
            };
            opt.IsCheckedChanged += OnProviderToggleChanged;
            opt.ApiKeySaved += (key, apiKey) =>
            {
                _settings.SetApiKey(key, apiKey);
                _ = RefreshQuotaAsync();
            };
            AvailableProviders.Add(opt);
        }

        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.AutoRefreshMinutes))
        };
        _autoRefreshTimer.Tick += async (s, e) => await RefreshQuotaAsync();
        _autoRefreshTimer.Start();
    }

    private void OnProviderToggleChanged()
    {
        var enabled = AvailableProviders.Where(p => p.IsChecked).Select(p => p.Key).ToList();
        if (enabled.Count == 0)
        {
            enabled = new List<string> { "codex", "antigravity" };
        }

        _settings.EnabledProviders = enabled;
        _settings.Save();
        SettingsChanged?.Invoke(_settings);

        _ = RefreshQuotaAsync();
    }

    #region Settings Properties

    public AppSettings Settings => _settings;
    public IPlatformHelper PlatformHelper => _platformHelper;
    public IAutoStartupService AutoStartupService => _autoStartupService;

    public bool IsCompactMode
    {
        get => _settings.IsCompactMode;
        set
        {
            if (_settings.IsCompactMode != value)
            {
                _settings.IsCompactMode = value;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDetailedMode));
                OnPropertyChanged(nameof(CompactModeToggleTooltip));
                OnPropertyChanged(nameof(CompactModeIconData));
            }
        }
    }

    public bool IsDetailedMode => !IsCompactMode;
    public string CompactModeToggleTooltip => IsCompactMode ? "상세 모드로 최대화" : "간단 모드로 최소화";

    public string CompactModeIconData => IsCompactMode
        ? "M4 4h16v16H4V4zm2 4v10h12V8H6z"
        : "M20 14H4v-2h16v2z";

    public SettingOptionItem? SelectedRefreshIntervalOption
    {
        get => RefreshIntervalOptions.FirstOrDefault(o => o.Value == AutoRefreshMinutes) ?? RefreshIntervalOptions[1];
        set
        {
            if (value != null && AutoRefreshMinutes != value.Value)
            {
                AutoRefreshMinutes = value.Value;
                OnPropertyChanged();
            }
        }
    }

    public SettingOptionItem? SelectedWarningThresholdOption
    {
        get => WarningThresholdOptions.FirstOrDefault(o => o.Value == WarningThresholdPercent) ?? WarningThresholdOptions[2];
        set
        {
            if (value != null && WarningThresholdPercent != value.Value)
            {
                WarningThresholdPercent = value.Value;
                OnPropertyChanged();
            }
        }
    }

    public int AutoRefreshMinutes
    {
        get => _settings.AutoRefreshMinutes;
        set
        {
            if (_settings.AutoRefreshMinutes != value)
            {
                _settings.AutoRefreshMinutes = value;
                _settings.Save();
                _autoRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, value));
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedRefreshIntervalOption));
                UpdateStatusMessage();
                SettingsChanged?.Invoke(_settings);
            }
        }
    }

    public int WarningThresholdPercent
    {
        get => _settings.WarningThresholdPercent;
        set
        {
            if (_settings.WarningThresholdPercent != value)
            {
                _settings.WarningThresholdPercent = value;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedWarningThresholdOption));
                SettingsChanged?.Invoke(_settings);
            }
        }
    }

    public bool EnableNotifications
    {
        get => _settings.EnableNotifications;
        set
        {
            if (_settings.EnableNotifications != value)
            {
                _settings.EnableNotifications = value;
                _settings.Save();
                OnPropertyChanged();
                SettingsChanged?.Invoke(_settings);
            }
        }
    }

    public void LaunchCliLogin(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        _platformHelper.LaunchTerminal(command);
    }

    public string GetDiagnosticsReport()
    {
        return QuotaDiagnostics.GenerateReport(_lastResults, AutoRefreshMinutes, WarningThresholdPercent);
    }

    #endregion

    #region Status Properties

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetField(ref _isRefreshing, value);
    }

    public bool IsRefreshFailed
    {
        get => _isRefreshFailed;
        set => SetField(ref _isRefreshFailed, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string CompactSummaryText
    {
        get
        {
            if (ActiveProviders.Count == 0) return "AI Quota";
            var parts = ActiveProviders.Where(p => p.IsSuccess).Select(p =>
            {
                string val = p.IsBalanceProvider ? (p.BalanceFormatted ?? "$0.00") : $"{p.PrimaryRemainingPercent:F0}%";
                return $"{p.Title.Split(' ')[0]} {val}";
            });
            return string.Join(" · ", parts);
        }
    }

    public string TrayTooltipText
    {
        get
        {
            if (ActiveProviders.Count == 0) return "AI Quota";
            var parts = ActiveProviders.Select(p =>
            {
                string text = p.IsSuccess ? p.PrimaryRemainingPercentText : "오류";
                return $"{p.Title.Split(' ')[0]}: {text}";
            });
            string joined = string.Join(" | ", parts);
            return joined.Length > 63 ? joined[..63] : joined;
        }
    }

    public double LowestRemainingPercent
    {
        get
        {
            if (ActiveProviders.Count == 0) return 100.0;
            double lowest = 100.0;
            bool found = false;
            foreach (var p in ActiveProviders)
            {
                if (p.IsSuccess && !p.IsBalanceProvider)
                {
                    lowest = Math.Min(lowest, p.PrimaryRemainingPercent);
                    found = true;
                }
            }
            return found ? lowest : 100.0;
        }
    }

    public bool HasAnyError => ActiveProviders.Any(p => !p.IsSuccess);

    #endregion

    public async Task RefreshQuotaAsync()
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsRefreshing = true;
                StatusMessage = "쿼터 조회 중...";
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var results = await _quotaService.RefreshSelectedAsync(_settings.EnabledProviders, _settings.GetApiKey, cts.Token);
            _lastResults = results;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyResults(results);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsRefreshFailed = true;
                StatusMessage = _lastUpdated.HasValue
                    ? $"새로고침 실패 · 마지막 성공 {_lastUpdated.Value.ToLocalTime():HH:mm}"
                    : $"새로고침 실패: {ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsRefreshing = false;
            });
            _refreshLock.Release();
            RefreshCompleted?.Invoke();
        }
    }

    private void UpdateStatusMessage()
    {
        if (_lastUpdated.HasValue && !IsRefreshFailed)
        {
            StatusMessage = $"Updated {_lastUpdated.Value.ToLocalTime():HH:mm} · Auto {AutoRefreshMinutes} min";
        }
    }

    private void ApplyResults(List<ProviderQuotaResult> results)
    {
        _lastUpdated = DateTimeOffset.UtcNow;
        bool anySuccess = results.Any(r => r.IsSuccess);
        IsRefreshFailed = !anySuccess && results.Count > 0;

        if (anySuccess)
        {
            StatusMessage = $"Updated {_lastUpdated.Value.ToLocalTime():HH:mm} · Auto {AutoRefreshMinutes} min";
        }
        else
        {
            StatusMessage = "쿼터 정보를 가져오지 못했습니다";
        }

        foreach (var res in results)
        {
            var targetOpt = AvailableProviders.FirstOrDefault(p => p.Key.Equals(res.ProviderKey, StringComparison.OrdinalIgnoreCase));
            if (targetOpt != null)
            {
                targetOpt.AuthStatus = res.AuthStatus;
            }
        }

        if (EnableNotifications)
        {
            foreach (var res in results)
            {
                if (res.IsSuccess && !res.IsBalanceProvider)
                {
                    if (res.PrimaryRemainingPercent <= WarningThresholdPercent)
                    {
                        if (!_notifiedLowProviders.Contains(res.ProviderKey))
                        {
                            _notifiedLowProviders.Add(res.ProviderKey);
                            ShowToastNotification?.Invoke(
                                $"{res.ProviderTitle} 쿼터 부족 경고",
                                $"현재 남은 쿼터가 {res.PrimaryRemainingPercent:F0}% 입니다. ({res.FormattedNextResetIn})"
                            );
                        }
                    }
                    else
                    {
                        _notifiedLowProviders.Remove(res.ProviderKey);
                    }
                }
            }
        }

        var existingDict = ActiveProviders.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var updatedList = new List<ProviderItemViewModel>();

        foreach (var res in results)
        {
            if (existingDict.TryGetValue(res.ProviderKey, out var existingVm))
            {
                existingVm.UpdateFromResult(res);
                updatedList.Add(existingVm);
            }
            else
            {
                var newVm = new ProviderItemViewModel();
                newVm.UpdateFromResult(res);
                updatedList.Add(newVm);
            }
        }

        ActiveProviders.Clear();
        foreach (var item in updatedList)
        {
            ActiveProviders.Add(item);
        }

        OnPropertyChanged(nameof(CompactSummaryText));
        OnPropertyChanged(nameof(TrayTooltipText));
        OnPropertyChanged(nameof(LowestRemainingPercent));
        OnPropertyChanged(nameof(HasAnyError));
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
