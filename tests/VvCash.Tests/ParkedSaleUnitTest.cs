using System.Text.Json;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class ParkedSaleUnitTest
{
    [Fact]
    public void ParkedCartItem_RoundTripsTheEntryModeAndUnitAmount()
    {
        // A line parked in m² must come back in m². Restoring it in pieces
        // would silently change what the cashier sees on resume.
        var original = new ParkedCartItem
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

        var restored = JsonSerializer.Deserialize<ParkedCartItem>(JsonSerializer.Serialize(original))!;

        Assert.Equal(53m, restored.Quantity);
        Assert.Equal(12.72m, restored.QuantityInUnit);
        Assert.True(restored.EnteredInUnit);
        Assert.Equal("u-1", restored.Product.UnitId);
        Assert.Equal(0.24m, restored.Product.UnitFactor);
    }
}
