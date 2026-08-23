using System.Globalization;

namespace VvCash.Models;

/// <summary>Количество без хвостовых нулей, одинаково на экране и на чеке.
///
/// Одним местом, а не тремя: у CartItem уже было две копии этой логики, и третья
/// на чеке — прямой путь к расхождению между тем, что кассир видит в корзине, и
/// тем, что печатается покупателю.</summary>
public static class QuantityFormat
{
    /// <summary>Инвариантный разделитель намеренно: тот же чек печатался 20.00 на
    /// одной кассе и 20,00 на соседней, пока формат брался из локали ОС.</summary>
    public static string Display(decimal value, string fractionFormat)
        => value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(fractionFormat, CultureInfo.InvariantCulture);
}
