using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

/// <summary>Общий эталон для кассы и для превью в бэкофисе: шаблон, демо-продажа
/// и строки, которые из них обязаны получиться. Копия файла лежит в bozor и
/// проверяется его собственными тестами.
///
/// rendererVersion поднимается РУКАМИ при каждой правке раскладки. Это и есть
/// признанная слабость схемы: забыл поднять — расхождение не поймает никто.
/// Полностью чинится только монорепой, пакетом или CI, чекаутящим оба
/// репозитория; см. раздел спеки «Расхождение двух рендереров».</summary>
public class ReceiptPreviewGoldenTest
{
    public const int RendererVersion = 1;

    /// <summary>Не вторая копия настроек сериализации, а те же самые
    /// (ReceiptTemplate.Options — camelCase-политика и camelCase-конвертер
    /// перечислений), скопированные и дополненные только форматом ВЫВОДА:
    /// отступы и без экранирования кириллицы, чтобы эталон можно было
    /// прочитать глазами (Step 4 этой задачи). Заведи здесь свою пару
    /// PropertyNamingPolicy/Converters — и при следующей правке enum'а или
    /// политики именования в ReceiptTemplate эта копия молча разъедется с
    /// боевой, а тест продолжит зеленеть.</summary>
    private static readonly JsonSerializerOptions Options = new(ReceiptTemplate.Options)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Позиции демо-продажи. СВОИ, а не GoldenItems() из байтового замка:
    /// на тех стоят пять уже закреплённых фикстур, и добавить к ним позицию нельзя
    /// не сдвинув байты. А позиция здесь нужна особая — с серединной суммой.
    ///
    /// 13.50 × 0.15 = 2.0250. Это ровно тот край, где ToString("F2") в C# (округление
    /// от нуля) даёт 2.03, а toFixed(2) в JavaScript — 2.02. Демо-продажа из круглых
    /// чисел сертифицировала бы паритет двух рендереров, которого на этом крае нет.</summary>
    private static IReadOnlyList<CartItem> PreviewItems() => new[]
    {
        new CartItem
        {
            Product = new Product
            {
                Id = "p1", Name = "Плитка", Price = 100m,
                UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
                UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
            },
            Quantity = 53m, QuantityInUnit = 12.72m, EnteredInUnit = true,
        },
        new CartItem
        {
            Product = new Product { Id = "p2", Name = "Клей", Price = 45m },
            Quantity = 3m,
        },
        new CartItem
        {
            Product = new Product { Id = "p3", Name = "Смесь", Price = 13.50m, IsDivisible = true },
            Quantity = 0.15m,
        },
    };

    private static SaleReceiptData DemoSale() => new(
        PreviewItems(),
        Subtotal: 5437.03m, Discount: 435m, Total: 5002.03m,
        DiscountName: "Акция «Ремонт»",
        DocumentNumber: "A-42", WarehouseName: "Склад №1",
        SellerName: "Иванов", SaleDate: "01.09.2026 12:30");

    [Fact]
    public void PreviewGolden_MatchesWhatTheRendererProduces()
    {
        var sale = DemoSale();
        var expected = ReceiptRenderer.Render(ReceiptTemplate.Default, sale)
            .OfType<TextOp>().Select(o => o.Line).ToArray();

        var fixturePath = FixturePath();

        if (Environment.GetEnvironmentVariable("VVCASH_UPDATE_GOLDEN") == "1")
        {
            var payload = new
            {
                rendererVersion = RendererVersion,
                template = ReceiptTemplate.Default,
                sale = new
                {
                    subtotal = sale.Subtotal, discount = sale.Discount, total = sale.Total,
                    discountName = sale.DiscountName, documentNumber = sale.DocumentNumber,
                    warehouseName = sale.WarehouseName, sellerName = sale.SellerName,
                    saleDate = sale.SaleDate,
                    // Строки, не decimal: TS-превью не форматирует деньги вовсе —
                    // сумма позиции "Смесь" едет уже посчитанной по C#-правилу
                    // (ToString("F2"), округление от нуля), потому что именно
                    // здесь, на 13.50 × 0.15 = 2.0250, JS toFixed(2) дал бы другую
                    // цифру. Считаются той же парой ReceiptText.Money/QuantityDisplay
                    // и тем же HasSecondaryUnit, что и боевой RenderItem — не
                    // переписаны вручную второй раз, чтобы не разъехаться с ним.
                    items = sale.Items.Select(i => new
                    {
                        name = i.Product.Name,
                        quantity = i.QuantityDisplay,
                        lineTotal = ReceiptText.Money(i.LineTotal),
                        secondaryUnit = i.Product.HasSecondaryUnit
                            ? $"{i.QuantityInUnitDisplay} {i.Product.UnitShortName}"
                            : (string?)null,
                    }).ToArray(),
                },
                expectedLines = expected,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            File.WriteAllText(fixturePath, JsonSerializer.Serialize(payload, Options));

            // Fail, а не return — та же причина, что в SaleReceiptGoldenTest:
            // утёкшая в CI или в чужую оболочку VVCASH_UPDATE_GOLDEN обязана дать
            // красный прогон, а не тихо-зелёный тест без единой проверки внутри.
            Assert.Fail(
                $"Эталон перезаписан: {fixturePath}. Проверьте `git diff` и перезапустите " +
                "без VVCASH_UPDATE_GOLDEN.");
        }

        Assert.True(File.Exists(fixturePath),
            $"Эталона нет: {fixturePath}. Сгенерируйте его с VVCASH_UPDATE_GOLDEN=1.");

        using var doc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        Assert.Equal(RendererVersion, doc.RootElement.GetProperty("rendererVersion").GetInt32());
        Assert.Equal(expected,
            doc.RootElement.GetProperty("expectedLines").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    /// <summary>Путь в ИСХОДНИКАХ, не в каталоге сборки. VvCash.Tests.csproj не
    /// копирует Fixtures\ в bin\ (там нет ни одного ItemGroup с
    /// CopyToOutputDirectory), поэтому Path.Combine(AppContext.BaseDirectory,
    /// "Fixtures", ...) — как было в черновике этой задачи — никогда не находит
    /// файл, который FindRepoRoot() только что записал в исходники: FAIL
    /// "Эталона нет" стоял бы даже сразу после VVCASH_UPDATE_GOLDEN=1, при
    /// каждом запуске. Путь по FindRepoRoot() — тот же приём, что уже
    /// использует SaleReceiptGoldenTest.FixturePath рядом, и работает по той же
    /// причине.</summary>
    private static string FixturePath() =>
        Path.Combine(FindRepoRoot(), "tests", "VvCash.Tests", "Fixtures", "receipt-golden.json");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "vv-cash.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("vv-cash.slnx не найден выше по дереву");
    }
}
