using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;
using Xunit.Sdk;

namespace VvCash.Tests;

/// <summary>Байты чека продажи, снятые ДО перевода раскладки на шаблон. Всё, что
/// делает этот план, обязано оставлять их неизменными: касса, до которой шаблон с
/// сервера не доехал, должна печатать ровно то, что печатала вчера.
///
/// Пять случаев, не один: "всё заполнено" задевает каждую ветку НАЛИЧИЯ (скидка,
/// бегунок, все четыре реквизита), но ни одной ветки ОТСУТСТВИЯ — а именно там
/// сидит самый частый чек вообще (обычная продажа без акции) и офлайновый
/// (пустые реквизиты). Рефакторинг, уронивший "Subtotal:" при нулевой скидке,
/// проходил бы замок из одного случая мимо. Ни один из первых трёх не задевает
/// ширину ленты: и название товара, и название акции там короче 32 колонок.
/// Четвёртый случай — с обоими длиной 40 символов — держит на месте
/// Math.Max(1, spaces) в PadLine и Truncate(discountName, 32), которые сейчас
/// пришпилены только этим тестом и разъедутся вместе с восемью литералами 32,
/// когда ширина ленты станет параметром слоя рендеринга. Пятый случай закрывает
/// последнюю непокрытую ветку самого метода: скидка есть, а названия у неё нет —
/// ручная скидка кассира или акция без имени, обе достижимы в бою. Строка
/// "Discount:" обязана остаться, строка с названием под ней — не появиться.
///
/// Фикстуры перегенерируются только при VVCASH_UPDATE_GOLDEN=1 и только руками —
/// режим обновления оканчивается Assert.Fail, а не тихим "return", именно чтобы
/// утёкшая в CI или в чужую оболочку переменная окружения давала красный прогон,
/// а не ложно-зелёный. Автоматическая перезапись без сигнала о ней превратила бы
/// регрессию в молчаливое обновление эталона — то есть ровно в то, от чего этот
/// тест заведён.</summary>
public class SaleReceiptGoldenTest
{
    /// <summary>Продажа, задевающая каждую ветку раскладки НАЛИЧИЯ: две позиции, одна
    /// из них во вторичной единице, ненулевая скидка с названием, и все четыре
    /// реквизита заполнены.
    ///
    /// Имя и сигнатура намеренно неприкосновенны: на них завязан отдельный тест
    /// в этом же плане (Task 12), который зовёт GoldenItems() напрямую.</summary>
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

    /// <summary>Ветка ОТСУТСТВИЯ: самый частый чек вообще — одна позиция без
    /// вторичной единицы, скидки нет (discount: 0, без discountName), и все четыре
    /// реквизита переданы пустой строкой, как их шлёт офлайновая продажа или касса
    /// без переключения продавцов — а не null, чтобы проверить именно ветку
    /// IsNullOrWhiteSpace на реальном "пусто", а не на отсутствующем аргументе.</summary>
    internal static byte[] BuildBareGolden() =>
        EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            new[]
            {
                new CartItem
                {
                    Product = new Product { Id = "p3", Name = "Мыло", Price = 50m },
                    Quantity = 2m,
                },
            },
            subtotal: 100m, discount: 0m, total: 100m,
            documentNumber: "",
            warehouseName: "",
            sellerName: "",
            saleDate: "");

    /// <summary>Ветка бегунка: тот же чек, что и GoldenItems()/BuildGolden(), но с
    /// заполненным queueNumber — живой путь через PrintKitchenOrderAsync, который
    /// печатает крупный "# NN" в шапке вместо номера документа.</summary>
    internal static byte[] BuildQueueGolden() =>
        EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            GoldenItems(),
            subtotal: 5435m, discount: 435m, total: 5000m,
            discountName: "Акция «Ремонт»",
            documentNumber: "A-42",
            warehouseName: "Склад №1",
            sellerName: "Иванов",
            saleDate: "01.09.2026 12:30",
            queueNumber: "17");

    /// <summary>Ветка ШИРИНЫ ленты: единственная величина, от которой зависит,
    /// влезет ли строка в 32 колонки термопринтера, встречается в раскладке
    /// восемь раз как голый литерал — и ни один из первых трёх случаев её не
    /// задевает, потому что там и название товара, и название акции короче
    /// ширины. Здесь оба ровно по 40 символов, заведомо длиннее 32:
    /// - строка позиции переполняет PadLine (Math.Max(1, spaces) вместо
    ///   отрицательного числа пробелов — цена прижимается к названию, а не
    ///   выравнивается по правому краю);
    /// - discountName длиннее ширины и обязан быть обрезан Truncate до 32
    ///   символов, а не перенесён или напечатан целиком.</summary>
    internal static byte[] BuildWideGolden() =>
        EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            new[]
            {
                new CartItem
                {
                    Product = new Product
                    {
                        Id = "p4", Name = "Ламинат влагостойкий, дуб дымчатый, 32кл", Price = 999m,
                    },
                    Quantity = 1m,
                },
            },
            subtotal: 999m, discount: 100m, total: 899m,
            discountName: "Скидка выходного дня на весь ассортимент");

    /// <summary>Последняя непокрытая ветка раскладки скидки — она живёт в
    /// TotalsBlock (см. ReceiptRenderer), а не в BuildSaleReceipt, у которого
    /// своей раскладки больше нет: скидка есть (discount > 0), а имени у неё
    /// нет (discountName — пустая строка, а не null — так её шлёт форма
    /// ручной скидки, где поле имени просто не заполнено). Достижима в бою:
    /// ручная скидка кассира и акция без названия дают ровно её. Ширина здесь
    /// ни при чём — короткое название товара и пустая discountName эту ветку
    /// не задевают, её проверяет sale-receipt-wide.bin.
    ///
    /// На бумаге ожидается: строка "Discount:" есть, строки с названием акции
    /// под ней — нет.</summary>
    internal static byte[] BuildDiscountNoNameGolden() =>
        EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            new[]
            {
                new CartItem
                {
                    Product = new Product { Id = "p5", Name = "Скотч", Price = 30m },
                    Quantity = 2m,
                },
            },
            subtotal: 60m, discount: 10m, total: 50m,
            discountName: "");

    public static IEnumerable<object[]> GoldenCases()
    {
        yield return new object[] { "sale-receipt-default.bin", (Func<byte[]>)BuildGolden };
        yield return new object[] { "sale-receipt-bare.bin", (Func<byte[]>)BuildBareGolden };
        yield return new object[] { "sale-receipt-queue.bin", (Func<byte[]>)BuildQueueGolden };
        yield return new object[] { "sale-receipt-wide.bin", (Func<byte[]>)BuildWideGolden };
        yield return new object[] { "sale-receipt-discount-no-name.bin", (Func<byte[]>)BuildDiscountNoNameGolden };
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void SaleReceipt_MatchesTheGoldenBytes(string fixtureName, Func<byte[]> build)
    {
        var actual = build();
        var fixturePath = Path.Combine(FindRepoRoot(), "tests", "VvCash.Tests", "Fixtures", fixtureName);

        if (Environment.GetEnvironmentVariable("VVCASH_UPDATE_GOLDEN") == "1")
        {
            // Каталог не создаётся здесь: Fixtures\ закоммичен и обязан существовать
            // сам по себе. Молчаливое Directory.CreateDirectory на разъехавшемся пути
            // создало бы пустой каталог не там и спрятало бы саму разъехавшуюся дорогу.
            File.WriteAllBytes(fixturePath, actual);
            // Fail, а не return: обновление эталона обязано быть видимым и осознанным
            // действием, а не побочным эффектом обычного прогона. Утёкшая в CI или в
            // чужую оболочку VVCASH_UPDATE_GOLDEN даёт красный прогон, а не молчаливую
            // перезапись замка под сломанный код.
            Assert.Fail(
                $"Эталон перезаписан ({actual.Length} б): {fixturePath}. " +
                "Проверьте `git diff --stat` и перезапустите БЕЗ VVCASH_UPDATE_GOLDEN.");
        }

        Assert.True(File.Exists(fixturePath),
            $"Фикстуры нет: {fixturePath}. Сгенерируйте её с VVCASH_UPDATE_GOLDEN=1.");

        var expected = File.ReadAllBytes(fixturePath);
        try
        {
            // Сначала читаемый построчный дифф декодированного текста — по нему
            // человек видит, какая строка чека съехала. Управляющие байты (ESC-команды)
            // тоже видны, вынесены в <XX>, а не проглочены как непечатаемые.
            Assert.Equal(Show(expected), Show(actual));
            // И всё-таки байт-в-байт: текстовое представление ловит съехавшую строку,
            // но не невидимую ESC-команду, которая ничего не меняет в тексте, но меняет
            // поведение принтера (жирный, выравнивание, разрез).
            Assert.Equal(expected, actual);
        }
        catch (XunitException ex)
        {
            throw new XunitException(
                ex.Message + "\n\nЕсли расхождение — ожидаемая правка раскладки, а не регрессия: " +
                "перегенерируйте эталон через VVCASH_UPDATE_GOLDEN=1, декодируйте новый файл и " +
                "посмотрите на него глазами, прежде чем коммитить отдельно от кода раскладки.",
                ex);
        }
    }

    /// <summary>Декодирует байты чека в текст, выводя управляющие байты (ESC-команды)
    /// как видимые <XX> вместо того, чтобы дать им молча пропасть в выводе как
    /// непечатаемым символам — иначе "жирный включён" и "жирный выключен" выглядели
    /// бы в диффе одинаково пусто.</summary>
    private static string Show(byte[] bytes) => string.Join("\n",
        EscPosCodePages.Cp866.Encoding.GetString(bytes)
            .Split('\n')
            .Select(l => Regex.Replace(l, @"\p{Cc}", m => $"<{(int)m.Value[0]:X2}>")));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // "vv-cash.slnx" — переименование решения тихо сломает эту функцию: она
        // перестанет находить корень репозитория, и VVCASH_UPDATE_GOLDEN=1 либо упадёт
        // на InvalidOperationException ниже, либо (если найдёт чужой vv-cash.slnx выше
        // по дереву) молча запишет фикстуру не в тот репозиторий. Переименовывая
        // решение, поправьте и это имя.
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "vv-cash.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"vv-cash.slnx не найден выше по дереву: {AppContext.BaseDirectory}");
    }
}
