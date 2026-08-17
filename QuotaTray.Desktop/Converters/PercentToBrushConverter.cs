using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace QuotaTray.Desktop.Converters;

public class PercentToBrushConverter : IValueConverter
{
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double pct)
        {
            if (pct > 30.0) return GreenBrush;
            if (pct >= 15.0) return OrangeBrush;
            return RedBrush;
        }

        if (value is int intPct)
        {
            if (intPct > 30) return GreenBrush;
            if (intPct >= 15) return OrangeBrush;
            return RedBrush;
        }

        return MutedBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
