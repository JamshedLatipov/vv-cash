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
}
