using System;

namespace QuotaTray.Core.Utils;

public static class TimeFormatter
{
    public static string FormatSeconds(long totalSeconds)
    {
        if (totalSeconds <= 0) return "Available";

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalDays >= 1)
        {
            int days = (int)ts.TotalDays;
            int hours = ts.Hours;
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }
        if (ts.TotalHours >= 1)
        {
            int hours = (int)ts.TotalHours;
            int minutes = ts.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (ts.TotalMinutes >= 1)
        {
            int minutes = (int)ts.TotalMinutes;
            int seconds = ts.Seconds;
            return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
        }
        return $"{ts.Seconds}s";
    }

    public static string FormatResetText(long totalSeconds)
    {
        if (totalSeconds <= 0) return "Reset: Available";
        return $"Reset in {FormatSeconds(totalSeconds)}";
    }
}
