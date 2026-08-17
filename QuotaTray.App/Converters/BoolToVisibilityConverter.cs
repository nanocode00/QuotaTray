using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuotaTray.App.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isVisible = false;
        if (value is bool b)
        {
            isVisible = b;
        }
        else if (value is string s)
        {
            isVisible = !string.IsNullOrWhiteSpace(s);
        }
        else if (value is int i)
        {
            isVisible = i > 0;
        }
        else if (value != null)
        {
            isVisible = true;
        }

        if (Invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            bool b = v == Visibility.Visible;
            return Invert ? !b : b;
        }
        return false;
    }
}
