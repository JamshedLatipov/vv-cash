using VvCash.Models;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

// The pad's whole job is showing the cashier what a typed amount becomes before
// it is committed — above all the round-up on indivisible goods, which changes
// what the customer pays.
public class QuantityPadTest
{
    private static Product Tile(bool divisible = false) => new()
    {
        Id = "p1", Name = "Плитка", Price = 100m,
        UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
        UnitFactor = 0.24m, IsDivisible = divisible, SellInSecondaryUnit = true,
    };

    private static QuantityPadViewModel PadFor(Product p, bool inUnit = true) =>
        new(new CartItem { Product = p, Quantity = 1m, EnteredInUnit = inUnit });

    [Fact]
    public void Preview_ShowsThePieceCountAndTheRoundedUnitAmount()
    {
        var pad = PadFor(Tile());

        pad.Input = "12.5";

        Assert.Equal(53m, pad.PreviewQuantity);
        Assert.Equal(12.72m, pad.PreviewQuantityInUnit);
        Assert.Equal(5300m, pad.PreviewTotal);
        Assert.True(pad.IsRounded);
    }

    [Fact]
    public void Preview_DoesNotFlagRounding_OnAnExactMultiple()
    {
        var pad = PadFor(Tile());

        pad.Input = "12";

        Assert.Equal(50m, pad.PreviewQuantity);
        Assert.False(pad.IsRounded);
    }

    [Fact]
    public void Preview_KeepsTheTypedAmount_ForADivisibleProduct()
    {
        var pad = PadFor(Tile(divisible: true));

        pad.Input = "12.5";

        Assert.Equal(12.5m, pad.PreviewQuantityInUnit);
        Assert.False(pad.IsRounded);
    }

    [Fact]
    public void Preview_InPieceMode_ReportsTheUnitAmount()
    {
        var pad = PadFor(Tile(), inUnit: false);

        pad.Input = "10";

        Assert.Equal(10m, pad.PreviewQuantity);
        Assert.Equal(2.4m, pad.PreviewQuantityInUnit);
    }

    [Fact]
    public void PieceMode_RejectsAFractionalCount_ForAnIndivisibleProduct()
    {
        var pad = PadFor(Tile(), inUnit: false);

        pad.Input = "10.5";

        Assert.False(pad.IsValid);
    }

    [Fact]
    public void PriceInSelectedUnit_FollowsTheSelectedUnit()
    {
        var pad = PadFor(Tile());

        Assert.Equal(416.67m, decimal.Round(pad.PriceInSelectedUnit, 2));
        Assert.Equal("м²", pad.UnitLabel);

        pad.Mode = PadMode.Pieces;

        Assert.Equal(100m, pad.PriceInSelectedUnit);
        Assert.Equal("шт", pad.UnitLabel);
    }

    [Fact]
    public void PriceInSelectedUnit_FollowsTheServerQuote_NotTheCachedPrice()
    {
        // Once a quote prices the line, that is what the customer is charged;
        // the pad must not quietly show the stale catalogue figure.
        var item = new CartItem { Product = Tile(), Quantity = 1m, EnteredInUnit = false, QuotedUnitPrice = 80m };
        var pad = new QuantityPadViewModel(item);

        Assert.Equal(80m, pad.PriceInSelectedUnit);

        pad.Input = "10";
        Assert.Equal(800m, pad.PreviewTotal);
    }

    [Fact]
    public void EmptyOrGarbageInput_IsNotCommittable()
    {
        var pad = PadFor(Tile());

        pad.Input = "";
        Assert.False(pad.IsValid);

        pad.Input = "abc";
        Assert.False(pad.IsValid);

        pad.Input = "0";
        Assert.False(pad.IsValid);
    }

    [Fact]
    public void PieceOnlyProduct_HasNoUnitToggle()
    {
        var pad = PadFor(new Product { Id = "p2", Name = "Товар", Price = 10m }, inUnit: false);

        Assert.False(pad.CanSwitchUnit);
        Assert.Equal("шт", pad.UnitLabel);
    }

    [Fact]
    public void OpensInTheUnitTheLineWasEnteredIn()
    {
        Assert.Equal(PadMode.Unit, PadFor(Tile(), inUnit: true).Mode);
        Assert.Equal(PadMode.Pieces, PadFor(Tile(), inUnit: false).Mode);

        // EnteredInUnit on a piece-only line is stale data, not a unit to open in.
        var pieceOnly = new Product { Id = "p2", Name = "Товар", Price = 10m };
        Assert.Equal(PadMode.Pieces, PadFor(pieceOnly, inUnit: true).Mode);
    }

    [Fact]
    public void ModeBools_MirrorTheMode_AndOnlyATrueWriteMovesIt()
    {
        var pad = PadFor(Tile(), inUnit: false);

        Assert.True(pad.IsPiecesMode);
        Assert.False(pad.IsUnitMode);

        pad.IsUnitMode = true;
        Assert.Equal(PadMode.Unit, pad.Mode);

        // A RadioButton writes false to the segment it leaves; that must not
        // knock the pad out of the mode it just entered.
        pad.IsPiecesMode = false;
        Assert.Equal(PadMode.Unit, pad.Mode);
    }
}
