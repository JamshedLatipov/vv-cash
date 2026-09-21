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

    // Nuggets sold by the pack (0.5 kg, 15 each) with kg as the secondary
    // unit, so the price per kg is 30 — the figure the customer is quoted.
    private static Product Nuggets() => new()
    {
        Id = "p3", Name = "Наггетсы", Price = 15m,
        UnitId = "u-kg", UnitCode = "kg", UnitShortName = "кг",
        UnitFactor = 0.5m, IsDivisible = true, SellInSecondaryUnit = true,
    };

    // Weighed goods with no secondary unit: the piece *is* the kilogram.
    private static Product LooseCandy() => new()
    {
        Id = "p4", Name = "Конфеты", Price = 30m, IsDivisible = true,
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
        // knock the pad out of the mode it just entered. Nor may a false on
        // the active segment — nothing writes it, but the setter's whole job
        // is that false never moves the mode.
        pad.IsPiecesMode = false;
        pad.IsUnitMode = false;
        Assert.Equal(PadMode.Unit, pad.Mode);

        pad.IsPiecesMode = true;
        Assert.Equal(PadMode.Pieces, pad.Mode);

        // Bindings are reflective: the segments only follow a programmatic
        // Mode change if the notification actually fires.
        Assert.PropertyChanged(pad, nameof(pad.IsUnitMode), () => pad.Mode = PadMode.Unit);
        Assert.PropertyChanged(pad, nameof(pad.IsPiecesMode), () => pad.Mode = PadMode.Pieces);
        Assert.PropertyChanged(pad, nameof(pad.IsMoneyMode), () => pad.Mode = PadMode.Money);

        pad.Mode = PadMode.Pieces;
        Assert.PropertyChanged(pad, nameof(pad.PriceUnitLabel), () => pad.Mode = PadMode.Money);
    }

    [Fact]
    public void MoneyMode_IsOfferedOnlyForADivisibleProductWithAPrice()
    {
        Assert.True(PadFor(Nuggets()).CanEnterMoney);
        Assert.True(PadFor(LooseCandy(), inUnit: false).CanEnterMoney);

        // 50 / 3 = 16.67 pieces does not exist.
        Assert.False(PadFor(Tile(divisible: false)).CanEnterMoney);

        // Nothing to divide by.
        var free = LooseCandy();
        free.Price = 0m;
        Assert.False(PadFor(free, inUnit: false).CanEnterMoney);

        // A server quote is what the line is priced at; a free catalogue
        // price with a live quote is still something to divide by.
        var quoted = new CartItem { Product = free, Quantity = 1m, EnteredInUnit = false, QuotedUnitPrice = 30m };
        Assert.True(new QuantityPadViewModel(quoted).CanEnterMoney);
    }

    [Fact]
    public void ModeSelector_IsShownWhenAnySegmentBesidesPiecesExists()
    {
        Assert.True(PadFor(Tile()).HasModeSelector);                      // unit only
        Assert.True(PadFor(LooseCandy(), inUnit: false).HasModeSelector); // money only

        var pieceOnly = new Product { Id = "p2", Name = "Товар", Price = 10m };
        Assert.False(PadFor(pieceOnly, inUnit: false).HasModeSelector);
    }

    [Fact]
    public void MoneyMode_LabelsTheInputAsMoney_AndTheHeaderInTheDerivedUnit()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        Assert.Equal("сум", pad.UnitLabel);
        Assert.Equal("кг", pad.PriceUnitLabel);
        Assert.Equal(30m, pad.PriceInSelectedUnit);

        var candy = PadFor(LooseCandy(), inUnit: false);
        candy.Mode = PadMode.Money;

        Assert.Equal("сум", candy.UnitLabel);
        Assert.Equal("шт", candy.PriceUnitLabel);
        Assert.Equal(30m, candy.PriceInSelectedUnit);

        // Entered in pieces or not, "50 somoni of nuggets" is weighed in kilograms.
        var fromPieces = PadFor(Nuggets(), inUnit: false);
        fromPieces.Mode = PadMode.Money;
        Assert.Equal("кг", fromPieces.PriceUnitLabel);
        Assert.Equal(30m, fromPieces.PriceInSelectedUnit);
    }

    [Fact]
    public void PriceUnitLabel_MatchesUnitLabel_OutsideMoneyMode()
    {
        var pad = PadFor(Tile());
        Assert.Equal(pad.UnitLabel, pad.PriceUnitLabel);

        pad.Mode = PadMode.Pieces;
        Assert.Equal(pad.UnitLabel, pad.PriceUnitLabel);
    }

    [Fact]
    public void MoneyMode_DerivesTheWeight_FlooredToTheGram()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        pad.Input = "50";

        // 50 / 30 = 1.6666…; the customer named 50, so it is cut, not rounded.
        Assert.Equal(1.666m, pad.PreviewQuantityInUnit);
        Assert.Equal(3.332m, pad.PreviewQuantity);
        Assert.Equal(49.98m, pad.PreviewTotal);
        Assert.True(pad.IsRoundedDown);
        Assert.False(pad.IsRounded);
        Assert.True(pad.IsValid);
    }

    [Fact]
    public void MoneyMode_DerivesPieces_WhenThereIsNoSecondaryUnit()
    {
        var pad = PadFor(LooseCandy(), inUnit: false);
        pad.Mode = PadMode.Money;

        pad.Input = "50";

        Assert.Equal(1.666m, pad.PreviewQuantity);
        Assert.Equal(0m, pad.PreviewQuantityInUnit);
        Assert.Equal(49.98m, pad.PreviewTotal);
        Assert.True(pad.IsRoundedDown);
        Assert.True(pad.IsValid);
    }

    [Fact]
    public void MoneyMode_DoesNotFlagRoundDown_OnAnExactDivision()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        pad.Input = "60";

        Assert.Equal(2m, pad.PreviewQuantityInUnit);
        Assert.Equal(60m, pad.PreviewTotal);
        Assert.False(pad.IsRoundedDown);
    }

    [Fact]
    public void MoneyMode_RejectsASumBelowOneGram()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        pad.Input = "0.01";

        Assert.False(pad.IsValid);
        Assert.Equal(0m, pad.PreviewQuantity);
        Assert.False(pad.IsRoundedDown);
    }

    [Fact]
    public void MoneyMode_DividesByTheQuotedPrice_NotTheCatalogueOne()
    {
        var item = new CartItem { Product = LooseCandy(), Quantity = 1m, QuotedUnitPrice = 25m };
        var pad = new QuantityPadViewModel(item);
        pad.Mode = PadMode.Money;

        pad.Input = "50";

        Assert.Equal(2m, pad.PreviewQuantity);
        Assert.Equal(50m, pad.PreviewTotal);
    }

    [Fact]
    public void IsRoundedDown_IsFalse_OutsideMoneyMode()
    {
        var pad = PadFor(Nuggets());
        pad.Input = "1.6666";

        Assert.False(pad.IsRoundedDown);
    }
}
