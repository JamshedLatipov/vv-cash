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

        var actual = printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m);

        // Точное равенство байтов с template = null (значит — ReceiptTemplate.Default
        // внутри BuildSaleReceipt), а не Assert.Contains("VV CASH POS", ...): подстрока
        // прошла бы для любого шаблона, начинающегося этой строкой, и не отличила бы
        // дефолт от чужого шаблона с тем же заголовком.
        var expected = EscPosPrinterService.BuildSaleReceipt(EscPosCodePages.Cp866,
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildConfiguredSaleReceipt_PlacesWarehouseAndSellerInTheirOwnFields()
    {
        // У BuildConfiguredSaleReceipt пять подряд string? параметров
        // (discountName, documentNumber, warehouseName, sellerName, saleDate) —
        // любые два соседних молча поменялись бы местами при переносе на другую
        // перегрузку, и компилятор не заметил бы. Оба теста выше этого не
        // увидят: там все строковые поля дефолтны. Этот тест держит склад и
        // продавца различимыми и проверяет, что каждый напечатался в своём
        // собственном поле, а не в чужом.
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, "127.0.0.1:9100", EscPosCodePages.Cp866);

        var text = EscPosCodePages.Cp866.Encoding.GetString(printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m,
            warehouseName: "Склад №1", sellerName: "Иванов"));

        Assert.Contains("Whse: Склад №1", text);
        Assert.Contains("Seller: Иванов", text);
    }
}
