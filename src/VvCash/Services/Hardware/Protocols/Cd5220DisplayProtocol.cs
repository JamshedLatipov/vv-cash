using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>CD5220 — второй по распространённости диалект после ESC/POS.
///
/// Отличается тем, что строка адресуется своей командой и закрывается CR, а не
/// сорока символами подряд: ESC Q A для верхней, ESC Q B для нижней.</summary>
public sealed class Cd5220DisplayProtocol : IDisplayProtocol
{
    private const byte Cr = 0x0D;

    public string Id => "CD5220";
    public string DisplayName => "CD5220";

    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
        => Encode(line1, line2, codePage);

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => Encode(name, DisplayText.Money(total), codePage);

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => Encode("TOTAL", DisplayText.Money(total), codePage);

    public byte[] BuildClear(EscPosCodePage codePage)
        => Encode(string.Empty, string.Empty, codePage);

    public byte[] BuildProbe(int number)
    {
        var bytes = new List<byte> { 0x1B, 0x51, 0x41 };
        bytes.AddRange(Encoding.ASCII.GetBytes(DisplayText.Pad("PROBE")));
        bytes.Add(Cr);
        bytes.AddRange(new byte[] { 0x1B, 0x51, 0x42 });
        bytes.AddRange(Encoding.ASCII.GetBytes(
            DisplayText.Pad(number.ToString(CultureInfo.InvariantCulture))));
        bytes.Add(Cr);
        return bytes.ToArray();
    }

    /// <summary>Строки добиваются пробелами до 20, хотя CR у части моделей гасит
    /// остаток сам. У другой части не гасит, и тогда на табло остаётся хвост
    /// предыдущего товара — набивка стоит двадцати байт и снимает вопрос.</summary>
    private static byte[] Encode(string line1, string line2, EscPosCodePage codePage)
    {
        var bytes = new List<byte> { 0x1B, 0x51, 0x41 };
        bytes.AddRange(codePage.Encoding.GetBytes(DisplayText.Pad(line1)));
        bytes.Add(Cr);
        bytes.AddRange(new byte[] { 0x1B, 0x51, 0x42 });
        bytes.AddRange(codePage.Encoding.GetBytes(DisplayText.Pad(line2)));
        bytes.Add(Cr);
        return bytes.ToArray();
    }
}
