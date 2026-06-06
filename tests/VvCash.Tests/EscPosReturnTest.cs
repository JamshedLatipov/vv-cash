using System.Collections.Generic;
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class EscPosReturnTest
{
    [Fact]
    public void DrawerKick_IsStandardPulse()
    {
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, EscPosPrinterService.CmdDrawerKick);
    }

    [Fact]
    public void ReturnReceiptBuffer_ContainsHeaderAndTotal()
    {
        var lines = new List<ReturnReceiptLine> { new("Salad", 2, 200m) };
        var bytes = EscPosPrinterService.BuildReturnReceipt(lines, 200m, "9");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("RETURN", text);
        Assert.Contains("Salad", text);
        Assert.Contains("#9", text);
        Assert.Contains("200", text);
    }
}
