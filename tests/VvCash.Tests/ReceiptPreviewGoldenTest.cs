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

/// <summary>Общий эталон для кассы и для превью в бэкофисе: список именованных
/// случаев, для каждого — шаблон, продажа и строки, которые из них обязаны
/// получиться. Копия файла лежит в bozor и проверяется его собственными
/// тестами.
///
/// Список случаев, а не один сценарий — тот же довод, которым в
/// SaleReceiptGoldenTest обоснованы пять байтовых фикстур вместо одной: один
/// сценарий задевает ветки НАЛИЧИЯ и почти не задевает ветки ОТСУТСТВИЯ и
/// раскладку НЕШТАТНОГО (не ReceiptTemplate.Default) шаблона. Дешевле, чем
/// там: байты здесь не пришпилены, только текст.
///
/// rendererVersion поднимается РУКАМИ при ЛЮБОЙ из двух разных правок — и это
/// МЁРТВОЕ поле, пока сторона bozor не завела у себя ожидаемую константу и
/// не сравнивает её с этим числом при КАЖДОМ чтении фикстуры. Без такого
/// сравнения там номер версии — комментарий, а не защита: раскладку можно
/// поменять, перегенерировать эталон, забыть поднять число — и всё останется
/// зелёным по обе стороны, ровно тот случай, ради которого поле заведено. См.
/// scripts/sync-receipt-fixture.ps1 — он печатает текущее значение при
/// каждом копировании именно затем, чтобы это было на виду у того, кто
/// копирует.
///
/// Две причины поднять число, не одна:
/// 1. Изменились ПРАВИЛА РАСКЛАДКИ (что и как печатается) — тест на стороне
///    bozor должен перечитать expectedLines и, если он их сверяет посимвольно,
///    обновить у себя ожидаемое.
/// 2. Изменилась СХЕМА ФАЙЛА (форма JSON, а не то, что она описывает) — тест
///    на стороне bozor, написанный под старые имена полей и типы, либо упадёт
///    невнятно на разборе, либо — хуже — тихо прочитает пустоту и сравнит её
///    с пустотой. Ровно это случилось при переходе с одного сценария на
///    список "cases" и со строк на числа в деньгах/количестве: раскладка не
///    менялась ни на бит, а старый тест на другой стороне тем не менее обязан
///    был перечитать файл заново, и версия обязана была это просигналить.
/// Обе причины требуют одного и того же действия от чужой стороны — перечитать
/// файл и поправить свой код под него, — поэтому и число одно, а не два разных
/// счётчика.</summary>
public class ReceiptPreviewGoldenTest
{
    public const int RendererVersion = 2;

    /// <summary>Не вторая копия настроек сериализации, а те же самые
    /// (ReceiptTemplate.Options — camelCase-политика и camelCase-конвертер
    /// перечислений), скопированные и дополненные только форматом ВЫВОДА:
    /// отступы и без экранирования кириллицы, чтобы эталон можно было
    /// прочитать глазами.</summary>
    private static readonly JsonSerializerOptions Options = new(ReceiptTemplate.Options)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Case(string Name, ReceiptTemplate Template, SaleReceiptData Sale);

    /// <summary>Случай "по умолчанию": раскладка ReceiptTemplate.Default на
    /// продаже, которая одним чеком задевает пять разных крайних случаев
    /// формата — каждый подписан на своей позиции ниже.</summary>
    private static SaleReceiptData DefaultSale()
    {
        var items = new[]
        {
            new CartItem
            {
                Product = new Product
                {
                    Id = "p1", Name = "Плитка", Price = 100m,
                    UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
                    UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
                },
                // 12.7255, не 12.72: у формата основного количества ("0.###",
                // до трёх знаков) и вторичной единицы ("0.######", до шести)
                // разное число знаков после запятой, а на значении с двумя
                // знаками форматы неотличимы — двойник, взявший не тот формат,
                // эталон бы не заметил.
                Quantity = 53m, QuantityInUnit = 12.7255m, EnteredInUnit = true,
            },
            new CartItem
            {
                Product = new Product { Id = "p2", Name = "Клей", Price = 45m },
                Quantity = 3m,
            },
            // Серединное значение: 13.50 × 0.15 = 2.0250 — ровно тот край, где
            // ToString("F2") в C# (округление от нуля) расходится с toFixed(2)
            // в JavaScript. См. ItemJson/SaleJson — почему сумма позиции едет
            // в JSON СЫРЫМ числом, а не уже отформатированной строкой.
            new CartItem
            {
                Product = new Product { Id = "p3", Name = "Смесь", Price = 13.50m, IsDivisible = true },
                Quantity = 0.15m,
            },
            // Точное совпадение с шириной ленты: "Дюбель-гвоздь 6х40 мм x50"
            // (25 симв.) + "1234.50" (7 симв.) = 32 = ширина ленты ровно, без
            // места на разделяющий пробел между колонками. ReceiptText.PadLine
            // документирует это как намеренный компромисс (Math.Max(1, spaces)
            // даёт строку в 33 символа, а не 32 слипшихся) — двойник,
            // потянувшийся за padEnd(width), эту строку не воспроизведёт.
            new CartItem
            {
                Product = new Product { Id = "p4", Name = "Дюбель-гвоздь 6х40 мм", Price = 24.69m },
                Quantity = 50m,
            },
            // Переполнение: имя само по себе (40 символов) уже длиннее ленты —
            // тот же класс беды, что байтовая фикстура sale-receipt-wide.bin,
            // на своей отдельной строке и своих данных.
            new CartItem
            {
                Product = new Product { Id = "p5", Name = "Керамогранит матовый тёмно-серый 60х60см", Price = 999m },
                Quantity = 1m,
            },
        };

        var subtotal = items.Sum(i => i.LineTotal);
        const decimal discount = 435m;

        return new SaleReceiptData(
            items, Subtotal: subtotal, Discount: discount, Total: subtotal - discount,
            // 40 символов, не 14: обрезка Truncate(discountName, width) этим
            // эталоном раньше не проявлялась вовсе (ту же ветку на байтах
            // ловит отдельная фикстура sale-receipt-wide.bin) — замена ничего
            // не теряет.
            DiscountName: "Скидка на весь ассортимент этой недели-2",
            DocumentNumber: "A-42", WarehouseName: "Склад №1",
            SellerName: "Иванов", SaleDate: "01.09.2026 12:30");
    }

    /// <summary>Самый частый чек вообще: скидки нет, все реквизиты пустые (не
    /// null — так их шлёт офлайновая продажа или касса без переключения
    /// продавцов, ровно как SaleReceiptGoldenTest.BuildBareGolden), одна
    /// простая позиция. Закрывает разом ветку ОТСУТСТВИЯ скидки и ветку
    /// ОТСУТСТВИЯ реквизитов — включая номер бегунка: единственный ключ
    /// подстановки, которого раньше не было ни в одном случае эталона вовсе,
    /// хотя правило "известно, но пусто" на нём и держится. Заодно несёт
    /// управляющие символы в имени товара — см. комментарий на позиции ниже.</summary>
    private static SaleReceiptData OfflineSale()
    {
        var items = new[]
        {
            // Таб между первым и вторым словом, "\r\n" между вторым и
            // третьим — оба ровно там, где их вполне может оставить импорт
            // каталога из Excel или веб-формы бэкофиса, и оба проходят через
            // ReceiptRenderer.Sanitize, а не через TextBlock.Content.set
            // (тот чистит только литеральный текст самого шаблона). Sanitize
            // документирует конкретный ПОРЯДОК — сначала "\r\n" целиком
            // схлопывается в один пробел, потом уже отдельные управляющие
            // символы (включая уцелевший таб) заменяются на пробел по
            // одному, — и порядок здесь важен: сделай наоборот, и "\r\n"
            // распадётся на два независимых пробела раньше, чем сработает
            // правило "одна пара — один пробел", а строка выйдет с двойным
            // пробелом там, где должен быть одинарный. Без этой позиции
            // правило порядка не проверяет ничего.
            new CartItem
            {
                Product = new Product { Id = "p6", Name = "Плёнка\tзащитная\r\nпрозрачная", Price = 25m },
                Quantity = 4m,
            },
        };
        var subtotal = items.Sum(i => i.LineTotal);

        return new SaleReceiptData(
            items, Subtotal: subtotal, Discount: 0m, Total: subtotal,
            DocumentNumber: "", WarehouseName: "", SellerName: "", SaleDate: "",
            QueueNumber: "");
    }

    /// <summary>Позиция для случая "шаблон из конструктора": несёт цену за
    /// единицу, артикул, штрихкод и скидку по строке разом — четыре поля,
    /// которых не было ни у одной позиции остальных случаев, а ItemsBlock
    /// этого случая включает флаги показа всех четырёх сразу.</summary>
    private static SaleReceiptData ConstructorSale()
    {
        var items = new[]
        {
            new CartItem
            {
                Product = new Product
                {
                    Id = "p7", Name = "Дрель ударная", Price = 3500m,
                    Sku = "SKU-4471", Barcode = "4870000012345",
                },
                Quantity = 1m,
                QuotedUnitPrice = 3325m,
                QuotedUnitDiscount = 175m,
            },
        };
        var subtotal = items.Sum(i => i.LineTotal);

        return new SaleReceiptData(
            items, Subtotal: subtotal, Discount: 0m, Total: subtotal,
            DocumentNumber: "A-90", WarehouseName: "Склад №2",
            SellerName: "Петров", SaleDate: "01.09.2026 13:15",
            QueueNumber: "");
    }

    /// <summary>Шаблон, который реально мог бы собрать администратор в
    /// конструкторе — не ReceiptTemplate.Default. Один случай ловит девять
    /// правил разом, каждое — на своём блоке:
    /// 1. Enabled=false — блок не печатается вовсе.
    /// 2. Незнакомая подстановка ({unknown}) остаётся в строке буквально.
    /// 3. Незнакомый ключ поля печатается как Label + "{key}".
    /// 4. Пустой ключ поля пропускается целиком.
    /// 5. LineBlock.Count=0 — разделитель на всю ширину ленты (32), а не на
    ///    заданные литералом 28, как во всех LineBlock ReceiptTemplate.Default.
    /// 6. ItemsBlock.ShowUnitPrice — подстрока "количество x цена".
    /// 7. ItemsBlock.ShowSku — подстрока с артикулом.
    /// 8. ItemsBlock.ShowBarcode — подстрока со штрихкодом.
    /// 9. ItemsBlock.ShowLineDiscount — подстрока со скидкой по строке.</summary>
    private static ReceiptTemplate ConstructorTemplate() => new()
    {
        Version = ReceiptTemplate.CurrentVersion,
        Width = 32,
        Blocks = new List<ReceiptBlock>
        {
            new TextBlock { Content = "VV CASH POS", Align = ReceiptAlign.Center, DoubleSize = true },
            new TextBlock { Content = "Bonus: {unknown}", Align = ReceiptAlign.Center, Enabled = false },
            new TextBlock { Content = "Промо: {unknown}", Align = ReceiptAlign.Center },
            new FieldsBlock
            {
                Align = ReceiptAlign.Center,
                Fields = new List<ReceiptField>
                {
                    new() { Key = "doc", Label = "Doc #" },
                    new() { Key = "phone", Label = "Tel: " },
                    new() { Key = "", Label = "ignored" },
                },
            },
            new LineBlock { Align = ReceiptAlign.Center, Count = 0 },
            new ItemsBlock
            {
                Align = ReceiptAlign.Left,
                ShowUnitPrice = true, ShowSku = true, ShowBarcode = true,
                ShowSecondaryUnit = true, ShowLineDiscount = true,
            },
            new LineBlock { Align = ReceiptAlign.Left, Count = 28 },
            new TotalsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left, Count = 28 },
            new TextBlock { Content = "Thank you for shopping!", Align = ReceiptAlign.Center },
            new FeedBlock { Lines = 2, Align = ReceiptAlign.Center },
        },
    };

    /// <summary>Последняя непокрытая ветка блока итогов: скидка есть, а имени
    /// у неё нет (discountName — пустая строка, не null: так её шлёт форма
    /// ручной скидки кассира, где поле имени просто не заполнено). Строка
    /// "Discount:" обязана остаться на бумаге, строка с названием под ней —
    /// не появиться.</summary>
    private static SaleReceiptData DiscountNoNameSale()
    {
        var items = new[]
        {
            new CartItem
            {
                Product = new Product
                {
                    Id = "p8", Name = "Скотч малярный жёлтый", Price = 25m,
                    Sku = "SKU-1002", Barcode = "4870000054321",
                },
                // 4.1234, не целое 4: CartItem.QuantityDisplay форматирует
                // основное количество как "0.###" (потолок в три знака), а
                // QuantityInUnitDisplay у вторичной единицы — как "0.######"
                // (потолок в шесть). У всех остальных количеств во всех
                // случаях эталона не больше двух знаков после запятой, и оба
                // формата на них неотличимы — мутация, подменяющая один
                // потолок другим, эталон бы не заметила. Ставится сюда, а не
                // в случай "default": там любая правка количества
                // пересчитала бы подытог и сдвинула бы 7670.5250 — тот самый
                // дискриминатор округления, ради которого набор позиций
                // default собран. Здесь подытог ничем не пришпилен.
                Quantity = 4.1234m,
            },
        };
        var subtotal = items.Sum(i => i.LineTotal);
        const decimal discount = 15m;

        return new SaleReceiptData(
            items, Subtotal: subtotal, Discount: discount, Total: subtotal - discount,
            DiscountName: "",
            DocumentNumber: "A-15", WarehouseName: "Склад №1",
            SellerName: "Сидоров", SaleDate: "01.09.2026 14:00",
            QueueNumber: "");
    }

    private static IReadOnlyList<Case> Cases() => new[]
    {
        new Case("default", ReceiptTemplate.Default, DefaultSale()),
        new Case("offline", ReceiptTemplate.Default, OfflineSale()),
        new Case("constructor-template", ConstructorTemplate(), ConstructorSale()),
        new Case("discount-no-name", ReceiptTemplate.Default, DiscountNoNameSale()),
    };

    /// <summary>Сырые данные позиции — БЕЗ форматирования денег или
    /// количества. Раньше сумма позиции ехала в JSON уже отформатированной
    /// строкой ("2.03"), а итоги (subtotal/discount/total) — числами: эта
    /// несогласованность и была дырой, которую нашло ревью. Строка не даёт
    /// двойнику вообще ничего посчитать — он просто печатает то, что дали, и
    /// расхождение F2/toFixed тогда ловилось бы только на итогах, но не на
    /// позициях, хотя позиция "Смесь" заведена именно ради него. Число
    /// заставляет двойника САМОМУ применить своё правило округления к сырому
    /// значению — тем же способом, каким это делают
    /// ReceiptRenderer.RenderItem/RenderTotals на стороне кассы — и только
    /// тогда сравнение с expectedLines проверяет формат, а не дословную
    /// передачу строки. Отсюда общее правило для всего объекта sale ниже:
    /// деньги и количество — везде числа, ни одной готовой строки.</summary>
    private static object ItemJson(CartItem i) => new
    {
        name = i.Product.Name,
        quantity = i.Quantity,
        lineTotal = i.LineTotal,
        unitPrice = i.UnitPrice,
        sku = string.IsNullOrWhiteSpace(i.Product.Sku) ? null : i.Product.Sku,
        barcode = string.IsNullOrWhiteSpace(i.Product.Barcode) ? null : i.Product.Barcode,
        lineDiscount = i.LineDiscount,
        quantityInUnit = i.Product.HasSecondaryUnit ? i.QuantityInUnit : (decimal?)null,
        secondaryUnitName = i.Product.HasSecondaryUnit ? i.Product.UnitShortName : null,
    };

    private static object SaleJson(SaleReceiptData s) => new
    {
        documentNumber = s.DocumentNumber,
        warehouseName = s.WarehouseName,
        sellerName = s.SellerName,
        saleDate = s.SaleDate,
        // Пустая строка, а не отсутствующий ключ: это и есть пример
        // "известного, но пустого" имени подстановки на КОНКРЕТНЫХ данных —
        // раньше ни у одного случая эталона не было позиции, которая
        // отличала бы это правило от "неизвестного имени" (это второе
        // правило показывает ключ "phone" в constructor-template).
        queueNumber = s.QueueNumber ?? "",
        discountName = s.DiscountName,
        subtotal = s.Subtotal,
        discount = s.Discount,
        total = s.Total,
        items = s.Items.Select(ItemJson).ToArray(),
    };

    [Fact]
    public void PreviewGolden_MatchesWhatTheRendererProduces()
    {
        var payload = new
        {
            rendererVersion = RendererVersion,
            cases = Cases().Select(c => new
            {
                name = c.Name,
                template = c.Template,
                sale = SaleJson(c.Sale),
                expectedLines = ReceiptRenderer.Render(c.Template, c.Sale)
                    .OfType<TextOp>().Select(o => o.Line).ToArray(),
            }).ToArray(),
        };

        var expectedText = NormalizeJsonText(JsonSerializer.Serialize(payload, Options));
        var fixturePath = FixturePath();

        if (Environment.GetEnvironmentVariable("VVCASH_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            File.WriteAllText(fixturePath, expectedText);

            // Fail, а не return — та же причина, что в SaleReceiptGoldenTest:
            // утёкшая в CI или в чужую оболочку VVCASH_UPDATE_GOLDEN обязана
            // дать красный прогон, а не тихо-зелёный тест без единой проверки.
            Assert.Fail(
                $"Эталон перезаписан: {fixturePath}. Проверьте `git diff` и перезапустите " +
                "без VVCASH_UPDATE_GOLDEN.");
        }

        Assert.True(File.Exists(fixturePath),
            $"Эталона нет: {fixturePath}. Сгенерируйте его с VVCASH_UPDATE_GOLDEN=1.");

        // Файл ЦЕЛИКОМ, а не rendererVersion и expectedLines по отдельности:
        // сравнение той же строки, которую построил бы режим обновления, с
        // тем, что реально лежит на диске, ловит любое ручное расхождение —
        // подправленную вручную ширину ленты, подпись реквизита, состав
        // блоков шаблона — а не только итоговые строки чека.
        var actualText = NormalizeJsonText(File.ReadAllText(fixturePath));
        Assert.Equal(expectedText, actualText);
    }

    /// <summary>LF и ровно один завершающий перевод строки — не то, что
    /// решит система сборки. System.Text.Json c WriteIndented=true переносит
    /// строки через Environment.NewLine (CRLF на Windows, LF на Linux/mac);
    /// перегенерация на другой ОС дала бы другой файл во всех полутора сотнях
    /// строк, а .gitattributes для этого файла (см. файл — ему явно возвращён
    /// текстовый диф) больше не нормализует построчные окончания при
    /// коммите, раз он больше не подпадает под общее правило "binary" для
    /// каталога Fixtures\. Явная нормализация здесь — единственная гарантия
    /// того, что байты не зависят от того, где выполнялась генерация.</summary>
    private static string NormalizeJsonText(string json) =>
        json.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    /// <summary>Путь в ИСХОДНИКАХ, не в каталоге сборки. VvCash.Tests.csproj не
    /// копирует Fixtures\ в bin\, поэтому путь по AppContext.BaseDirectory
    /// никогда не находит файл, который FindRepoRoot() только что записал в
    /// исходники. Тот же приём, что уже использует
    /// SaleReceiptGoldenTest.FixturePath рядом.</summary>
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
