using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class ReceiptRendererTest
{
    private static SaleReceiptData Sale(params CartItem[] items) => new(
        items, Subtotal: 100m, Discount: 0m, Total: 100m,
        DocumentNumber: "A-1", WarehouseName: "Склад", SellerName: "Пётр",
        SaleDate: "01.09.2026");

    private static CartItem Glue(decimal qty = 3m) => new()
    {
        Product = new Product { Id = "p2", Name = "Клей", Price = 45m },
        Quantity = qty,
    };

    private static string[] Lines(ReceiptTemplate t, SaleReceiptData sale) =>
        ReceiptRenderer.Render(t, sale).OfType<TextOp>().Select(o => o.Line).ToArray();

    private static ReceiptTemplate One(ReceiptBlock block) =>
        new() { Width = 32, Blocks = new List<ReceiptBlock> { block } };

    [Fact]
    public void Text_SubstitutesByName()
    {
        var t = One(new TextBlock { Content = "Продавец: {seller}" });

        Assert.Equal(new[] { "Продавец: Пётр" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Text_IsDroppedEntirely_WhenASubstitutionIsEmpty()
    {
        // Офлайновая продажа ещё не имеет номера документа, и пустая строка
        // "Doc #" на чеке — не информация, а мусор.
        var t = One(new TextBlock { Content = "Doc #{doc}" });
        var sale = Sale(Glue()) with { DocumentNumber = "" };

        Assert.Empty(Lines(t, sale));
    }

    [Fact]
    public void Text_WithNoSubstitutions_AlwaysPrints()
    {
        var t = One(new TextBlock { Content = "Спасибо за покупку" });

        Assert.Equal(new[] { "Спасибо за покупку" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Text_PrintsAnUnknownPlaceholderVerbatim()
    {
        // Опечатка в бэкофисе должна быть видна на бумаге. Молча съеденная
        // строка не показывает ничего.
        var t = One(new TextBlock { Content = "Итого: {tota}" });

        Assert.Equal(new[] { "Итого: {tota}" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void DisabledBlock_ProducesNothing()
    {
        var t = One(new TextBlock { Content = "Скрыто", Enabled = false });

        Assert.Empty(ReceiptRenderer.Render(t, Sale(Glue())));
    }

    [Fact]
    public void Line_RepeatsItsCharacter_AndZeroCountMeansFullWidth()
    {
        Assert.Equal(new[] { new string('-', 28) }, Lines(One(new LineBlock { Count = 28 }), Sale(Glue())));
        Assert.Equal(new[] { new string('=', 32) },
            Lines(One(new LineBlock { Char = "=", Count = 0 }), Sale(Glue())));
    }

    [Fact]
    public void Fields_PrintLabelThenValue_AndSkipEmptyOnes()
    {
        var t = One(new FieldsBlock
        {
            Fields = new List<ReceiptField>
            {
                new() { Key = "doc", Label = "Doc #" },
                new() { Key = "seller", Label = "Seller: " },
            },
        });
        var sale = Sale(Glue()) with { SellerName = "" };

        Assert.Equal(new[] { "Doc #A-1" }, Lines(t, sale));
    }

    [Fact]
    public void Items_PadTheLineTotalToTheTemplateWidth()
    {
        var t = One(new ItemsBlock());

        Assert.Equal(new[] { "Клей x3" + new string(' ', 19) + "135.00" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Items_RespectTheTemplateWidth_NotAHardcodedThirtyTwo()
    {
        // Ради этого ширина и стала параметром: 80-мм лента — 42 колонки.
        var t = new ReceiptTemplate { Width = 42, Blocks = new List<ReceiptBlock> { new ItemsBlock() } };

        Assert.Equal(new[] { "Клей x3" + new string(' ', 29) + "135.00" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Items_ShowTheSecondaryUnitOnItsOwnLine_WhenEnabled()
    {
        // Клиент просил квадратные метры, а платит за целые плитки; показать
        // одно без другого — значит выдать округление за ошибку.
        var tile = new CartItem
        {
            Product = new Product
            {
                Id = "p1", Name = "Плитка", Price = 100m,
                UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
                UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
            },
            Quantity = 53m, QuantityInUnit = 12.72m, EnteredInUnit = true,
        };

        var shown = Lines(One(new ItemsBlock { ShowSecondaryUnit = true }), Sale(tile));
        var hidden = Lines(One(new ItemsBlock { ShowSecondaryUnit = false }), Sale(tile));

        Assert.Contains("    12.72 м²", shown);
        Assert.DoesNotContain(hidden, l => l.Contains("12.72"));
    }

    [Fact]
    public void Items_AddAUnitPriceLine_WhenEnabled()
    {
        var lines = Lines(One(new ItemsBlock { ShowUnitPrice = true }), Sale(Glue()));

        Assert.Contains("    3 x 45.00", lines);
    }

    [Fact]
    public void Totals_PrintSubtotalDiscountAndTotal_WithTheirLabels()
    {
        var t = One(new TotalsBlock());
        var sale = Sale(Glue()) with { Subtotal = 150m, Discount = 50m, Total = 100m, DiscountName = "Акция" };

        var lines = Lines(t, sale);

        Assert.Equal("Subtotal:" + new string(' ', 17) + "150.00", lines[0]);
        Assert.Equal("Discount:" + new string(' ', 17) + "-50.00", lines[1]);
        Assert.Equal("Акция", lines[2]);
        Assert.Equal("TOTAL:" + new string(' ', 20) + "100.00", lines[3]);
    }

    [Fact]
    public void Totals_OmitTheDiscountLines_WhenThereIsNoDiscount()
    {
        var lines = Lines(One(new TotalsBlock()), Sale(Glue()) with { Subtotal = 100m, Discount = 0m });

        Assert.DoesNotContain(lines, l => l.StartsWith("Discount:"));
    }

    [Fact]
    public void Totals_WrapTheTotalInBold_WhenAsked()
    {
        var ops = ReceiptRenderer.Render(One(new TotalsBlock { BoldTotal = true }), Sale(Glue()));

        var opsList = ops.ToList();
        var boldOn = opsList.FindIndex(o => o is BoldOp { On: true });
        var boldOff = opsList.FindIndex(boldOn + 1, o => o is BoldOp { On: false });
        Assert.True(boldOn >= 0 && boldOff > boldOn);
    }

    [Fact]
    public void Render_ClosesTheDocumentWithACut()
    {
        var ops = ReceiptRenderer.Render(One(new TextBlock { Content = "A" }), Sale(Glue()));

        Assert.IsType<CutOp>(ops[^1]);
    }
}
