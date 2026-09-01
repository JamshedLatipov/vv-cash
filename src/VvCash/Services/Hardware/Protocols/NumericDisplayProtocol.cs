using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>Сегментное LED-табло: 6–8 разрядов, только цифры.
///
/// Букв такая панель не умеет физически, поэтому название товара здесь отбрасывается,
/// и на табло всегда виден итог по чеку. Решение живёт в протоколе, а не в
/// PosViewModel: тот шлёт один и тот же кадр любому табло и о разнице между ними не
/// знает.
///
/// Команд не шлёт вовсе — эти панели принимают цифры и CR.</summary>
public sealed class NumericDisplayProtocol : IDisplayProtocol
{
    private const string Empty = "0.00";

    public string Id => "NUMERIC";
    public string DisplayName => "LED / 7-segment";

    /// <summary>Из текста берутся цифры и точка, всё прочее выбрасывается. Нижняя
    /// строка вперёд верхней: у всех вызовов сумма стоит именно в ней.</summary>
    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
    {
        var digits = DigitsOf(line2);
        if (digits.Length == 0) digits = DigitsOf(line1);
        return Ascii(digits.Length == 0 ? Empty : digits);
    }

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => Ascii(DisplayText.Money(total));

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => Ascii(DisplayText.Money(total));

    public byte[] BuildClear(EscPosCodePage codePage) => Ascii(Empty);

    public byte[] BuildProbe(int number)
        => Ascii(number.ToString(CultureInfo.InvariantCulture));

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value + "\r");

    private static string DigitsOf(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsDigit(c) || c == '.') sb.Append(c);
        }
        return sb.ToString();
    }
}
