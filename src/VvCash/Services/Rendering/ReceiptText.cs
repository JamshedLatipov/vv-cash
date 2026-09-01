using System;
using System.Globalization;

namespace VvCash.Services.Rendering;

/// <summary>Формирование строк чека. Public, а не internal: тот же расчёт
/// колонок повторяет превью в бэкофисе, и здесь он единственный источник
/// правды на стороне кассы.</summary>
public static class ReceiptText
{
    /// <summary>Amounts on a receipt, formatted the same way on every register.
    /// Interpolating with ":F2" took the decimal separator from the operating
    /// system's locale, so the same sale printed 20.00 on one till and 20,00 on the
    /// next — and CartItem.QuantityDisplay, right beside it on the line, has always
    /// used the invariant form.</summary>
    public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    public static string PadLine(string left, string right, int width)
    {
        var spaces = width - left.Length - right.Length;
        return left + new string(' ', Math.Max(1, spaces)) + right;
    }

    /// <summary>Clips a label to the paper width. A promotion name is free text and
    /// a long one would wrap into a ragged second line on a 32-column roll.</summary>
    public static string Truncate(string s, int width)
        => s.Length <= width ? s : s.Substring(0, width);
}
