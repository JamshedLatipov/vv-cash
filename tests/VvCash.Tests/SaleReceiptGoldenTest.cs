using System;
using System.Collections.Generic;
using System.IO;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Байты чека продажи, снятые ДО перевода раскладки на шаблон. Всё, что
/// делает этот план, обязано оставлять их неизменными: касса, до которой шаблон с
/// сервера не доехал, должна печатать ровно то, что печатала вчера.
///
/// Фикстура перегенерируется только при VVCASH_UPDATE_GOLDEN=1 и только руками.
/// Автоматическая перезапись превратила бы регрессию в молчаливое обновление
/// эталона — то есть ровно в то, от чего этот тест заведён.</summary>
public class SaleReceiptGoldenTest
{
    internal static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sale-receipt-default.bin");

    /// <summary>Продажа, задевающая каждую ветку раскладки: две позиции, одна из
    /// них во вторичной единице, ненулевая скидка с названием, и все четыре
    /// реквизита заполнены.</summary>
    internal static IReadOnlyList<CartItem> GoldenItems() => new[]
    {
        new CartItem
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
        },
        new CartItem
        {
            Product = new Product { Id = "p2", Name = "Клей", Price = 45m },
            Quantity = 3m,
        },
    };

    internal static byte[] BuildGolden() =>
        EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            GoldenItems(),
            subtotal: 5435m, discount: 435m, total: 5000m,
            discountName: "Акция «Ремонт»",
            documentNumber: "A-42",
            warehouseName: "Склад №1",
            sellerName: "Иванов",
            saleDate: "01.09.2026 12:30");

    [Fact]
    public void SaleReceipt_MatchesTheGoldenBytes()
    {
        var actual = BuildGolden();

        if (Environment.GetEnvironmentVariable("VVCASH_UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(FindRepoRoot(), "tests", "VvCash.Tests", "Fixtures");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "sale-receipt-default.bin"), actual);
            return;
        }

        Assert.True(File.Exists(FixturePath),
            $"Фикстуры нет: {FixturePath}. Сгенерируйте её с VVCASH_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllBytes(FixturePath), actual);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "vv-cash.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("vv-cash.slnx не найден выше по дереву");
    }
}
