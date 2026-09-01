using System.Globalization;

namespace VvCash.Services.Hardware;

/// <summary>Набивка колонок и формат суммы — то общее, что было приватным внутри
/// VfdDisplayService и понадобилось трём текстовым протоколам сразу.
///
/// Ширина колонки и «доллара нет» переехали сюда вместе: символ валюты был зашит в
/// "$" на кассах, которые долларов не берут, и правился один раз здесь же.</summary>
internal static class DisplayText
{
    public const int Columns = 20;

    public static string Pad(string text)
        => text.Length >= Columns ? text[..Columns] : text.PadRight(Columns);

    public static string Money(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);
}
