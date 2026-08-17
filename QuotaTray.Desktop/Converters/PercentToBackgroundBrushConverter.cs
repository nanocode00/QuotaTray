using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace QuotaTray.Desktop.Converters;

public class PercentToBackgroundBrushConverter : IValueConverter
{
    private static readonly IBrush GreenBg = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
    private static readonly IBrush OrangeBg = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11));
    private static readonly IBrush RedBg = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
    private static readonly IBrush MutedBg = new SolidColorBrush(Color.FromArgb(30, 148, 163, 184));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double pct)
        {
            if (pct > 30.0) return GreenBg;
            if (pct >= 15.0) return OrangeBg;
            return RedBg;
        }

        if (value is int intPct)
        {
            if (intPct > 30) return GreenBg;
            if (intPct >= 15) return OrangeBg;
            return RedBg;
        }

        return MutedBg;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
