using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VvCash.Converters;

/// <summary>Masks a PIN string for display, replacing every entered digit with a filled
/// bullet so the till never shows the actual PIN on screen. If <paramref name="parameter"/>
/// (ConverterParameter) parses as an int larger than the entered length, the remaining
/// slots up to that count are padded with a hollow placeholder bullet, cueing how many
/// digits are still expected. Without a parameter (or one smaller than what's already
/// entered), only the entered digits are rendered.</summary>
public class PinMaskConverter : IValueConverter
{
    private const char Filled = '●';
    private const char Placeholder = '○';

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var entered = value as string ?? string.Empty;

        var total = entered.Length;
        if (parameter is string p && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) && expected > total)
            total = expected;

        var chars = new char[total];
        for (var i = 0; i < total; i++)
            chars[i] = i < entered.Length ? Filled : Placeholder;

        return new string(chars);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
