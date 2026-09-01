using System;
using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class ReceiptRendererTest
{
    /// <summary>Тип блока, которого switch в RenderBlock не знает. ReceiptBlock
    /// — обычный abstract class без явного конструктора, и компилятор
    /// генерирует для него protected конструктор без параметров — этого
    /// достаточно, чтобы унаследоваться из тестовой сборки.</summary>
    private sealed class UnknownBlock : ReceiptBlock
    {
    }

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

    [Fact]
    public void NoPrintedContent_ProducesNoCut()
    {
        // Логотип-картинка пока не печатает ничего (растр подключается в
        // Task 9) — блок отбрасывается целиком, до AlignOp, и список операций
        // пуст. Резать тут нечего: считать по длине списка было бы неверно в
        // общем случае (AlignOp/BoldOp/DoubleSizeOp сами по себе ничего не
        // печатают), здесь же список пуст в буквальном смысле.
        //
        // qr/barcode/logo(Nv) сюда больше не годятся как пример: все три
        // печатают графику (QrOp/BarcodeOp/NvLogoOp), а обрезка теперь
        // учитывает графические операции наравне с текстовыми — см.
        // ReceiptRenderer.Render и EscPosGraphicsTest.
        var ops = ReceiptRenderer.Render(One(new LogoBlock { Source = LogoSource.Bitmap }), Sale(Glue()));

        Assert.DoesNotContain(ops, o => o is CutOp);
    }

    [Fact]
    public void DroppedTextBlock_LeavesNoDanglingAlignOp()
    {
        var t = new ReceiptTemplate
        {
            Width = 32,
            Blocks = new List<ReceiptBlock>
            {
                new TextBlock { Content = "Doc #{doc}", Align = ReceiptAlign.Right },
                new TextBlock { Content = "OK" },
            },
        };
        var sale = Sale(Glue()) with { DocumentNumber = "" };

        var ops = ReceiptRenderer.Render(t, sale);

        Assert.DoesNotContain(ops, o => o is AlignOp { Align: ReceiptAlign.Right });
    }

    [Fact]
    public void DroppedBitmapLogoBlock_LeavesNoDanglingAlignOp()
    {
        // Зеркало DroppedTextBlock_LeavesNoDanglingAlignOp: логотип-картинка
        // тоже отбрасывается целиком (см. ReceiptRenderer.RenderBlock), и это
        // тоже обязано случиться ДО AlignOp, а не после.
        var t = new ReceiptTemplate
        {
            Width = 32,
            Blocks = new List<ReceiptBlock>
            {
                new LogoBlock { Source = LogoSource.Bitmap, Align = ReceiptAlign.Right },
                new TextBlock { Content = "OK" },
            },
        };

        var ops = ReceiptRenderer.Render(t, Sale(Glue()));

        Assert.DoesNotContain(ops, o => o is AlignOp { Align: ReceiptAlign.Right });
    }

    [Fact]
    public void Render_ThrowsNotSupported_ForAnUnhandledBlockType()
    {
        // Тот же довод, что у EscPosEmitterTest.Emit_ThrowsNotSupported_ForAnUnhandledOpType:
        // switch по типу блока не проверяется компилятором на полноту, и
        // default обязан быть живой, закреплённой тестом веткой — иначе
        // забытый в switch новый тип блока молча превратился бы в "решили не
        // печатать" вместо ошибки, замеченной на этапе разработки.
        var t = One(new UnknownBlock());

        var ex = Assert.Throws<NotSupportedException>(() => ReceiptRenderer.Render(t, Sale(Glue())));
        Assert.Contains(nameof(UnknownBlock), ex.Message);
    }

    [Fact]
    public void Items_ShowTheQuotedUnitPrice_NotTheCatalogPrice()
    {
        // Сервер оценивает корзину по каталогу склада и игнорирует цену,
        // присланную кассой — QuotedUnitPrice, когда он есть, и есть то, что
        // реально платит клиент. LineTotal уже считается из него; печатать
        // рядом Product.Price значит показать цену, с которой сумма строки
        // арифметически не сходится.
        var item = Glue();
        item.QuotedUnitPrice = 40m;

        var lines = Lines(One(new ItemsBlock { ShowUnitPrice = true }), Sale(item));

        Assert.Contains("Клей x3" + new string(' ', 19) + "120.00", lines);
        Assert.Contains("    3 x 40.00", lines);
        Assert.DoesNotContain(lines, l => l.Contains("45.00"));
    }

    [Fact]
    public void Items_AddALineDiscountLine_WhenEnabled()
    {
        var item = Glue();
        item.QuotedUnitDiscount = 5m; // LineDiscount = 5 * 3 = 15.00

        var lines = Lines(One(new ItemsBlock { ShowLineDiscount = true }), Sale(item));

        Assert.Contains("    Discount:" + new string(' ', 13) + "-15.00", lines);
    }

    [Fact]
    public void Items_OmitTheLineDiscountLine_WhenThereIsNone()
    {
        var lines = Lines(One(new ItemsBlock { ShowLineDiscount = true }), Sale(Glue()));

        Assert.DoesNotContain(lines, l => l.Contains("Discount"));
    }

    [Fact]
    public void Items_AddASkuLine_WhenEnabled()
    {
        var item = new CartItem
        {
            Product = new Product { Id = "p6", Name = "Гвозди", Price = 10m, Sku = "SKU-1" },
            Quantity = 1m,
        };

        var lines = Lines(One(new ItemsBlock { ShowSku = true }), Sale(item));

        Assert.Contains("    SKU-1", lines);
    }

    [Fact]
    public void Items_AddABarcodeLine_WhenEnabled()
    {
        var item = new CartItem
        {
            Product = new Product { Id = "p7", Name = "Гвозди", Price = 10m, Barcode = "4600000000000" },
            Quantity = 1m,
        };

        var lines = Lines(One(new ItemsBlock { ShowBarcode = true }), Sale(item));

        Assert.Contains("    4600000000000", lines);
    }

    [Fact]
    public void Items_StripNewlinesFromTheProductName()
    {
        // Замер из ревью: "Клей\nОПАСНО" даёт сырой 0x0A посреди строки чека,
        // ломая и выравнивание PadLine, и число строк на бумаге.
        var item = new CartItem
        {
            Product = new Product { Id = "p9", Name = "Клей\nОПАСНО", Price = 45m },
            Quantity = 1m,
        };

        var lines = Lines(One(new ItemsBlock()), Sale(item));

        Assert.Equal(new[] { "Клей ОПАСНО x1" + new string(' ', 13) + "45.00" }, lines);
        Assert.DoesNotContain(lines, l => l.Contains('\n'));
    }

    [Fact]
    public void Text_StripsNewlinesThatArriveThroughASubstitution()
    {
        // TextBlock.Content чистит "\n" в своём сеттере, но подстановка идёт
        // мимо этого сеттера — она подставляет значение уже после него.
        var t = One(new TextBlock { Content = "Продавец: {seller}" });
        var sale = Sale(Glue()) with { SellerName = "Пётр\nОПАСНО" };

        Assert.Equal(new[] { "Продавец: Пётр ОПАСНО" }, Lines(t, sale));
    }

    [Fact]
    public void Fields_StripsNewlinesFromTheLabel()
    {
        // Label приходит из того же конструктора, ради которого сеттер
        // TextBlock.Content и был заведён — но сам сеттер тут не задействован.
        var t = One(new FieldsBlock
        {
            Fields = new List<ReceiptField> { new() { Key = "doc", Label = "Doc\n#" } },
        });

        Assert.Equal(new[] { "Doc #A-1" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Fields_PrintsThePlaceholderVerbatim_ForAnUnknownKey()
    {
        // Та же политика, что у TrySubstitute для TextBlock: опечатка в ключе
        // ({sellr} вместо {seller}) должна быть видна на бумаге, а не молча
        // пропасть неотличимо от "seller" оказался пустым.
        var t = One(new FieldsBlock
        {
            Fields = new List<ReceiptField> { new() { Key = "sellr", Label = "Seller: " } },
        });

        Assert.Equal(new[] { "Seller: {sellr}" }, Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Fields_SkipsAnEmptyKey_WithoutPrintingAPlaceholder()
    {
        // Незаполненная строка в конструкторе — это "поле ещё не выбрали",
        // а не опечатка. Показывать "X: {}" покупателю незачем: пустой ключ
        // проверяется до ветки "неизвестный ключ", а не попадает в неё.
        var t = One(new FieldsBlock
        {
            Fields = new List<ReceiptField> { new() { Key = "", Label = "X: " } },
        });

        Assert.Empty(Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Render_SurvivesATemplateWithANullFieldsList()
    {
        // Штатный, а не гипотетический вход: сервер на Go сериализует так
        // nil-слайс, тем же доводом, что и "blocks":null у ReceiptTemplate.
        // Раньше foreach по null-списку ронял чек целиком —
        // NullReferenceException на каждой продаже, пока кто-то не поправит
        // шаблон, и PrintReceiptAsync возвращал бы false без единого чека.
        var t = ReceiptTemplate.Parse("""{"blocks":[{"type":"fields","fields":null}]}""");

        var ops = ReceiptRenderer.Render(t, Sale(Glue()));

        Assert.Empty(ops.OfType<TextOp>());
    }

    [Fact]
    public void Render_SurvivesATemplateWithANullFieldKey()
    {
        // "key":null — та же категория штатного JSON, что и "fields":null.
        // Раньше values.TryGetValue(field.Key, ...) с null ронял
        // ArgumentNullException прямо из рендерера.
        var t = ReceiptTemplate.Parse(
            """{"blocks":[{"type":"fields","fields":[{"key":null,"label":"X: "}]}]}""");

        Assert.Empty(Lines(t, Sale(Glue())));
    }

    [Fact]
    public void Items_LineDiscountLabel_IsConfigurable()
    {
        // TotalsBlock.DiscountLabel рядом настраивается из локали
        // администратора; зашитая в код латиница на строке позиции была бы
        // вторым, непереводимым словом для того же смысла на одном чеке.
        var item = Glue();
        item.QuotedUnitDiscount = 5m; // LineDiscount = 5 * 3 = 15.00

        var lines = Lines(
            One(new ItemsBlock { ShowLineDiscount = true, LineDiscountLabel = "Skidka:" }),
            Sale(item));

        Assert.Contains(lines, l => l.StartsWith("    Skidka:"));
        Assert.DoesNotContain(lines, l => l.Contains("Discount:"));
    }

    [Fact]
    public void Items_StripsAllControlCharacters_NotJustNewlines()
    {
        // Таб и ESC — не перевод строки, но тот же класс беды: PadLine
        // считает их печатными колонками (строка "шириной 32" выходит короче
        // на бумаге), а сырой ESC — первый байт чужой команды принтера,
        // которая съест следующий байт как свой параметр.
        var item = new CartItem
        {
            Product = new Product { Id = "p10", Name = "A\tB" + (char)27 + "C", Price = 10m },
            Quantity = 1m,
        };

        var lines = Lines(One(new ItemsBlock()), Sale(item));

        Assert.DoesNotContain(lines, l => l.Any(char.IsControl));
        Assert.Contains(lines, l => l.StartsWith("A B C x1"));
    }
}
