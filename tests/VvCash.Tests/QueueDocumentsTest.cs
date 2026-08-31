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

    [Fact]
    public void SaleReceiptDataCarriesEverythingTheReceiptPrints()
    {
        var data = new SaleReceiptData(OneCoffee(), 24m, 4m, 20m,
            DiscountName: "Happy hour", DocumentNumber: "A-7",
            WarehouseName: "Market 1", SellerName: "Ann", SaleDate: "2026-08-31 14:22");

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, data.Items, data.Subtotal, data.Discount, data.Total,
            data.DiscountName, data.DocumentNumber, data.WarehouseName, data.SellerName,
            data.SaleDate, queueNumber: "305");

        var text = Text(bytes);
        Assert.Contains("# 305", text);
        Assert.Contains("A-7", text);
        Assert.Contains("Ann", text);
        Assert.Contains("Happy hour", text);
    }
}
