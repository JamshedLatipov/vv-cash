using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Раскладка талона и бегунка. Latin1 для разбора байтов: ASCII в любой
/// однобайтовой таблице ESC/POS совпадает сам с собой, а проверяются здесь только
/// цифры и латиница.</summary>
public class QueueDocumentsTest
{
    private static string Text(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static List<CartItem> OneCoffee() => new()
    {
        new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m }
    };

    [Fact]
    public void KitchenOrderCarriesTheNumber()
    {
        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, OneCoffee(), 24m, 0m, 24m, queueNumber: "305");

        Assert.Contains("# 305", Text(bytes));
    }

    [Fact]
    public void CustomerReceiptCarriesNoNumber()
    {
        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, OneCoffee(), 24m, 0m, 24m);

        Assert.DoesNotContain("#", Text(bytes));
    }

    [Fact]
    public void TicketCarriesNumberTimeAndStore()
    {
        var bytes = EscPosPrinterService.BuildTicket(
            EscPosCodePages.Default, "305", "14:22", "Market 1");

        var text = Text(bytes);
        Assert.Contains("305", text);
        Assert.Contains("14:22", text);
        Assert.Contains("Market 1", text);
    }

    /// <summary>Сравнение с полным талоном, а не одна проверка на номер: талон без
    /// склада и времени обязан не нести пустых строк, а тест, который ищет только
    /// номер, зелен и без обоих охранных условий.</summary>
    [Fact]
    public void TicketOmitsWhatItWasNotGiven()
    {
        var bare = EscPosPrinterService.BuildTicket(EscPosCodePages.Default, "305");
        var full = EscPosPrinterService.BuildTicket(
            EscPosCodePages.Default, "305", "14:22", "Market 1");

        var text = Text(bare);
        Assert.Contains("305", text);
        Assert.DoesNotContain("14:22", text);
        Assert.DoesNotContain("Market 1", text);
        Assert.True(bare.Length < full.Length);
    }

    /// <summary>Проверяет все девять полей записи, а не выборку — иначе имя теста
    /// обещает больше, чем он держит. Item, Subtotal и Total намеренно не совпадают
    /// друг с другом и со строкой одной позиции (10.00), чтобы "24.00" в тексте
    /// нельзя было списать на что-то другое, кроме Subtotal.</summary>
    [Fact]
    public void SaleReceiptDataCarriesEverythingTheReceiptPrints()
    {
        var items = new List<CartItem>
        {
            new CartItem { Product = new Product { Name = "Espresso", Price = 10m }, Quantity = 1m }
        };
        var data = new SaleReceiptData(items, Subtotal: 24m, Discount: 4m, Total: 20m,
            DiscountName: "Happy hour", DocumentNumber: "A-7",
            WarehouseName: "Market 1", SellerName: "Ann", SaleDate: "2026-08-31 14:22");

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, data.Items, data.Subtotal, data.Discount, data.Total,
            data.DiscountName, data.DocumentNumber, data.WarehouseName, data.SellerName,
            data.SaleDate, queueNumber: "305");

        var text = Text(bytes);
        Assert.Contains("# 305", text);
        Assert.Contains("Espresso", text);         // Items
        Assert.Contains("24.00", text);            // Subtotal
        Assert.Contains("-4.00", text);            // Discount
        Assert.Contains("20.00", text);            // Total
        Assert.Contains("Happy hour", text);       // DiscountName
        Assert.Contains("A-7", text);              // DocumentNumber
        Assert.Contains("Market 1", text);         // WarehouseName
        Assert.Contains("Ann", text);              // SellerName
        Assert.Contains("2026-08-31 14:22", text); // SaleDate
    }

    /// <summary>ConnectionType вне диапазона enum уводит SendAsync в свою ветку
    /// default: и бросает, не тронув транспорт ни на байт. Тот же приём, что в
    /// CompositePrinterServiceTest: тесту нужно «не делает ввода-вывода».</summary>
    [Fact]
    public async Task TicketAndKitchenOrderReportFailureRatherThanThrowing()
    {
        var printer = new EscPosPrinterService(
            (PrinterConnectionType)99, "nowhere", EscPosCodePages.Default);

        Assert.False(await printer.PrintTicketAsync("305", "14:22", "Market 1"));
        Assert.False(await printer.PrintKitchenOrderAsync(
            new SaleReceiptData(OneCoffee(), 24m, 0m, 24m), "305"));
    }
}
