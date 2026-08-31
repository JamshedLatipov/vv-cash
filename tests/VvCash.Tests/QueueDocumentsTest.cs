using System;
using System.Collections.Generic;
using System.Text;
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

    [Fact]
    public void TicketOmitsWhatItWasNotGiven()
    {
        var bytes = EscPosPrinterService.BuildTicket(EscPosCodePages.Default, "305");

        Assert.Contains("305", Text(bytes));
    }
}
