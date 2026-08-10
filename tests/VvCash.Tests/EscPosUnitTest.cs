using System.Collections.Generic;
using System.Text;
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
        Encoding.UTF8.GetString(
            EscPosPrinterService.BuildSaleReceipt(items, subtotal: 5300m, discount: 0m, total: 5300m));

    [Fact]
    public void Receipt_ShowsTheUnitAmount_ForAUnitLine()
    {
        // The customer asked for square metres and pays for whole tiles; the
        // receipt has to show both or the rounding looks like a mistake.
        var text = Render(new[] { TileLine() });

        Assert.Contains("12.72 м²", text);
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

        var text = Encoding.UTF8.GetString(EscPosPrinterService.BuildSaleReceipt(
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
}
