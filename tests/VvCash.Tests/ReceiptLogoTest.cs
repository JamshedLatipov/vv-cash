using System;
using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class ReceiptLogoTest
{
    private static readonly SaleReceiptData Empty = new(new List<CartItem>(), 0m, 0m, 0m);

    private static ReceiptTemplate WithLogo() => new()
    {
        Blocks = new List<ReceiptBlock> { new LogoBlock { Source = LogoSource.Bitmap } },
    };

    /// <summary>Формат опции receipt_logo: ширина в БАЙТАХ, высота в точках,
    /// растр в base64. Байты, а не точки, потому что столько же требует GS v 0 —
    /// пересчёт в одном месте лучше, чем в двух репозиториях.
    ///
    /// "AAAAAAAAAAAAAAAA" — 16 base64-символов без паддинга, ровно 12 нулевых
    /// байт (6 widthBytes × 2 height = 12): BitmapOp требует ТОЧНОГО совпадения
    /// длины растра с объявленным размером, а не "не меньше", так что фикстура
    /// обязана декодироваться ровно в 12 байт, не в 10 и не в 13.</summary>
    private const string Logo = """{"widthBytes":6,"height":2,"raster":"AAAAAAAAAAAAAAAA"}""";

    [Fact]
    public void ABitmapLogo_BecomesABitmapOp()
    {
        var op = ReceiptRenderer.Render(WithLogo(), Empty, Logo).OfType<BitmapOp>().Single();

        Assert.Equal(6, op.WidthBytes);
        Assert.Equal(2, op.Height);
        Assert.Equal(12, op.Raster.Length);
    }

    [Fact]
    public void ABitmapLogoBlock_PrintsNothing_WhenNoLogoWasSynced()
    {
        // Блок включён, а картинки нет — это состояние наполовину настроенной
        // кассы, а не повод уронить чек.
        Assert.Empty(ReceiptRenderer.Render(WithLogo(), Empty, "").OfType<BitmapOp>());
    }

    [Fact]
    public void ABitmapLogoBlock_PrintsNothing_WhenTheLogoIsCorrupt()
    {
        Assert.Empty(ReceiptRenderer.Render(WithLogo(), Empty, "не json").OfType<BitmapOp>());
    }

    [Fact]
    public void ABitmapLogoBlock_PrintsNothing_WhenTheRasterIsNotBase64()
    {
        // "не base64!!!" содержит символы вне алфавита base64 — Convert.FromBase64String
        // бросает FormatException, а не что-то, что ParseLogo не ловит.
        const string json = """{"widthBytes":6,"height":2,"raster":"не base64!!!"}""";
        Assert.Empty(ReceiptRenderer.Render(WithLogo(), Empty, json).OfType<BitmapOp>());
    }

    [Fact]
    public void ABitmapLogoBlock_PrintsNothing_WhenTheDimensionsAreNotNumbers()
    {
        // widthBytes строкой, а не числом: JsonElement.GetInt32() на строковом
        // значении бросает InvalidOperationException, а не FormatException —
        // отдельная ветка catch, которую легко забыть.
        const string json = """{"widthBytes":"six","height":2,"raster":"AAAAAAAAAAAAAAAA"}""";
        Assert.Empty(ReceiptRenderer.Render(WithLogo(), Empty, json).OfType<BitmapOp>());
    }

    [Fact]
    public void ABitmapLogoBlock_PrintsNothing_WhenTheDeclaredSizeDoesNotMatchTheRasterLength()
    {
        // widthBytes×height=12, а декодированный растр — только 4 байта:
        // конструктор BitmapOp сам бросает ArgumentException на этом
        // рассогласовании, и разбор обязан его поймать, а не уронить печать.
        const string json = """{"widthBytes":6,"height":2,"raster":"AAAAAA=="}""";
        Assert.Empty(ReceiptRenderer.Render(WithLogo(), Empty, json).OfType<BitmapOp>());
    }

    [Fact]
    public void AnNvLogoBlock_IgnoresTheSyncedBitmap()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new LogoBlock { Source = LogoSource.Nv, NvSlot = 1 } },
        };

        var ops = ReceiptRenderer.Render(t, Empty, Logo);

        Assert.Empty(ops.OfType<BitmapOp>());
        Assert.Single(ops.OfType<NvLogoOp>());
    }
}
