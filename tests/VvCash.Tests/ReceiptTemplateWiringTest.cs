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
            EscPosCodePages.Cp866, PrintRole.Receipt, () => (current, ""));

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
    public void Printer_PrintsTheSyncedLogo()
    {
        // Стык принтер→рендерер не был покрыт ничем сквозным: ReceiptLogoTest
        // целиком зовёт ReceiptRenderer.Render напрямую, и две регрессии
        // проходили бы весь набор молча — логотип, вообще не доехавший до
        // рендерера (провайдер отдаёт пустую строку вместо Logo), и логотип,
        // потерянный на самом стыке (BuildConfiguredSaleReceipt распаковал
        // пару, но забыл передать logo дальше в BuildSaleReceipt/Render). Этот
        // тест собирает чек ЧЕРЕЗ принтер — тем же путём, каким его строит
        // боевой код через CompositePrinterService, — и ищет в готовых байтах
        // саму команду печати растра, а не полагается на то, что где-то по
        // дороге не бросилось исключение.
        const string logo = """{"widthBytes":1,"height":1,"raster":"AA=="}""";
        var template = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new LogoBlock { Source = LogoSource.Bitmap } },
        };
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, "127.0.0.1:9100",
            EscPosCodePages.Cp866, PrintRole.Receipt, () => (template, logo));

        var bytes = printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m);

        // GS v 0, m=0, xL=1/xH=0 (widthBytes=1), yL=1/yH=0 (height=1), затем
        // сам растр — один нулевой байт ("AA==" декодируется в byte[]{0}).
        var expectedCommand = new byte[] { 0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };
        Assert.True(ContainsSequence(bytes, expectedCommand),
            "expected the GS v 0 bitmap command in the printed bytes");
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
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
