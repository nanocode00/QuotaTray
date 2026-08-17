using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace QuotaTray.App.Converters;

public class PercentToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(MediaColor.FromRgb(16, 185, 129));   // #10B981 (> 30%)
    private static readonly SolidColorBrush OrangeBrush = new(MediaColor.FromRgb(245, 158, 11));  // #F59E0B (15% ~ 30%)
    private static readonly SolidColorBrush RedBrush = new(MediaColor.FromRgb(239, 68, 68));      // #EF4444 (<= 15%)
    private static readonly SolidColorBrush MutedBrush = new(MediaColor.FromRgb(161, 161, 170));  // #A1A1AA

    static PercentToBrushConverter()
    {
        GreenBrush.Freeze();
        OrangeBrush.Freeze();
        RedBrush.Freeze();
        MutedBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double d = 0;
        if (value is double dv) d = dv;
        else if (value is int iv) d = iv;
        else if (value is float fv) d = fv;
        else return MutedBrush;

        if (d > 30.0) return GreenBrush;
        if (d > 15.0) return OrangeBrush;
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
