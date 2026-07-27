using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VvCash.Converters;

/// <summary>Masks a PIN string for display, replacing every entered digit with a bullet
/// so the till never shows the actual PIN on screen.</summary>
public class PinMaskConverter : IValueConverter
{
    public static readonly PinMaskConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s) return new string('●', s.Length);
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
