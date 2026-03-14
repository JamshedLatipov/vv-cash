using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VvCash.Converters;

public class StringEqualityToBoolConverter : IValueConverter
{
    public static readonly StringEqualityToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string stringValue && parameter is string targetValue)
        {
            return string.Equals(stringValue, targetValue, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
