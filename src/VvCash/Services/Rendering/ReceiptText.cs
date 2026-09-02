using System;
using System.Globalization;

namespace VvCash.Services.Rendering;

/// <summary>Формирование строк чека — здесь единственный источник правды на
/// стороне кассы, и его TS-двойник в бэкофисе обязан считать так же.
///
/// Ширину левой колонки строки позиции формирует не только <see cref="PadLine"/>:
/// само название собирается через QuantityFormat.Display (см.
/// EscPosPrinterService) с форматом "0.###", у которого нет прямого аналога в
/// JS. Зеркалить в TS нужно обе части — и PadLine/Truncate, и этот формат
/// количества, — иначе ширины колонок разъедутся.
///
/// PadLine и Truncate считают длину в code units UTF-16 (<c>string.Length</c>) —
/// совпадение с JS <c>.length</c> точное, включая суррогатные пары: обе стороны
/// считают одни и те же code units и режут пару одинаково. Держится это на том,
/// что все таблицы кассы (Cp866, Cp1251, Pc437) однобайтовые, поэтому code unit
/// равен и байту, и колонке на ленте. Не переводить ни одну из сторон на
/// кодовые точки или графемные кластеры — это сломает и паритет с TS, и счёт
/// колонок на бумаге.</summary>
public static class ReceiptText
{
    /// <summary>Amounts on a receipt, formatted the same way on every register.
    /// Interpolating with ":F2" took the decimal separator from the operating
    /// system's locale, so the same sale printed 20.00 on one till and 20,00 on the
    /// next — and CartItem.QuantityDisplay, right beside it on the line, has always
    /// used the invariant form.
    ///
    /// Rounds away from zero — decimal.ToString("F2") semantics, not banker's
    /// rounding: Math.Round(2.005m, 2) gives 2.00m, this gives "2.01". JavaScript's
    /// toFixed(2) rounds by the binary float representation and disagrees with
    /// both (toFixed on 2.675 gives "2.67"). The TS twin of this receipt must
    /// replicate away-from-zero rounding explicitly — toFixed is not a
    /// substitute, and reaching for it will print a different total than the
    /// till for an ordinary sale of a fractional-quantity item.</summary>
    public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Pads two columns to width with at least one separating space
    /// between them. When left.Length + right.Length == width there is no room
    /// left for that space, so the result is deliberately width + 1 characters —
    /// the printer wraps the extra character onto its own line. A name stuck
    /// directly against its price is less readable than that wrap, so this is
    /// the chosen trade-off, not an oversight: a TS twin reaching for
    /// padEnd(width) would not reproduce it.
    ///
    /// Precondition: width > 0. Clamping a width that arrives as a number in a
    /// JSON template is the caller's job, done once at template-parse time —
    /// not this method's; it does not validate or clamp.</summary>
    public static string PadLine(string left, string right, int width)
    {
        var spaces = width - left.Length - right.Length;
        return left + new string(' ', Math.Max(1, spaces)) + right;
    }

    /// <summary>Clips a label to the paper width. A promotion name is free text and
    /// a long one would wrap into a ragged second line on a 32-column roll.
    ///
    /// Precondition: width > 0, same as <see cref="PadLine"/> — validated at
    /// template-parse time, not here.</summary>
    public static string Truncate(string s, int width)
        => s.Length <= width ? s : s.Substring(0, width);
}
