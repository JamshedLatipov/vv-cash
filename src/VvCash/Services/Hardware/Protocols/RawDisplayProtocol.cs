using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>Голый текст: 40 символов, ни одного управляющего байта.
///
/// Для табло, которые принимают строку как есть. Отсутствие команд здесь — не
/// упрощение, а само содержание протокола: любая добавленная сюда команда сделает его
/// вторым ESCPOS и лишит смысла.</summary>
public sealed class RawDisplayProtocol : IDisplayProtocol
{
    public string Id => "RAW";
    public string DisplayName => "Plain text (no commands)";

    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
        => codePage.Encoding.GetBytes(DisplayText.Pad(line1) + DisplayText.Pad(line2));

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => BuildLine(name, DisplayText.Money(total), codePage);

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => BuildLine("TOTAL", DisplayText.Money(total), codePage);

    public byte[] BuildClear(EscPosCodePage codePage)
        => codePage.Encoding.GetBytes(new string(' ', DisplayText.Columns * 2));

    public byte[] BuildProbe(int number)
        => Encoding.ASCII.GetBytes(
            DisplayText.Pad("PROBE") + DisplayText.Pad(number.ToString(CultureInfo.InvariantCulture)));
}
