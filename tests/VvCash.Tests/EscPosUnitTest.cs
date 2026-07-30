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
}
