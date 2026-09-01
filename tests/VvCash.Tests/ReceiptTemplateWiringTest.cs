using System.Collections.Generic;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateWiringTest
{
    [Fact]
    public void Printer_ReadsTheTemplateAtPrintTime_NotAtConstruction()
    {
        // Шаблон приезжает синхронизацией в произвольный момент. Читай принтер
        // его в конструкторе — новый шаблон дожидался бы перезапуска кассы или
        // пересборки состава принтеров по SettingsChanged.
        var current = ReceiptTemplate.Default;
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, "127.0.0.1:9100",
            EscPosCodePages.Cp866, PrintRole.Receipt, () => current);

        current = new ReceiptTemplate
        {
            Width = 32,
            Blocks = new List<ReceiptBlock> { new TextBlock { Content = "НОВЫЙ ШАБЛОН" } },
        };

        var text = EscPosCodePages.Cp866.Encoding.GetString(printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m));

        Assert.Contains("НОВЫЙ ШАБЛОН", text);
    }

    [Fact]
    public void Printer_FallsBackToTheDefaultTemplate_WhenNoProviderWasGiven()
    {
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, "127.0.0.1:9100", EscPosCodePages.Cp866);

        var text = EscPosCodePages.Cp866.Encoding.GetString(printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m));

        Assert.Contains("VV CASH POS", text);
    }
}
