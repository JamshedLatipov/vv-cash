using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>Epson ESC/POS — то, что касса шлёт с самого начала и что работает на
/// табло в точках.
///
/// Реализация сознательно консервативная. Инициализацию (ESC @) и выбор кодовой
/// страницы (ESC t n) понимают практически все VFD; команды позиционирования курсора
/// у моделей расходятся сильнее, чем у принтеров, поэтому их здесь нет — 40 символов
/// двумя строками по 20, и модель раскладывает их сама.</summary>
public sealed class EscPosDisplayProtocol : IDisplayProtocol
{
    public string Id => "ESCPOS";
    public string DisplayName => "ESC/POS (Epson)";

    /// <summary>Кадр строкой, отдельно от кодирования: разметку можно проверить, не
    /// думая про байты и не открывая порт.</summary>
    public static string BuildFrame(string line1, string line2)
        => DisplayText.Pad(line1) + DisplayText.Pad(line2);

    public static string BuildItemFrame(string name, decimal total)
        => BuildFrame(name, DisplayText.Money(total));

    public static string BuildTotalFrame(decimal total)
        => BuildFrame("TOTAL", DisplayText.Money(total));

    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
        => Encode(BuildFrame(line1, line2), codePage);

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => Encode(BuildItemFrame(name, total), codePage);

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => Encode(BuildTotalFrame(total), codePage);

    public byte[] BuildClear(EscPosCodePage codePage)
        => Encode(new string(' ', DisplayText.Columns * 2), codePage);

    public byte[] BuildProbe(int number)
    {
        // Только ESC @ и ASCII: ни ESC t, ни байтов старше 0x7F — см. BuildProbe
        // в IDisplayProtocol.
        var body = Encoding.ASCII.GetBytes(
            BuildFrame("PROBE", number.ToString(CultureInfo.InvariantCulture)));

        var bytes = new List<byte> { 0x1B, 0x40 };
        bytes.AddRange(body);
        return bytes.ToArray();
    }

    /// <summary>ESC @, затем ESC t n, затем текст. Без инициализации дисплей копит
    /// мусор от предыдущей строки; без кодовой страницы кириллица уходит в ASCII и
    /// превращается в вопросительные знаки.</summary>
    private static byte[] Encode(string text, EscPosCodePage codePage)
    {
        var bytes = new List<byte> { 0x1B, 0x40, 0x1B, 0x74, codePage.EscTSelector };
        bytes.AddRange(codePage.Encoding.GetBytes(text));
        return bytes.ToArray();
    }
}
