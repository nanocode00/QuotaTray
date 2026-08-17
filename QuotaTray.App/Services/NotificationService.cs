using System;
using System.Collections.Generic;
using System.Windows.Forms;
using QuotaTray.App.Models;
using QuotaTray.App.ViewModels;

namespace QuotaTray.App.Services;

public class NotificationService
{
    private readonly Dictionary<string, double> _lastPercentages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lowNotifiedProviders = new(StringComparer.OrdinalIgnoreCase);

    public bool IsNotificationsEnabled { get; set; } = true;
    public double LowThresholdPercent { get; set; } = 15.0;

    public event Action<string, string, ToolTipIcon>? NotificationRequested;

    public void ApplySettings(AppSettings settings)
    {
        IsNotificationsEnabled = settings.EnableNotifications;
        LowThresholdPercent = settings.WarningThresholdPercent;
    }

    public void EvaluateQuotaUpdate(QuotaViewModel vm)
    {
        if (!IsNotificationsEnabled) return;

        foreach (var provider in vm.ActiveProviders)
        {
            if (!provider.IsSuccess || provider.IsBalanceProvider) continue;

            double current = provider.PrimaryRemainingPercent;
            string key = provider.Key;

            // 1. Low Quota Alert
            if (current <= LowThresholdPercent && !_lowNotifiedProviders.Contains(key))
            {
                _lowNotifiedProviders.Add(key);
                string resetInfo = !string.IsNullOrEmpty(provider.ResetText) ? $" ({provider.ResetText})" : "";
                NotificationRequested?.Invoke(
                    $"⚠️ {provider.Title} Quota 부족 경고",
                    $"{provider.Title} 잔여량이 {(int)Math.Round(current)}%로 설정 기준({(int)LowThresholdPercent}%) 이하입니다.{resetInfo}",
                    ToolTipIcon.Warning
                );
            }
            // 2. Refill / Reset Alert
            else if (_lastPercentages.TryGetValue(key, out double last) && last <= LowThresholdPercent && current >= 80.0)
            {
                _lowNotifiedProviders.Remove(key);
                NotificationRequested?.Invoke(
                    $"🎉 {provider.Title} Quota 충전 완료",
                    $"{provider.Title} 쿼터가 리셋되었습니다! (현재 {(int)Math.Round(current)}% 사용 가능)",
                    ToolTipIcon.Info
                );
            }
            else if (current > LowThresholdPercent + 10.0)
            {
                _lowNotifiedProviders.Remove(key);
            }

            _lastPercentages[key] = current;
        }
    }
}
