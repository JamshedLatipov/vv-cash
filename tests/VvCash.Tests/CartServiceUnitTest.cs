using System;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CartServiceUnitTest
{
    private static Product Tile(bool sellInUnit = true, bool divisible = false) => new()
    {
        Id = "p1", Name = "Плитка", Price = 100m,
        UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
        UnitFactor = 0.24m, IsDivisible = divisible, SellInSecondaryUnit = sellInUnit,
    };

    private static CartService NewCart() => new(new StubPromotionProvider(Array.Empty<Promotion>()));

    [Fact]
    public void SetQuantityInUnit_ConvertsToPiecesAndKeepsTheUnitAmount()
    {
        var c = NewCart();
        c.AddProduct(Tile());

        c.SetQuantityInUnit(c.Items[0], 12.5m);

        Assert.Equal(53m, c.Items[0].Quantity);
        Assert.Equal(12.72m, c.Items[0].QuantityInUnit);
        // Money is always pieces × price: 53 tiles at 100.
        Assert.Equal(5300m, c.Items[0].LineTotal);
    }

    [Fact]
    public void SetQuantityInUnit_RemovesTheLineAtZero()
    {
        var c = NewCart();
        c.AddProduct(Tile());

        c.SetQuantityInUnit(c.Items[0], 0m);

        Assert.Empty(c.Items);
    }

    [Fact]
    public void SetQuantityInUnit_IsIgnored_ForAPieceOnlyProduct()
    {
        // Nothing to convert with, and silently inventing a factor would bill
        // the customer for a quantity nobody entered.
        var c = NewCart();
        c.AddProduct(new Product { Id = "p2", Name = "Товар", Price = 10m });

        c.SetQuantityInUnit(c.Items[0], 12.5m);

        Assert.Equal(1m, c.Items[0].Quantity);
        Assert.Equal(0m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void AddProduct_TakesTheEntryModeFromTheProductCard()
    {
        var c = NewCart();

        c.AddProduct(Tile(sellInUnit: true));

        Assert.True(c.Items[0].EnteredInUnit);
        // One piece, not one m2: a tap adds a piece and the pad refines it.
        Assert.Equal(1m, c.Items[0].Quantity);
        Assert.Equal(0.24m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void AddProduct_StaysInPieces_WhenTheCardSaysSo()
    {
        var c = NewCart();

        c.AddProduct(Tile(sellInUnit: false));

        Assert.False(c.Items[0].EnteredInUnit);
    }

    [Fact]
    public void IncreaseQuantity_StepsByOnePiece_EvenInUnitMode()
    {
        // "+" on a tile adds a tile, not a square metre.
        var c = NewCart();
        c.AddProduct(Tile());
        c.SetQuantityInUnit(c.Items[0], 12.5m);

        c.IncreaseQuantity(c.Items[0]);

        Assert.Equal(54m, c.Items[0].Quantity);
        Assert.Equal(12.96m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void DecreaseQuantity_RecomputesTheUnitAmount()
    {
        var c = NewCart();
        c.AddProduct(Tile());
        c.SetQuantityInUnit(c.Items[0], 12.5m);

        c.DecreaseQuantity(c.Items[0]);

        Assert.Equal(52m, c.Items[0].Quantity);
        Assert.Equal(12.48m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void SetQuantity_InPieces_RecomputesTheUnitAmount()
    {
        var c = NewCart();
        c.AddProduct(Tile());

        c.SetQuantity(c.Items[0], 10m);

        Assert.Equal(10m, c.Items[0].Quantity);
        Assert.Equal(2.4m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void AddProduct_Twice_KeepsTheUnitAmountInStep()
    {
        var c = NewCart();
        var tile = Tile();

        c.AddProduct(tile);
        c.AddProduct(tile);

        Assert.Equal(2m, c.Items[0].Quantity);
        Assert.Equal(0.48m, c.Items[0].QuantityInUnit);
    }
}
