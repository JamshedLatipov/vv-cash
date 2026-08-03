using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class CartItemUnitTest
{
    private static Product Tile() => new()
    {
        Id = "p1", Name = "Плитка", Price = 100m,
        UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
        UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
    };

    [Fact]
    public void QuantityInUnitDisplay_DropsTrailingZeros()
    {
        var item = new CartItem { Product = Tile(), Quantity = 53m, QuantityInUnit = 12.720m };

        Assert.Equal("12.72", item.QuantityInUnitDisplay);
    }

    [Fact]
    public void EnteredInUnit_DefaultsToFalse()
    {
        Assert.False(new CartItem { Product = Tile(), Quantity = 1m }.EnteredInUnit);
    }

    [Fact]
    public void NoQuote_LineIsNotDiscounted()
    {
        var item = new CartItem { Product = Tile(), Quantity = 2m };

        Assert.False(item.HasLineDiscount);
        Assert.Equal(0m, item.LineDiscount);
        Assert.Equal(200m, item.LineFinalTotal);
    }

    [Fact]
    public void QuotedDiscount_IsPerUnit_SoItScalesWithQuantity()
    {
        // Stored per unit rather than per line so that between a quantity change and the
        // replacement quote landing the line still reads as a discounted line instead of
        // briefly jumping back to full price.
        var item = new CartItem
        {
            Product = Tile(), Quantity = 2m,
            QuotedUnitPrice = 100m, QuotedUnitDiscount = 30m, QuotedDiscountPercent = 30m,
        };
        Assert.Equal(60m, item.LineDiscount);
        Assert.Equal(140m, item.LineFinalTotal);

        item.Quantity = 3m;

        Assert.Equal(90m, item.LineDiscount);
        Assert.Equal(210m, item.LineFinalTotal);
    }
}
