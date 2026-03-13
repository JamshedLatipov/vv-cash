using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VvCash.Converters;

public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? new SolidColorBrush(Color.Parse("#4caf50")) : new SolidColorBrush(Color.Parse("#f44336"));
        return new SolidColorBrush(Color.Parse("#f44336"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
