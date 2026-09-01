using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class EscPosUnitTest
{
    private static CartItem TileLine() => new()
    {
        Product = new Product
        {
            Id = "p1", Name = "Плитка", Price = 100m,
            UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
            UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
        },
        Quantity = 53m,
        QuantityInUnit = 12.72m,
        EnteredInUnit = true,
    };

    private static string Render(IEnumerable<CartItem> items) =>
        EscPosCodePages.Cp866.Encoding.GetString(
            EscPosPrinterService.BuildSaleReceipt(
                EscPosCodePages.Cp866, items, subtotal: 5300m, discount: 0m, total: 5300m));

    [Fact]
    public void Receipt_ShowsTheUnitAmount_ForAUnitLine()
    {
        // The customer asked for square metres and pays for whole tiles; the
        // receipt has to show both or the rounding looks like a mistake.
        var text = Render(new[] { TileLine() });

        // Надстрочной двойки нет ни в одной однобайтовой таблице ESC/POS, поэтому
        // единица печатается как "м?". Это граница подхода, а не промах с выбором
        // таблицы, — см. «Честная граница» в спеке. Сама цифра, ради которой строка
        // существует, доезжает целой.
        Assert.Contains("12.72 м?", text);
        Assert.Contains("Плитка x53", text);
    }

    [Fact]
    public void Receipt_IsUnchanged_ForAPieceOnlyLine()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 2m };

        var text = Render(new[] { line });

        Assert.DoesNotContain("м²", text);
        Assert.Contains("Товар x2", text);
    }

    // -------------------------------------------------------------------------------
    // Currency and receipt identity. The return and exchange receipts already print
    // neither a currency symbol nor a foreign one, and carry the document number, date
    // and seller. The sale receipt printed "$" and none of the rest.
    // -------------------------------------------------------------------------------

    [Fact]
    public void SaleReceipt_DoesNotPrintAForeignCurrencySymbol()
    {
        // These stores do not take dollars. The symbol was hardcoded and appeared on
        // every line and total of every sale.
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 2m };

        var text = Render(new[] { line });

        Assert.DoesNotContain("$", text);
        Assert.Contains("20.00", text);
    }

    [Fact]
    public void SaleReceipt_CarriesTheDocumentNumberDateSellerAndWarehouse()
    {
        // Same four facts the return receipt prints. Without them a sale receipt cannot
        // be matched to its document when a customer brings it back.
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var text = EscPosCodePages.Cp866.Encoding.GetString(EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            new[] { line }, subtotal: 10m, discount: 0m, total: 10m,
            discountName: null,
            documentNumber: "SL-42", warehouseName: "Склад 1", sellerName: "Анна", saleDate: "10.08.2026 14:05"));

        Assert.Contains("SL-42", text);
        Assert.Contains("Склад 1", text);
        Assert.Contains("Анна", text);
        Assert.Contains("10.08.2026 14:05", text);
    }

    [Fact]
    public void SaleReceipt_OmitsTheHeaderLinesItWasNotGiven()
    {
        // An offline sale has no document number yet, and a register with seller
        // switching off has no seller to name. Neither may print an empty label.
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var text = Render(new[] { line });

        Assert.DoesNotContain("Doc #", text);
        Assert.DoesNotContain("Seller:", text);
        Assert.DoesNotContain("Whse:", text);
    }

    // -------------------------------------------------------------------------------
    // Кодовая страница. Сегодня чек уходит в UTF-8 без ESC t n вообще, то есть
    // кириллица печатается мусором. Тесты ниже проверяют обе половины: какими
    // байтами кодируем и каким номером объявляем таблицу принтеру.
    // -------------------------------------------------------------------------------

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }

    [Fact]
    public void SaleReceipt_SelectsTheCodePageRightAfterInit()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866, new[] { line }, subtotal: 10m, discount: 0m, total: 10m);

        // ESC @ первым, FS . за ним, ESC t n третьим: таблица должна быть выбрана
        // до первой буквы, иначе шапка уходит в дефолтную, — а выбор таблицы
        // должен идти после выхода из китайского режима, иначе принтер его не
        // рассматривает и печатает иероглифами независимо от селектора.
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17 }, bytes[..7]);
    }

    [Fact]
    public void PreReceipt_SelectsTheCodePage()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var bytes = EscPosPrinterService.BuildPreReceipt(EscPosCodePages.Cp866, new[] { line }, total: 10m);

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17 }, bytes[..7]);
    }

    [Fact]
    public void PreReceipt_CarriesItsLinesAndTotal()
    {
        // До извлечения BuildPreReceipt разметка пречека доставалась только через
        // сокет, поэтому не проверялась вовсе. PrintPreReceiptAsync сегодня не
        // вызывается нигде в приложении — этот тест страхует готовый билдер, а не
        // тот, что уже в деле.
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 2m };

        var text = EscPosCodePages.Cp866.Encoding.GetString(
            EscPosPrinterService.BuildPreReceipt(EscPosCodePages.Cp866, new[] { line }, total: 20m));

        Assert.Contains("PRE-RECEIPT", text);
        Assert.Contains("Товар x2", text);
        Assert.Contains("20.00", text);
    }

    [Fact]
    public void ReturnReceipt_SelectsTheCodePage()
    {
        var bytes = EscPosPrinterService.BuildReturnReceipt(
            EscPosCodePages.Cp866, new[] { new ReturnReceiptLine("Товар", 1, 10m) },
            totalRefund: 10m, documentNumber: "RT-1");

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17 }, bytes[..7]);
    }

    [Fact]
    public void ExchangeReceipt_SelectsTheCodePage()
    {
        var bytes = EscPosPrinterService.BuildExchangeReceipt(
            EscPosCodePages.Cp866,
            new[] { new ReturnReceiptLine("Товар", 1, 10m) },
            new[] { new ReturnReceiptLine("Другой", 1, 12m) },
            difference: 2m, documentNumber: "EX-1");

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17 }, bytes[..7]);
    }

    [Fact]
    public void TestReceipt_SelectsTheCodePage_AndNamesIt()
    {
        // Пробный чек печатает выбранную таблицу и её селектор, чтобы точка могла
        // сказать, что именно пробовала, не глядя в настройки.
        var bytes = EscPosPrinterService.BuildTestReceipt(EscPosCodePages.Cp1251);

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 46 }, bytes[..7]);

        var text = EscPosCodePages.Cp1251.Encoding.GetString(bytes);
        Assert.Contains("CP1251", text);
        Assert.Contains("ESC t 46", text);
    }

    [Fact]
    public void TestReceipt_CarriesRussianTajikKazakhLatinAndDigits()
    {
        // Без второй строки русский образец печатается безупречно, кассир
        // отвечает «кириллица видна», и граница обнаруживается позже — на
        // названиях товаров в бою.
        var text = EscPosCodePages.Cp866.Encoding.GetString(
            EscPosPrinterService.BuildTestReceipt(EscPosCodePages.Cp866));

        Assert.Contains("Ёжик", text);
        Assert.Contains("The quick brown fox", text);
        Assert.Contains("0123456789", text);
        // Ни одной из десяти таджикских и казахских букв в CP866 нет —
        // строка целиком вырождается в вопросительные знаки, и предъявляется
        // это ровно там, где на неё смотрят. Утверждение точное, а не
        // Contains("?"): последнее прошло бы и на одной случайной замене,
        // то есть не отличило бы эту границу от опечатки в другом месте.
        Assert.Contains("TJ/KK: ? ? ? ? ? ? ? ? ? ?", text);
    }

    [Fact]
    public void Receipt_IsEncodedInTheChosenCodePage_NotUtf8()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866, new[] { line }, subtotal: 10m, discount: 0m, total: 10m);

        Assert.True(Contains(bytes, EscPosCodePages.Cp866.Encoding.GetBytes("Товар")));
        Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("Товар")));
    }

    [Fact]
    public async Task UnknownConnectionType_ReportsFailureRatherThanSilentSuccess()
    {
        // Число вне диапазона попадает в settings.json от правки руками или
        // неудачной миграции: System.Text.Json диапазон enum не проверяет.
        var printer = new EscPosPrinterService(
            (PrinterConnectionType)99, "whatever", EscPosCodePages.Default);

        Assert.False(await printer.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m));
    }

    [Fact]
    public async Task UsbPrinting_OnAQueueThatDoesNotExist_ReportsFailure()
    {
        // Находка #4 и причина всего батча: SendViaUsb писал в Console и отдавал
        // завершённую задачу, поэтому касса рапортовала о напечатанном чеке над
        // принтером, который ничего не напечатал. Интероп с winspool непокрываем,
        // но сам исход — покрываем: OpenPrinter не найдёт несуществующую очередь,
        // Failure() бросит, и существующий catch превратит это в false.
        //
        // На не-Windows тот же assert проходит другим путём: SendViaUsb бросает
        // PlatformNotSupportedException раньше, чем доходит до OpenPrinter, и её
        // гасит тот же catch — тест осмысленен только под Windows, но нигде не
        // ломается.
        var printer = new EscPosPrinterService(
            PrinterConnectionType.USB, "VvCash queue that does not exist", EscPosCodePages.Default);

        Assert.False(await printer.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m));
        Assert.Equal(PrinterStatus.Error, printer.Status);
    }
}
