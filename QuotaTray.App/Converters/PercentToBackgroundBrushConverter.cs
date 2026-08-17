using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace QuotaTray.App.Converters;

public class PercentToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = new(MediaColor.FromArgb(45, 16, 185, 129));   // > 30%
    private static readonly SolidColorBrush OrangeBg = new(MediaColor.FromArgb(45, 245, 158, 11));  // 15% ~ 30%
    private static readonly SolidColorBrush RedBg = new(MediaColor.FromArgb(45, 239, 68, 68));      // <= 15%
    private static readonly SolidColorBrush MutedBg = new(MediaColor.FromArgb(30, 161, 161, 170));

    static PercentToBackgroundBrushConverter()
    {
        GreenBg.Freeze();
        OrangeBg.Freeze();
        RedBg.Freeze();
        MutedBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double d = 0;
        if (value is double dv) d = dv;
        else if (value is int iv) d = iv;
        else if (value is float fv) d = fv;
        else return MutedBg;

        if (d > 30.0) return GreenBg;
        if (d > 15.0) return OrangeBg;
        return RedBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
