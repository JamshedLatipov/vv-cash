# Настраиваемый чек: касса (vv-cash) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Раскладка чека продажи перестаёт быть кодом и становится данными — списком блоков, который приезжает с сервера; касса без шаблона печатает ровно то, что печатала вчера.

**Architecture:** Два новых слоя между шаблоном и принтером. `ReceiptRenderer` разворачивает блоки плюс данные продажи в плоский список `ReceiptOp` (чистая функция: ни байтов, ни сокетов). `EscPosEmitter` превращает операции в байты и **отслеживает состояние принтера** — повторная установка того же выравнивания, жирности или размера команду не порождает. Именно это свойство даёт байт-в-байт совпадение с нынешним чеком и делает возможным замок совместимости. `EscPosPrinterService.BuildSaleReceipt` схлопывается до `Emit(Render(...))`; остальные пять `Build*` не трогаются.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json (полиморфизм через `[JsonPolymorphic]`), SQLite (`Microsoft.Data.Sqlite`), Avalonia DI (`Microsoft.Extensions.DependencyInjection`).

**Спека:** [`2026-09-01-configurable-receipt-template-design.md`](../specs/2026-09-01-configurable-receipt-template-design.md)

**Границы этого плана.** Только репозиторий `vv-cash`. Миграция и валидация на `cloudmarket-server`, конструктор блоков и превью в `bozor` — отдельные планы, каждый в своём репозитории. Этот план даёт работающее ПО сам по себе: касса рендерит чек по шаблону, читает шаблон с сервера, а без шаблона печатает как раньше.

---

## Как запускать тесты

```bash
& ./run-tests.ps1
```

Именно так, с `&` и без `pwsh`: на машине разработчика нет `pwsh`, а шебанг в скрипте на это намекает. Скрипт собирается в `build/verify-tests`, чтобы запущенное приложение не держало залоченным вывод сборки.

Один тест:

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptRendererTest"
```

**Про случайные падения.** Полный прогон иногда роняет произвольный тест гонкой в Avalonia Dispatcher. Прежде чем винить свою правку — прочитайте стектрейс: если он не про ваш файл, перезапустите.

---

## Структура файлов

| Файл | Ответственность |
|---|---|
| `src/VvCash/Models/Receipt/ReceiptAlign.cs` | Перечисление выравнивания |
| `src/VvCash/Models/Receipt/ReceiptBlock.cs` | Базовый класс блока + `[JsonDerivedType]` для всех девяти |
| `src/VvCash/Models/Receipt/Blocks.cs` | Девять классов блоков, по одному полю на настройку |
| `src/VvCash/Models/Receipt/ReceiptTemplate.cs` | Корень шаблона + `Default` + `Parse` |
| `src/VvCash/Services/Rendering/ReceiptOp.cs` | Записи операций печати |
| `src/VvCash/Services/Rendering/ReceiptText.cs` | `Money` / `PadLine` / `Truncate` — переезжают из `EscPosPrinterService` |
| `src/VvCash/Services/Rendering/ReceiptRenderer.cs` | Блоки + данные → операции |
| `src/VvCash/Services/Rendering/EscPosEmitter.cs` | Операции → байты, со слежением за состоянием |
| `src/VvCash/Services/IReceiptTemplateService.cs` | Интерфейс поставщика текущего шаблона |
| `src/VvCash/Services/ReceiptTemplateService.cs` | Реализация поверх кэша |
| `tests/VvCash.Tests/Fixtures/sale-receipt-default.bin` | Замок совместимости: байты нынешнего чека |
| `tests/VvCash.Tests/Fixtures/receipt-golden.json` | Эталон для превью в bozor |

Модифицируются: `EscPosPrinterService.cs`, `CompositePrinterService.cs`, `SyncService.cs`, `OfflineStorageService.cs`, `IOfflineStorageService.cs`, `App.axaml.cs`.

---

## Task 1: Замок совместимости — снять байты нынешнего чека

> **Задача выполнена и по ходу расширена — итог отличается от текста ниже.** Ревью
> вскрыло, что один эталон закрывает только чек, где заполнено всё, а ветки
> ОТСУТСТВИЯ (нулевая скидка, пустые реквизиты, бегунок) и ветки, зависящие от
> ширины ленты, не пришпинены ничем. Фактически сделано: `[Theory]` на четыре
> случая — `default`, `bare`, `queue`, `wide`; эталон читается из исходников через
> `FindRepoRoot()`, а не из копии в `build/verify-tests` (`PreserveNewest` сравнивает
> время, а не содержимое, и давал ложно-зелёный прогон); режим
> `VVCASH_UPDATE_GOLDEN=1` завершается `Assert.Fail`, иначе утёкшая переменная
> молча переписывала эталон под сломанный код. `GoldenItems()` и `BuildGolden()`
> сохранены под прежними именами — на них опирается Task 12.

Это делается **первым**, до единой правки боевого кода. Смысл: зафиксировать сегодняшний чек как эталон, чтобы весь дальнейший рефакторинг проверялся против него, а не против собственных представлений о том, каким чек был.

**Files:**
- Create: `tests/VvCash.Tests/SaleReceiptGoldenTest.cs`
- Create (генерируется на шаге 3): `tests/VvCash.Tests/Fixtures/sale-receipt-default.bin`
- Modify: `tests/VvCash.Tests/VvCash.Tests.csproj`

- [ ] **Step 1: Разрешить тестам читать файлы фикстур**

В `tests/VvCash.Tests/VvCash.Tests.csproj`, внутрь существующего `<ItemGroup>` с пакетами добавьте отдельной группой:

```xml
  <ItemGroup>
    <None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Написать тест-замок**

Создайте `tests/VvCash.Tests/SaleReceiptGoldenTest.cs`:

```csharp
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
```

- [ ] **Step 3: Прогнать тест и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SaleReceiptGoldenTest"`
Expected: FAIL — «Фикстуры нет: …sale-receipt-default.bin».

- [ ] **Step 4: Сгенерировать фикстуру**

```bash
VVCASH_UPDATE_GOLDEN=1 ./run-tests.ps1 --filter "FullyQualifiedName~SaleReceiptGoldenTest"
```

В PowerShell — двумя командами:

```bash
$env:VVCASH_UPDATE_GOLDEN='1'; & ./run-tests.ps1 --filter "FullyQualifiedName~SaleReceiptGoldenTest"; $env:VVCASH_UPDATE_GOLDEN=$null
```

Expected: PASS, и появился файл `tests/VvCash.Tests/Fixtures/sale-receipt-default.bin`.

- [ ] **Step 5: Прогнать тест начисто и увидеть зелёное**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SaleReceiptGoldenTest"`
Expected: PASS. Убедитесь, что переменная окружения снята — иначе тест не сравнивает, а переписывает.

- [ ] **Step 6: Глазами проверить, что в фикстуре осмысленный чек**

```bash
python -c "import sys;d=open('tests/VvCash.Tests/Fixtures/sale-receipt-default.bin','rb').read();print(d.decode('cp866','replace'))"
```

Expected: видны `VV CASH POS`, `Doc #A-42`, `Плитка x53`, `TOTAL:`, `Thank you for shopping!`. Если вместо этого мусор — фикстура снята не с того, и дальше идти нельзя.

- [ ] **Step 7: Commit**

```bash
git add tests/VvCash.Tests/SaleReceiptGoldenTest.cs tests/VvCash.Tests/Fixtures/sale-receipt-default.bin tests/VvCash.Tests/VvCash.Tests.csproj
git commit -m "test(receipt): pin the sale receipt's current bytes as a golden fixture"
```

---

## Task 2: Операции печати и эмиттер

**Files:**
- Create: `src/VvCash/Models/Receipt/ReceiptAlign.cs`
- Create: `src/VvCash/Services/Rendering/ReceiptOp.cs`
- Create: `src/VvCash/Services/Rendering/EscPosEmitter.cs`
- Test: `tests/VvCash.Tests/EscPosEmitterTest.cs`

- [ ] **Step 1: Написать падающие тесты**

Создайте `tests/VvCash.Tests/EscPosEmitterTest.cs`:

```csharp
using System.Linq;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class EscPosEmitterTest
{
    private static readonly byte[] Init = { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17 };

    private static byte[] Emit(params ReceiptOp[] ops) =>
        EscPosEmitter.Emit(ops, EscPosCodePages.Cp866);

    [Fact]
    public void Emit_OpensWithInit_CancelKanji_AndCodeTable()
    {
        // Порядок здесь не вкусовщина: в китайском режиме ESC t принтером
        // игнорируется, поэтому FS . обязан идти до него.
        var bytes = Emit(new TextOp("A"));

        Assert.Equal(EscPosCodePages.Cp866.EscTSelector, bytes[6]);
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74 }, bytes.Take(6).ToArray());
    }

    [Fact]
    public void Emit_WritesTextInTheCodePage_WithATrailingNewline()
    {
        var bytes = Emit(new TextOp("Ok"));

        Assert.Equal(new byte[] { (byte)'O', (byte)'k', 0x0A }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_SkipsAnAlignCommand_WhenTheAlignmentIsAlreadyInEffect()
    {
        // Ради этого свойства эмиттер и ведёт состояние: без него блочная
        // раскладка выдала бы лишнюю ESC a на каждый блок, и байт-в-байт
        // совпадения с нынешним чеком не вышло бы никогда.
        var bytes = Emit(
            new AlignOp(ReceiptAlign.Center), new TextOp("A"),
            new AlignOp(ReceiptAlign.Center), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x61, 0x01 }));
    }

    [Fact]
    public void Emit_EmitsAlignLeft_WhenTheAlignmentActuallyChanges()
    {
        var bytes = Emit(
            new AlignOp(ReceiptAlign.Center), new TextOp("A"),
            new AlignOp(ReceiptAlign.Left), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x61, 0x00 }));
    }

    [Fact]
    public void Emit_TurnsBoldOffOnlyWhenSomethingNonBoldFollows()
    {
        var bytes = Emit(
            new BoldOp(true), new TextOp("A"),
            new BoldOp(false), new TextOp("B"));

        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x01 }));
        Assert.Single(FindAll(bytes, new byte[] { 0x1B, 0x45, 0x00 }));
    }

    [Fact]
    public void Emit_WritesOneLineFeedPerFeedLine()
    {
        var bytes = Emit(new FeedOp(2));

        Assert.Equal(new byte[] { 0x0A, 0x0A }, bytes.Skip(Init.Length).ToArray());
    }

    [Fact]
    public void Emit_WritesTheCutCommand()
    {
        var bytes = Emit(new CutOp());

        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes.Skip(Init.Length).ToArray());
    }

    private static int[] FindAll(byte[] haystack, byte[] needle)
    {
        var hits = new System.Collections.Generic.List<int>();
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];
            if (match) hits.Add(i);
        }
        return hits.ToArray();
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение компиляции**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosEmitterTest"`
Expected: FAIL — сборка не проходит, `ReceiptOp`, `EscPosEmitter`, `ReceiptAlign` не найдены.

- [ ] **Step 3: Завести выравнивание**

Создайте `src/VvCash/Models/Receipt/ReceiptAlign.cs`:

```csharp
namespace VvCash.Models.Receipt;

public enum ReceiptAlign
{
    Left,
    Center,
    Right,
}
```

- [ ] **Step 4: Завести операции**

Создайте `src/VvCash/Services/Rendering/ReceiptOp.cs`:

```csharp
using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Одно действие принтера, ещё не превращённое в байты. Промежуточный
/// слой существует ради двух вещей: раскладка тестируется как текст, а не как
/// байты, и ширина ленты становится параметром одного слоя вместо восьми
/// литералов по коду.</summary>
public abstract record ReceiptOp;

public sealed record TextOp(string Line) : ReceiptOp;

public sealed record AlignOp(ReceiptAlign Align) : ReceiptOp;

public sealed record BoldOp(bool On) : ReceiptOp;

public sealed record DoubleSizeOp(bool On) : ReceiptOp;

public sealed record FeedOp(int Lines) : ReceiptOp;

public sealed record CutOp : ReceiptOp;
```

- [ ] **Step 5: Написать эмиттер**

Создайте `src/VvCash/Services/Rendering/EscPosEmitter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using VvCash.Models;
using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Единственное место, знающее про ESC/POS. Всё, что выше, оперирует
/// операциями и ничего не знает ни про байты, ни про кодовую страницу.
///
/// Эмиттер ведёт состояние принтера и не повторяет команду, которая уже в силе.
/// Это не микрооптимизация: блочная раскладка объявляет выравнивание на каждом
/// блоке, и без слежения чек получал бы ESC a перед каждой строкой — то есть
/// отличался бы от нынешнего байтами, и замок совместимости из Task 1 сойтись
/// не мог бы в принципе.</summary>
public static class EscPosEmitter
{
    private static readonly byte[] CmdInit = { 0x1B, 0x40 };
    private static readonly byte[] CmdCancelKanji = { 0x1C, 0x2E };
    private static readonly byte[] CmdSelectCodeTable = { 0x1B, 0x74 };
    private static readonly byte[] CmdAlignLeft = { 0x1B, 0x61, 0x00 };
    private static readonly byte[] CmdAlignCenter = { 0x1B, 0x61, 0x01 };
    private static readonly byte[] CmdAlignRight = { 0x1B, 0x61, 0x02 };
    private static readonly byte[] CmdBoldOn = { 0x1B, 0x45, 0x01 };
    private static readonly byte[] CmdBoldOff = { 0x1B, 0x45, 0x00 };
    private static readonly byte[] CmdDoubleSizeOn = { 0x1B, 0x21, 0x30 };
    private static readonly byte[] CmdDoubleSizeOff = { 0x1B, 0x21, 0x00 };
    private static readonly byte[] CmdCut = { 0x1D, 0x56, 0x42, 0x00 };

    public static byte[] Emit(IEnumerable<ReceiptOp> ops, EscPosCodePage codePage)
    {
        using var ms = new MemoryStream();

        ms.Write(CmdInit, 0, CmdInit.Length);
        // Строго до ESC t: в китайском режиме выбор таблицы принтером не
        // рассматривается, и порядок здесь — не вкусовщина.
        ms.Write(CmdCancelKanji, 0, CmdCancelKanji.Length);
        ms.Write(CmdSelectCodeTable, 0, CmdSelectCodeTable.Length);
        ms.WriteByte(codePage.EscTSelector);

        var align = ReceiptAlign.Left;
        var bold = false;
        var doubleSize = false;

        foreach (var op in ops)
        {
            switch (op)
            {
                case AlignOp a when a.Align != align:
                    align = a.Align;
                    var cmd = align switch
                    {
                        ReceiptAlign.Center => CmdAlignCenter,
                        ReceiptAlign.Right => CmdAlignRight,
                        _ => CmdAlignLeft,
                    };
                    ms.Write(cmd, 0, cmd.Length);
                    break;

                case AlignOp:
                    break;

                case BoldOp b when b.On != bold:
                    bold = b.On;
                    var boldCmd = bold ? CmdBoldOn : CmdBoldOff;
                    ms.Write(boldCmd, 0, boldCmd.Length);
                    break;

                case BoldOp:
                    break;

                case DoubleSizeOp d when d.On != doubleSize:
                    doubleSize = d.On;
                    var sizeCmd = doubleSize ? CmdDoubleSizeOn : CmdDoubleSizeOff;
                    ms.Write(sizeCmd, 0, sizeCmd.Length);
                    break;

                case DoubleSizeOp:
                    break;

                case TextOp t:
                    var bytes = codePage.Encoding.GetBytes(t.Line + "\n");
                    ms.Write(bytes, 0, bytes.Length);
                    break;

                case FeedOp f:
                    for (var i = 0; i < f.Lines; i++) ms.WriteByte(0x0A);
                    break;

                case CutOp:
                    ms.Write(CmdCut, 0, CmdCut.Length);
                    break;

                default:
                    throw new NotSupportedException($"Неизвестная операция печати: {op.GetType().Name}");
            }
        }

        return ms.ToArray();
    }
}
```

- [ ] **Step 6: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosEmitterTest"`
Expected: PASS, 7 тестов.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/Models/Receipt/ReceiptAlign.cs src/VvCash/Services/Rendering/ReceiptOp.cs src/VvCash/Services/Rendering/EscPosEmitter.cs tests/VvCash.Tests/EscPosEmitterTest.cs
git commit -m "feat(receipt): add a print-op list and an ESC/POS emitter that tracks state"
```

---

## Task 3: Текстовые помощники переезжают в слой рендеринга

`Money`, `PadLine` и `Truncate` сейчас приватные статические внутри `EscPosPrinterService`. Рендереру они нужны, а тащить рендерер внутрь принтера — значит не разделять слои вовсе.

**Files:**
- Create: `src/VvCash/Services/Rendering/ReceiptText.cs`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs:356-369`
- Test: `tests/VvCash.Tests/ReceiptTextTest.cs`

- [ ] **Step 1: Написать падающий тест**

Создайте `tests/VvCash.Tests/ReceiptTextTest.cs`:

```csharp
using System.Globalization;
using System.Threading;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTextTest
{
    [Fact]
    public void Money_UsesADotOnEveryRegister_WhateverTheSystemLocale()
    {
        // Интерполяция с ":F2" брала разделитель из локали ОС, и одна и та же
        // продажа печаталась 20.00 на одной кассе и 20,00 на соседней.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.Equal("20.00", ReceiptText.Money(20m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PadLine_PushesTheRightSideToTheGivenWidth()
    {
        Assert.Equal("A" + new string(' ', 30) + "B", ReceiptText.PadLine("A", "B", 32));
    }

    [Fact]
    public void PadLine_KeepsAtLeastOneSpace_WhenTheSidesDoNotFit()
    {
        // Слипшиеся название и цена нечитаемы; переполнение по ширине принтер
        // перенесёт сам.
        var line = ReceiptText.PadLine(new string('x', 30), new string('y', 30), 32);

        Assert.Equal(new string('x', 30) + " " + new string('y', 30), line);
    }

    [Fact]
    public void Truncate_ClipsToTheWidth()
    {
        Assert.Equal("abc", ReceiptText.Truncate("abcdef", 3));
        Assert.Equal("abc", ReceiptText.Truncate("abc", 32));
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTextTest"`
Expected: FAIL — `ReceiptText` не найден.

- [ ] **Step 3: Создать `ReceiptText`**

Создайте `src/VvCash/Services/Rendering/ReceiptText.cs`:

```csharp
using System;
using System.Globalization;

namespace VvCash.Services.Rendering;

/// <summary>Форматирование строк чека. Public, а не internal: тот же расчёт
/// колонок повторяет превью в бэкофисе, и здесь он единственный источник правды
/// на стороне кассы.</summary>
public static class ReceiptText
{
    /// <summary>Суммы на чеке, одинаково на каждой кассе. Интерполяция с ":F2"
    /// брала разделитель из локали ОС, и одна продажа печаталась 20.00 на одной
    /// кассе и 20,00 на следующей — а CartItem.QuantityDisplay рядом на той же
    /// строке всегда был инвариантным.</summary>
    public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    public static string PadLine(string left, string right, int width)
    {
        var spaces = width - left.Length - right.Length;
        return left + new string(' ', Math.Max(1, spaces)) + right;
    }

    /// <summary>Обрезает подпись по ширине ленты. Название акции — свободный
    /// текст, и длинное перенеслось бы рваной второй строкой.</summary>
    public static string Truncate(string s, int width)
        => s.Length <= width ? s : s.Substring(0, width);
}
```

- [ ] **Step 4: Убрать копии из принтера**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs` удалите три приватных метода `Money`, `PadLine`, `Truncate` (строки 356–369 вместе с их doc-комментариями — комментарии переехали в `ReceiptText`). Добавьте в шапку файла:

```csharp
using VvCash.Services.Rendering;
```

и замените каждый вызов на квалифицированный: `Money(` → `ReceiptText.Money(`, `PadLine(` → `ReceiptText.PadLine(`, `Truncate(` → `ReceiptText.Truncate(`.

Вызовов ровно **21** (посчитано `grep -o`, три определения не в счёт). Часть строк несёт два вызова сразу — например `PadLine("Subtotal:", Money(subtotal), 32)`, — поэтому считать надо вхождения, а не строки:

```bash
grep -o "ReceiptText\." src/VvCash/Services/Hardware/EscPosPrinterService.cs | wc -l
```

Expected: `21`.

Но настоящая проверка не в подсчёте: файл обязан скомпилироваться (пропущенный вызов даст ошибку — приватных методов больше нет), а замок совместимости — остаться зелёным.

- [ ] **Step 5: Прогнать замок и новые тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTextTest|FullyQualifiedName~SaleReceiptGoldenTest|FullyQualifiedName~EscPos"`
Expected: PASS. Замок из Task 1 обязан остаться зелёным — переезд помощников байты менять не должен.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/Rendering/ReceiptText.cs src/VvCash/Services/Hardware/EscPosPrinterService.cs tests/VvCash.Tests/ReceiptTextTest.cs
git commit -m "refactor(receipt): move Money/PadLine/Truncate into the rendering layer"
```

---

## Task 4: Модель шаблона

**Files:**
- Create: `src/VvCash/Models/Receipt/ReceiptBlock.cs`
- Create: `src/VvCash/Models/Receipt/Blocks.cs`
- Create: `src/VvCash/Models/Receipt/ReceiptTemplate.cs`
- Test: `tests/VvCash.Tests/ReceiptTemplateTest.cs`

- [ ] **Step 1: Написать падающие тесты**

Создайте `tests/VvCash.Tests/ReceiptTemplateTest.cs`:

```csharp
using System.Linq;
using VvCash.Models.Receipt;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateTest
{
    [Fact]
    public void Parse_ReadsBlocksByTheirTypeDiscriminator()
    {
        var json = """
        {"version":1,"width":42,"blocks":[
          {"type":"text","content":"Магазин","align":"center","bold":true},
          {"type":"line","char":"=","count":10},
          {"type":"feed","lines":3}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(42, t.Width);
        var text = Assert.IsType<TextBlock>(t.Blocks[0]);
        Assert.Equal("Магазин", text.Content);
        Assert.Equal(ReceiptAlign.Center, text.Align);
        Assert.True(text.Bold);
        var line = Assert.IsType<LineBlock>(t.Blocks[1]);
        Assert.Equal("=", line.Char);
        Assert.Equal(10, line.Count);
        Assert.Equal(3, Assert.IsType<FeedBlock>(t.Blocks[2]).Lines);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnBrokenJson()
    {
        // В configs.val у существующих тенантов может лежать что угодно: опция
        // receiptTemplate засеяна в 2019 и шесть лет рендерилась текстовым полем.
        var t = ReceiptTemplate.Parse("не json вовсе");

        Assert.Same(ReceiptTemplate.Default, t);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnEmptyValue()
    {
        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse(""));
        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse(null));
    }

    [Fact]
    public void Parse_DropsAnUnknownBlockType_AndKeepsTheRest()
    {
        // Касса терпит блок из более новой админки, чем её собственная сборка.
        // Сервер такой type записать не даст, но обновляются они врозь.
        var json = """
        {"version":1,"width":32,"blocks":[
          {"type":"text","content":"A"},
          {"type":"hologram","spin":"fast"},
          {"type":"text","content":"B"}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(2, t.Blocks.Count);
        Assert.Equal(new[] { "A", "B" }, t.Blocks.Cast<TextBlock>().Select(b => b.Content));
    }

    [Fact]
    public void Parse_IgnoresAnUnknownFieldInsideAKnownBlock()
    {
        var t = ReceiptTemplate.Parse("""{"version":1,"blocks":[{"type":"text","content":"A","glitter":true}]}""");

        Assert.Equal("A", Assert.IsType<TextBlock>(t.Blocks[0]).Content);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnAFutureVersion()
    {
        // Несовместимый формат лучше не печатать вовсе, чем печатать наполовину.
        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse("""{"version":99,"blocks":[]}"""));
    }

    [Fact]
    public void Default_IsThirtyTwoColumnsWide()
    {
        Assert.Equal(32, ReceiptTemplate.Default.Width);
    }

    [Fact]
    public void Default_BlocksAreAllEnabled()
    {
        Assert.All(ReceiptTemplate.Default.Blocks, b => Assert.True(b.Enabled));
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTemplateTest"`
Expected: FAIL — сборка не проходит, типов нет.

- [ ] **Step 3: Завести базовый блок и дискриминаторы**

Создайте `src/VvCash/Models/Receipt/ReceiptBlock.cs`:

```csharp
using System.Text.Json.Serialization;

namespace VvCash.Models.Receipt;

/// <summary>Один элемент чека. Порядок задаётся позицией в списке шаблона, а не
/// полем: список и есть порядок, и второй источник правды тут не нужен.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(LineBlock), "line")]
[JsonDerivedType(typeof(FeedBlock), "feed")]
[JsonDerivedType(typeof(FieldsBlock), "fields")]
[JsonDerivedType(typeof(ItemsBlock), "items")]
[JsonDerivedType(typeof(TotalsBlock), "totals")]
[JsonDerivedType(typeof(QrBlock), "qr")]
[JsonDerivedType(typeof(BarcodeBlock), "barcode")]
[JsonDerivedType(typeof(LogoBlock), "logo")]
public abstract class ReceiptBlock
{
    /// <summary>Выключенный блок остаётся в шаблоне и не печатается. Так гасят
    /// строку, не теряя её настройки — то же решение, что PrintRole.None у
    /// принтера против IsEnabled.</summary>
    public bool Enabled { get; set; } = true;

    public ReceiptAlign Align { get; set; } = ReceiptAlign.Left;
}
```

- [ ] **Step 4: Завести девять блоков**

Создайте `src/VvCash/Models/Receipt/Blocks.cs`:

```csharp
using System.Collections.Generic;

namespace VvCash.Models.Receipt;

/// <summary>Строка свободного текста. Подстановки — плоские имена в фигурных
/// скобках, без циклов и условий; цикл по товарам делает ItemsBlock.</summary>
public sealed class TextBlock : ReceiptBlock
{
    public string Content { get; set; } = string.Empty;
    public bool Bold { get; set; }
    public bool DoubleSize { get; set; }
}

/// <summary>Разделитель. Count = 0 означает «во всю ширину ленты»; дефолтный
/// шаблон ставит 28 явно, потому что столько дефисов печатает нынешний чек, а
/// замок совместимости считает байты.</summary>
public sealed class LineBlock : ReceiptBlock
{
    public string Char { get; set; } = "-";
    public int Count { get; set; } = 28;
}

public sealed class FeedBlock : ReceiptBlock
{
    public int Lines { get; set; } = 1;
}

/// <summary>Одно поле реквизитов: что подставить и что написать перед ним.
/// Label — именно префикс, а не подпись с двоеточием от себя: нынешний чек
/// печатает "Doc #A-42" без пробела и дату вовсе без подписи.</summary>
public sealed class ReceiptField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class FieldsBlock : ReceiptBlock
{
    public List<ReceiptField> Fields { get; set; } = new();
}

public sealed class ItemsBlock : ReceiptBlock
{
    public bool ShowUnitPrice { get; set; }
    public bool ShowSku { get; set; }
    public bool ShowBarcode { get; set; }
    public bool ShowSecondaryUnit { get; set; } = true;
    public bool ShowLineDiscount { get; set; }
}

public sealed class TotalsBlock : ReceiptBlock
{
    public bool ShowSubtotal { get; set; } = true;
    public string SubtotalLabel { get; set; } = "Subtotal:";
    public bool ShowDiscount { get; set; } = true;
    public string DiscountLabel { get; set; } = "Discount:";
    public bool ShowDiscountName { get; set; } = true;
    public string TotalLabel { get; set; } = "TOTAL:";
    public bool BoldTotal { get; set; } = true;
}

public sealed class QrBlock : ReceiptBlock
{
    public string Data { get; set; } = string.Empty;
    public int ModuleSize { get; set; } = 6;
}

public enum BarcodeSymbology
{
    Code128,
    Ean13,
}

public sealed class BarcodeBlock : ReceiptBlock
{
    public string Data { get; set; } = string.Empty;
    public BarcodeSymbology Symbology { get; set; } = BarcodeSymbology.Code128;
    public int Height { get; set; } = 64;
    public bool PrintHri { get; set; } = true;
}

public enum LogoSource
{
    /// <summary>Логотип уже прошит в память принтера; чек печатает слот.</summary>
    Nv,
    /// <summary>Растр приезжает отдельной опцией конфига receipt_logo.</summary>
    Bitmap,
}

public sealed class LogoBlock : ReceiptBlock
{
    public LogoSource Source { get; set; } = LogoSource.Nv;
    public int NvSlot { get; set; } = 1;
}
```

- [ ] **Step 5: Завести корень шаблона с разбором и дефолтом**

Создайте `src/VvCash/Models/Receipt/ReceiptTemplate.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VvCash.Models.Receipt;

public sealed class ReceiptTemplate
{
    /// <summary>Формат самого шаблона, не его содержимого. Чужая версия —
    /// повод не печатать по нему вовсе: половина незнакомого формата хуже
    /// знакомого дефолта.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Колонок ленты: 32 на 58 мм, 42–48 на 80 мм.</summary>
    public int Width { get; set; } = 32;

    public List<ReceiptBlock> Blocks { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Разбирает значение опции receipt_template. Любая беда — пустое
    /// значение, не-JSON, чужая версия — читается как «шаблона нет», и касса
    /// печатает дефолт. Бросать нельзя: значение приходит с сервера и из кэша,
    /// а чек обязан выйти.
    ///
    /// Незнакомый type блока выбрасывается, а остальные печатаются: касса и
    /// админка обновляются врозь, и блок из более новой админки не повод
    /// потерять весь чек.</summary>
    public static ReceiptTemplate Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Default;

        try
        {
            var node = JsonNode.Parse(raw!)?.AsObject();
            if (node == null) return Default;

            var version = node["version"]?.GetValue<int>() ?? CurrentVersion;
            if (version != CurrentVersion) return Default;

            var kept = new JsonArray();
            foreach (var block in node["blocks"]?.AsArray() ?? new JsonArray())
            {
                var type = block?["type"]?.GetValue<string>();
                if (type != null && KnownTypes.Contains(type))
                    kept.Add(block!.DeepClone());
            }
            node["blocks"] = kept;

            return JsonSerializer.Deserialize<ReceiptTemplate>(node.ToJsonString(), Options) ?? Default;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            Console.WriteLine($"[ReceiptTemplate] значение не разобрано, печатаю дефолт: {ex.Message}");
            return Default;
        }
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "text", "line", "feed", "fields", "items", "totals", "qr", "barcode", "logo",
    };

    /// <summary>Ровно нынешняя раскладка, переписанная блок в блок с
    /// EscPosPrinterService.BuildSaleReceipt. Разделители — 28 дефисов, не по
    /// ширине ленты, потому что столько печатает сегодняшний чек.</summary>
    public static ReceiptTemplate Default { get; } = new()
    {
        Version = CurrentVersion,
        Width = 32,
        Blocks = new List<ReceiptBlock>
        {
            new TextBlock { Content = "VV CASH POS", Align = ReceiptAlign.Center, DoubleSize = true },
            new TextBlock { Content = "# {queue}", Align = ReceiptAlign.Center, Bold = true, DoubleSize = true },
            new FieldsBlock
            {
                Align = ReceiptAlign.Center,
                Fields = new List<ReceiptField>
                {
                    new() { Key = "doc", Label = "Doc #" },
                    new() { Key = "date", Label = "" },
                    new() { Key = "warehouse", Label = "Whse: " },
                    new() { Key = "seller", Label = "Seller: " },
                },
            },
            new LineBlock { Align = ReceiptAlign.Center },
            new ItemsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left },
            new TotalsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left },
            new TextBlock { Content = "Thank you for shopping!", Align = ReceiptAlign.Center },
            new FeedBlock { Lines = 2, Align = ReceiptAlign.Center },
        },
    };
}
```

- [ ] **Step 6: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTemplateTest"`
Expected: PASS, 8 тестов.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/Models/Receipt/ tests/VvCash.Tests/ReceiptTemplateTest.cs
git commit -m "feat(receipt): add the block template model with tolerant parsing"
```

---

## Task 5: Рендерер — блоки в операции

**Files:**
- Create: `src/VvCash/Services/Rendering/ReceiptRenderer.cs`
- Test: `tests/VvCash.Tests/ReceiptRendererTest.cs`

Правило пустой подстановки, которое реализуется здесь: **строка, в которой хоть одна подстановка разрешилась в пустое значение, не печатается целиком.** Это ровно то, что делает нынешний чек четырьмя `if (!string.IsNullOrWhiteSpace(...))` — офлайновая продажа без номера документа не должна нести пустую строку. Литерал без подстановок печатается всегда.

- [ ] **Step 1: Написать падающие тесты**

Создайте `tests/VvCash.Tests/ReceiptRendererTest.cs`:

```csharp
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
        Assert.Equal(new[] { new string('-', 28) }, Lines(One(new LineBlock()), Sale(Glue())));
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
        Assert.Equal("Discount:" + new string(' ', 16) + "-50.00", lines[1]);
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

        var boldOn = ops.ToList().FindIndex(o => o is BoldOp { On: true });
        var boldOff = ops.ToList().FindIndex(o => o is BoldOp { On: false });
        Assert.True(boldOn >= 0 && boldOff > boldOn);
    }

    [Fact]
    public void Render_ClosesTheDocumentWithACut()
    {
        var ops = ReceiptRenderer.Render(One(new TextBlock { Content = "A" }), Sale(Glue()));

        Assert.IsType<CutOp>(ops[^1]);
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptRendererTest"`
Expected: FAIL — `ReceiptRenderer` не найден.

- [ ] **Step 3: Написать рендерер**

Создайте `src/VvCash/Services/Rendering/ReceiptRenderer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using VvCash.Models;
using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Шаблон плюс данные продажи → плоский список операций. Чистая
/// функция: ни байтов, ни кодовой страницы, ни сокетов. Всё, что знает про
/// раскладку, живёт здесь и только здесь.</summary>
public static class ReceiptRenderer
{
    private static readonly Regex Placeholder = new(@"\{([a-zA-Z]+)\}", RegexOptions.Compiled);

    public static IReadOnlyList<ReceiptOp> Render(ReceiptTemplate template, SaleReceiptData sale)
    {
        var ops = new List<ReceiptOp>();
        var values = Values(sale);

        foreach (var block in template.Blocks)
        {
            if (!block.Enabled) continue;
            RenderBlock(block, template, sale, values, ops);
        }

        ops.Add(new CutOp());
        return ops;
    }

    private static void RenderBlock(ReceiptBlock block, ReceiptTemplate template, SaleReceiptData sale,
        IReadOnlyDictionary<string, string> values, List<ReceiptOp> ops)
    {
        switch (block)
        {
            case TextBlock t:
                if (!TrySubstitute(t.Content, values, out var line)) return;
                ops.Add(new AlignOp(t.Align));
                ops.Add(new BoldOp(t.Bold));
                ops.Add(new DoubleSizeOp(t.DoubleSize));
                ops.Add(new TextOp(line));
                break;

            case LineBlock l:
                var count = l.Count > 0 ? l.Count : template.Width;
                var ch = string.IsNullOrEmpty(l.Char) ? "-" : l.Char.Substring(0, 1);
                ops.Add(new AlignOp(l.Align));
                ops.Add(new BoldOp(false));
                ops.Add(new DoubleSizeOp(false));
                ops.Add(new TextOp(string.Concat(System.Linq.Enumerable.Repeat(ch, count))));
                break;

            case FeedBlock f:
                ops.Add(new AlignOp(f.Align));
                ops.Add(new FeedOp(f.Lines));
                break;

            case FieldsBlock fields:
                ops.Add(new AlignOp(fields.Align));
                ops.Add(new BoldOp(false));
                ops.Add(new DoubleSizeOp(false));
                foreach (var field in fields.Fields)
                {
                    if (!values.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value))
                        continue;
                    ops.Add(new TextOp(field.Label + value));
                }
                break;

            case ItemsBlock items:
                ops.Add(new AlignOp(items.Align));
                ops.Add(new BoldOp(false));
                ops.Add(new DoubleSizeOp(false));
                foreach (var item in sale.Items) RenderItem(item, items, template.Width, ops);
                break;

            case TotalsBlock totals:
                RenderTotals(totals, sale, template.Width, ops);
                break;

            default:
                // QR, штрихкод и логотип подключаются в Task 8. До тех пор блок
                // просто не печатается — это лучше, чем падение на чеке.
                break;
        }
    }

    private static void RenderItem(CartItem item, ItemsBlock cfg, int width, List<ReceiptOp> ops)
    {
        ops.Add(new TextOp(ReceiptText.PadLine(
            $"{item.Product.Name} x{item.QuantityDisplay}",
            ReceiptText.Money(item.LineTotal),
            width)));

        if (cfg.ShowUnitPrice)
            ops.Add(new TextOp($"    {item.QuantityDisplay} x {ReceiptText.Money(item.Product.Price)}"));

        if (cfg.ShowSku && !string.IsNullOrWhiteSpace(item.Product.Sku))
            ops.Add(new TextOp($"    {item.Product.Sku}"));

        if (cfg.ShowBarcode && !string.IsNullOrWhiteSpace(item.Product.Barcode))
            ops.Add(new TextOp($"    {item.Product.Barcode}"));

        if (cfg.ShowSecondaryUnit && item.Product.HasSecondaryUnit)
            ops.Add(new TextOp($"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}"));
    }

    private static void RenderTotals(TotalsBlock cfg, SaleReceiptData sale, int width, List<ReceiptOp> ops)
    {
        ops.Add(new AlignOp(cfg.Align));
        ops.Add(new DoubleSizeOp(false));
        ops.Add(new BoldOp(false));

        if (cfg.ShowSubtotal)
            ops.Add(new TextOp(ReceiptText.PadLine(cfg.SubtotalLabel, ReceiptText.Money(sale.Subtotal), width)));

        if (cfg.ShowDiscount && sale.Discount > 0)
        {
            ops.Add(new TextOp(ReceiptText.PadLine(cfg.DiscountLabel, $"-{ReceiptText.Money(sale.Discount)}", width)));
            if (cfg.ShowDiscountName && !string.IsNullOrWhiteSpace(sale.DiscountName))
                ops.Add(new TextOp(ReceiptText.Truncate(sale.DiscountName!, width)));
        }

        ops.Add(new BoldOp(cfg.BoldTotal));
        ops.Add(new TextOp(ReceiptText.PadLine(cfg.TotalLabel, ReceiptText.Money(sale.Total), width)));
        ops.Add(new BoldOp(false));
    }

    private static Dictionary<string, string> Values(SaleReceiptData sale) => new(StringComparer.Ordinal)
    {
        ["doc"] = sale.DocumentNumber ?? string.Empty,
        ["date"] = sale.SaleDate ?? string.Empty,
        ["warehouse"] = sale.WarehouseName ?? string.Empty,
        ["seller"] = sale.SellerName ?? string.Empty,
        ["queue"] = sale.QueueNumber ?? string.Empty,
        ["subtotal"] = ReceiptText.Money(sale.Subtotal),
        ["discount"] = ReceiptText.Money(sale.Discount),
        ["total"] = ReceiptText.Money(sale.Total),
        ["discountName"] = sale.DiscountName ?? string.Empty,
    };

    /// <summary>Подставляет значения. Возвращает false, если хоть одна известная
    /// подстановка пуста — тогда строка не печатается вовсе. Это то же, что
    /// делают четыре if в нынешнем BuildSaleReceipt: у офлайновой продажи нет
    /// номера, и пустая строка вместо него — мусор, а не информация.
    ///
    /// Незнакомое имя не считается пустым и остаётся на бумаге как есть: {tota}
    /// сразу показывает, где опечатка в бэкофисе.</summary>
    private static bool TrySubstitute(string content, IReadOnlyDictionary<string, string> values, out string result)
    {
        var dropped = false;

        result = Placeholder.Replace(content, m =>
        {
            var key = m.Groups[1].Value;
            if (!values.TryGetValue(key, out var value)) return m.Value;
            if (string.IsNullOrWhiteSpace(value)) dropped = true;
            return value;
        });

        return !dropped;
    }
}
```

- [ ] **Step 4: Добавить `QueueNumber` в `SaleReceiptData`**

Рендереру нужен номер бегунка тем же способом, что и остальные реквизиты. В `src/VvCash/Models/SaleReceiptData.cs` допишите последним параметром записи:

```csharp
    string? SaleDate = null,
    /// <summary>Номер бегунка на кухню. Пусто на клиентском чеке — блок с
    /// подстановкой {queue} тогда не печатается, ровно как решено спекой.</summary>
    string? QueueNumber = null);
```

- [ ] **Step 5: Прогнать тесты рендерера**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptRendererTest"`
Expected: PASS, 15 тестов.

Если падает `Items_AddAUnitPriceLine_WhenEnabled` или тесты про Sku/Barcode — проверьте, как называются свойства в `src/VvCash/Models/Product.cs`, и поправьте обращения в рендерере под фактические имена.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/Rendering/ReceiptRenderer.cs src/VvCash/Models/SaleReceiptData.cs tests/VvCash.Tests/ReceiptRendererTest.cs
git commit -m "feat(receipt): render blocks into print ops"
```

---

## Task 6: Перевести чек продажи на новый путь

Момент истины: `BuildSaleReceipt` начинает ходить через рендерер, а замок из Task 1 обязан остаться зелёным.

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs:97-167`
- Test: `tests/VvCash.Tests/SaleReceiptGoldenTest.cs` (без правок — он и есть проверка)

- [ ] **Step 1: Заменить тело `BuildSaleReceipt`**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs` замените весь метод `BuildSaleReceipt` (строки 97–167, вместе с его doc-комментарием) на:

```csharp
    /// <summary>Собирает байты чека продажи по шаблону. Раскладка живёт в
    /// ReceiptRenderer, байты — в EscPosEmitter; здесь остался только стык.
    ///
    /// template = null означает «шаблон с сервера не доехал» и берёт
    /// ReceiptTemplate.Default, который печатает ровно то, что печаталось до
    /// перевода на блоки. Это свойство закреплено SaleReceiptGoldenTest.</summary>
    public static byte[] BuildSaleReceipt(
        EscPosCodePage codePage,
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null,
        string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null,
        string? queueNumber = null,
        ReceiptTemplate? template = null)
    {
        var sale = new SaleReceiptData(
            new List<CartItem>(items), subtotal, discount, total,
            discountName, documentNumber, warehouseName, sellerName, saleDate, queueNumber);

        return EscPosEmitter.Emit(
            ReceiptRenderer.Render(template ?? ReceiptTemplate.Default, sale),
            codePage);
    }
```

Добавьте в шапку файла:

```csharp
using VvCash.Models.Receipt;
```

- [ ] **Step 2: Прогнать замок совместимости**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SaleReceiptGoldenTest"`
Expected: PASS.

**Если упал — не трогайте фикстуры.** Они и есть эталон. Сообщение о падении само показывает построчный дифф декодированного чека с управляющими байтами в виде `<XX>` — читайте его, копать байты руками не нужно.

Падений будет от одного до четырёх: каждый случай `[Theory]` охраняет свою ветку раскладки, и по тому, КАКИЕ из них упали, сразу видно, где сломано:

| упал случай | где искать |
|---|---|
| `default` | общая раскладка: порядок блоков, разделители, реквизиты |
| `bare` | ветки отсутствия: нулевая скидка, пустые реквизиты |
| `queue` | блок с номером бегунка |
| `wide` | ширина: `PadLine` при переполнении, `Truncate` названия акции |

Типичные причины: в `Default` не 28 дефисов; порядок блоков разошёлся с нынешним методом; эмиттер выдаёт лишнюю команду выравнивания, потому что где-то потерялось слежение за состоянием.

- [ ] **Step 3: Прогнать весь набор тестов ESC/POS**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~EscPos"`
Expected: PASS. `EscPosUnitTest` проверяет содержимое чека продажи текстом — он тоже обязан остаться зелёным.

- [ ] **Step 4: Прогнать всё**

Run: `& ./run-tests.ps1`
Expected: PASS. Про случайное падение по гонке Avalonia Dispatcher — см. раздел «Как запускать тесты».

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs
git commit -m "refactor(receipt): build the sale receipt through the template renderer"
```

---

## Task 7: Шаблон доезжает до принтера

Шаблон приходит с сервера в произвольный момент, а не по `SettingsChanged`. Поэтому принтер получает **поставщика**, а не значение: состав принтеров не надо пересобирать из-за нового шаблона.

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs:86-95`
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs:44-53`
- Test: `tests/VvCash.Tests/ReceiptTemplateWiringTest.cs`

- [ ] **Step 1: Написать падающий тест**

Создайте `tests/VvCash.Tests/ReceiptTemplateWiringTest.cs`:

```csharp
using System.Collections.Generic;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateWiringTest
{
    [Fact]
    public void Printer_ReadsTheTemplateAtPrintTime_NotAtConstruction()
    {
        // Шаблон приезжает синхронизацией в произвольный момент. Читай принтер
        // его в конструкторе — новый шаблон дожидался бы перезапуска кассы или
        // пересборки состава принтеров по SettingsChanged.
        var current = ReceiptTemplate.Default;
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, "127.0.0.1:9100",
            EscPosCodePages.Cp866, PrintRole.Receipt, () => current);

        current = new ReceiptTemplate
        {
            Width = 32,
            Blocks = new List<ReceiptBlock> { new TextBlock { Content = "НОВЫЙ ШАБЛОН" } },
        };

        var text = EscPosCodePages.Cp866.Encoding.GetString(printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m));

        Assert.Contains("НОВЫЙ ШАБЛОН", text);
    }

    [Fact]
    public void Printer_FallsBackToTheDefaultTemplate_WhenNoProviderWasGiven()
    {
        var printer = new EscPosPrinterService(
            PrinterConnectionType.LAN, "127.0.0.1:9100", EscPosCodePages.Cp866);

        var text = EscPosCodePages.Cp866.Encoding.GetString(printer.BuildConfiguredSaleReceipt(
            new List<CartItem>(), subtotal: 0m, discount: 0m, total: 0m));

        Assert.Contains("VV CASH POS", text);
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTemplateWiringTest"`
Expected: FAIL — у конструктора нет пятого параметра, метода `BuildConfiguredSaleReceipt` нет.

- [ ] **Step 3: Принять поставщика шаблона в конструкторе**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs` добавьте поле рядом с остальными:

```csharp
    private readonly Func<ReceiptTemplate> _template;
```

и замените конструктор:

```csharp
    /// <param name="template">Поставщик, а не значение: шаблон приезжает
    /// синхронизацией в произвольный момент, и читать его надо в момент печати.
    /// Иначе новый шаблон ждал бы перезапуска кассы. Null — печатать дефолтом;
    /// служба, собранная на экране настроек ради пробной печати, шаблон не
    /// использует вовсе.</param>
    public EscPosPrinterService(PrinterConnectionType connectionType, string connectionString,
        EscPosCodePage codePage, PrintRole roles = PrintRole.Receipt,
        Func<ReceiptTemplate>? template = null)
    {
        _connectionType = connectionType;
        _connectionString = connectionString;
        _codePage = codePage;
        _roles = roles;
        _template = template ?? (() => ReceiptTemplate.Default);
    }

    /// <summary>Чек этого принтера, собранный по действующему шаблону. Экземплярный
    /// в отличие от статического BuildSaleReceipt: шаблон — свойство принтера, а
    /// не аргумента вызова.</summary>
    public byte[] BuildConfiguredSaleReceipt(IEnumerable<CartItem> items,
        decimal subtotal, decimal discount, decimal total,
        string? discountName = null, string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null, string? queueNumber = null)
        => BuildSaleReceipt(_codePage, items, subtotal, discount, total, discountName,
            documentNumber, warehouseName, sellerName, saleDate, queueNumber, _template());
```

- [ ] **Step 4: Перевести оба метода печати на новый сбор**

В том же файле, в `PrintReceiptAsync` замените строку сбора байтов на:

```csharp
            await SendAsync(BuildConfiguredSaleReceipt(items, subtotal, discount, total, discountName,
                documentNumber, warehouseName, sellerName, saleDate));
```

и в `PrintKitchenOrderAsync`:

```csharp
            await SendAsync(BuildConfiguredSaleReceipt(sale.Items, sale.Subtotal, sale.Discount, sale.Total,
                sale.DiscountName, sale.DocumentNumber, sale.WarehouseName, sale.SellerName, sale.SaleDate,
                queueNumber));
```

- [ ] **Step 5: Пробросить поставщика через состав принтеров**

В `src/VvCash/Services/Hardware/CompositePrinterService.cs` замените конструктор:

```csharp
    /// <summary>Фабрика существует ради проверки маршрутизации: без неё состав
    /// принтеров создаётся внутри и подменить его нечем. По умолчанию — обычное
    /// создание, боевой путь тот же, что был.</summary>
    /// <param name="template">Поставщик шаблона чека. Отдаётся каждому принтеру
    /// как есть — читается он в момент печати, поэтому смена шаблона состава не
    /// пересобирает.</param>
    public CompositePrinterService(ISettingsService settingsService,
        Func<PrinterConfig, EscPosPrinterService>? printerFactory = null,
        Func<ReceiptTemplate>? template = null)
    {
        _settingsService = settingsService;
        _factory = printerFactory ?? (config => new EscPosPrinterService(
            config.ConnectionType, config.ConnectionString,
            EscPosCodePages.Resolve(config.CodePageId), config.Roles, template));
        _settingsService.SettingsChanged += OnSettingsChanged;
        InitializePrinters();
    }
```

Добавьте в шапку файла:

```csharp
using VvCash.Models.Receipt;
```

- [ ] **Step 6: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTemplateWiringTest|FullyQualifiedName~CompositePrinterServiceTest|FullyQualifiedName~PrinterRoutingTest|FullyQualifiedName~SaleReceiptGoldenTest"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs src/VvCash/Services/Hardware/CompositePrinterService.cs tests/VvCash.Tests/ReceiptTemplateWiringTest.cs
git commit -m "feat(receipt): let each printer read the live template at print time"
```

---

## Task 8: QR, штрихкод и логотип

**Files:**
- Modify: `src/VvCash/Services/Rendering/ReceiptOp.cs`
- Modify: `src/VvCash/Services/Rendering/EscPosEmitter.cs`
- Modify: `src/VvCash/Services/Rendering/ReceiptRenderer.cs`
- Test: `tests/VvCash.Tests/EscPosGraphicsTest.cs`

- [ ] **Step 1: Написать падающие тесты**

Создайте `tests/VvCash.Tests/EscPosGraphicsTest.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using VvCash.Models;
using VvCash.Models.Receipt;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class EscPosGraphicsTest
{
    private static byte[] Emit(params ReceiptOp[] ops) =>
        EscPosEmitter.Emit(ops, EscPosCodePages.Cp866);

    [Fact]
    public void Qr_SelectsModel2_SetsTheModuleSize_StoresTheData_ThenPrints()
    {
        var bytes = Emit(new QrOp("A-42", ModuleSize: 6));

        // GS ( k, функция 165 — модель; 167 — размер модуля; 180 — печать.
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 }));
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 6 }));
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 }));
        Assert.True(Contains(bytes, System.Text.Encoding.ASCII.GetBytes("A-42")));
    }

    [Fact]
    public void Barcode_SetsHeightAndHri_ThenPrintsCode128()
    {
        var bytes = Emit(new BarcodeOp("12345678", BarcodeSymbology.Code128, Height: 64, PrintHri: true));

        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x68, 64 }));       // GS h — высота
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x48, 0x02 }));      // GS H — подпись снизу
        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x6B, 73 }));        // GS k m=73 — Code128
    }

    [Fact]
    public void NvLogo_PrintsTheSlot()
    {
        var bytes = Emit(new NvLogoOp(Slot: 2));

        Assert.True(Contains(bytes, new byte[] { 0x1C, 0x70, 2, 0 }));      // FS p n m
    }

    [Fact]
    public void Bitmap_WritesTheRasterHeaderWithWidthInBytes()
    {
        // GS v 0: ширина задаётся в БАЙТАХ, высота — в точках. Перепутать их
        // местами — получить на бумаге кашу вместо логотипа.
        var raster = new byte[6];                                            // 48 точек × 1 строка
        var bytes = Emit(new BitmapOp(raster, WidthBytes: 6, Height: 1));

        Assert.True(Contains(bytes, new byte[] { 0x1D, 0x76, 0x30, 0x00, 6, 0, 1, 0 }));
    }

    [Fact]
    public void Renderer_EmitsAQrOp_ForAQrBlock_WithSubstitution()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new QrBlock { Data = "{doc}", ModuleSize = 8 } },
        };
        var sale = new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m, DocumentNumber: "A-7");

        var qr = ReceiptRenderer.Render(t, sale).OfType<QrOp>().Single();

        Assert.Equal("A-7", qr.Data);
        Assert.Equal(8, qr.ModuleSize);
    }

    [Fact]
    public void Renderer_DropsAQrBlock_WhenItsDataResolvesEmpty()
    {
        // Офлайновая продажа без номера: пустой QR печатать незачем.
        var t = new ReceiptTemplate { Blocks = new List<ReceiptBlock> { new QrBlock { Data = "{doc}" } } };
        var sale = new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m, DocumentNumber: "");

        Assert.Empty(ReceiptRenderer.Render(t, sale).OfType<QrOp>());
    }

    [Fact]
    public void Renderer_EmitsAnNvLogoOp_ForAnNvLogoBlock()
    {
        var t = new ReceiptTemplate
        {
            Blocks = new List<ReceiptBlock> { new LogoBlock { Source = LogoSource.Nv, NvSlot = 3 } },
        };

        var logo = ReceiptRenderer.Render(t, new SaleReceiptData(new List<CartItem>(), 0m, 0m, 0m))
            .OfType<NvLogoOp>().Single();

        Assert.Equal(3, logo.Slot);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];
            if (match) return true;
        }
        return false;
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosGraphicsTest"`
Expected: FAIL — `QrOp`, `BarcodeOp`, `NvLogoOp`, `BitmapOp` не найдены.

- [ ] **Step 3: Добавить операции**

В конец `src/VvCash/Services/Rendering/ReceiptOp.cs`:

```csharp
public sealed record QrOp(string Data, int ModuleSize) : ReceiptOp;

public sealed record BarcodeOp(string Data, BarcodeSymbology Symbology, int Height, bool PrintHri) : ReceiptOp;

/// <summary>Логотип, уже прошитый в память принтера. Байтов на ленте — четыре.</summary>
public sealed record NvLogoOp(int Slot) : ReceiptOp;

/// <summary>Растр, приехавший с сервера уже сведённым в один бит. Ширина здесь
/// в БАЙТАХ, высота в точках — так требует GS v 0, и путать их нельзя.</summary>
public sealed record BitmapOp(byte[] Raster, int WidthBytes, int Height) : ReceiptOp;
```

- [ ] **Step 4: Научить эмиттер четырём новым командам**

В `src/VvCash/Services/Rendering/EscPosEmitter.cs` добавьте ветки в `switch` перед `default:`:

```csharp
                case QrOp qr:
                    WriteQr(ms, qr, codePage);
                    break;

                case BarcodeOp bc:
                    WriteBarcode(ms, bc);
                    break;

                case NvLogoOp nv:
                    ms.Write(new byte[] { 0x1C, 0x70, (byte)nv.Slot, 0 }, 0, 4);
                    break;

                case BitmapOp bmp:
                    ms.Write(new byte[]
                    {
                        0x1D, 0x76, 0x30, 0x00,
                        (byte)(bmp.WidthBytes & 0xFF), (byte)(bmp.WidthBytes >> 8),
                        (byte)(bmp.Height & 0xFF), (byte)(bmp.Height >> 8),
                    }, 0, 8);
                    ms.Write(bmp.Raster, 0, bmp.Raster.Length);
                    break;
```

и методы в конец класса:

```csharp
    /// <summary>GS ( k тремя вызовами: выбрать модель, задать размер модуля,
    /// сложить данные в буфер символа, напечатать. Порядок обязателен — печать
    /// берёт то, что лежит в буфере на её момент.</summary>
    private static void WriteQr(MemoryStream ms, QrOp qr, EscPosCodePage codePage)
    {
        // Функция 165: модель 2 (0x32) — её понимают все ходовые аппараты.
        ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 }, 0, 9);
        // Функция 167: размер модуля в точках.
        ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)qr.ModuleSize }, 0, 8);

        var data = codePage.Encoding.GetBytes(qr.Data);
        var len = data.Length + 3;
        // Функция 180: сложить данные. pL/pH считают ТРИ служебных байта следом,
        // а не только полезную нагрузку — отсюда +3.
        ms.Write(new byte[] { 0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30 }, 0, 8);
        ms.Write(data, 0, data.Length);
        // Функция 181: напечатать то, что в буфере.
        ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 }, 0, 8);
    }

    private static void WriteBarcode(MemoryStream ms, BarcodeOp bc)
    {
        ms.Write(new byte[] { 0x1D, 0x68, (byte)bc.Height }, 0, 3);              // GS h — высота
        ms.Write(new byte[] { 0x1D, 0x48, bc.PrintHri ? (byte)0x02 : (byte)0x00 }, 0, 3); // GS H — подпись снизу

        // m ≥ 65 — форма с длиной вместо NUL-терминатора: она принимает данные с
        // любым байтом внутри, включая ноль, и не зависит от терминатора.
        var m = bc.Symbology == BarcodeSymbology.Ean13 ? (byte)67 : (byte)73;
        var data = System.Text.Encoding.ASCII.GetBytes(bc.Data);
        ms.Write(new byte[] { 0x1D, 0x6B, m, (byte)data.Length }, 0, 4);
        ms.Write(data, 0, data.Length);
    }
```

Добавьте в шапку `using VvCash.Models.Receipt;` — там живёт `BarcodeSymbology`.

- [ ] **Step 5: Научить рендерер трём новым блокам**

В `src/VvCash/Services/Rendering/ReceiptRenderer.cs` замените ветку `default:` в `RenderBlock` на:

```csharp
            case QrBlock qr:
                if (!TrySubstitute(qr.Data, values, out var qrData)) return;
                ops.Add(new AlignOp(qr.Align));
                ops.Add(new QrOp(qrData, qr.ModuleSize));
                break;

            case BarcodeBlock bc:
                if (!TrySubstitute(bc.Data, values, out var bcData)) return;
                ops.Add(new AlignOp(bc.Align));
                ops.Add(new BarcodeOp(bcData, bc.Symbology, bc.Height, bc.PrintHri));
                break;

            case LogoBlock logo:
                ops.Add(new AlignOp(logo.Align));
                if (logo.Source == LogoSource.Nv)
                    ops.Add(new NvLogoOp(logo.NvSlot));
                // Растровый логотип подключается в Task 9 вместе с опцией
                // receipt_logo: без её содержимого печатать нечего.
                break;

            default:
                break;
```

- [ ] **Step 6: Прогнать тесты**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosGraphicsTest|FullyQualifiedName~SaleReceiptGoldenTest"`
Expected: PASS. Замок обязан остаться зелёным — дефолтный шаблон графики не содержит.

- [ ] **Step 7: Проверить на живом принтере**

Поддержка QR и штрихкодов разнится по моделям, и байт-тесты этого не показывают. Соберите шаблон с блоком `qr`, положите его в `settings.json` кассы или скормите через пробную печать и напечатайте на том аппарате, что стоит на точке. Если QR не выходит — смотрите документацию конкретной модели на `GS ( k`; ветка `WriteQr` меняется, тест меняется вместе с ней.

- [ ] **Step 8: Commit**

```bash
git add src/VvCash/Services/Rendering/ tests/VvCash.Tests/EscPosGraphicsTest.cs
git commit -m "feat(receipt): add QR, barcode and logo print ops"
```

---

## Task 9: Кэш шаблона и логотипа в SQLite

**Files:**
- Modify: `src/VvCash/Services/Data/IOfflineStorageService.cs`
- Modify: `src/VvCash/Services/Data/OfflineStorageService.cs`
- Test: `tests/VvCash.Tests/ReceiptTemplateStorageTest.cs`

- [ ] **Step 1: Найти, как устроен существующий кэш**

```bash
grep -n "CashFeatures" src/VvCash/Services/Data/OfflineStorageService.cs src/VvCash/Services/Data/IOfflineStorageService.cs
```

Читайте `SaveCashFeaturesAsync` / `GetCashFeaturesAsync` — новые методы делаются по их образцу, включая правило «битый кэш не мешает кассе открыться».

- [ ] **Step 2: Написать падающий тест**

Создайте `tests/VvCash.Tests/ReceiptTemplateStorageTest.cs`. Посмотрите, как соседний `OfflineStorageServiceTest` заводит временную базу, и повторите этот способ:

```bash
grep -n "new OfflineStorageService" -B6 tests/VvCash.Tests/OfflineStorageServiceTest.cs | head -20
```

```csharp
using System.IO;
using System.Threading.Tasks;
using VvCash.Models.Receipt;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateStorageTest
{
    [Fact]
    public async Task RawTemplate_RoundTrips()
    {
        var storage = await NewStorage();
        var json = """{"version":1,"width":42,"blocks":[]}""";

        await storage.SaveReceiptTemplateAsync(json);

        Assert.Equal(json, await storage.GetReceiptTemplateAsync());
    }

    [Fact]
    public async Task RawTemplate_IsEmpty_WhenNothingWasEverSynced()
    {
        var storage = await NewStorage();

        Assert.True(string.IsNullOrEmpty(await storage.GetReceiptTemplateAsync()));
    }

    [Fact]
    public async Task ACorruptCachedTemplate_ParsesToTheDefault_RatherThanThrowing()
    {
        // Опция receiptTemplate засеяна в 2019 и шесть лет рендерилась текстовым
        // полем — в configs.val у живого тенанта может лежать что угодно.
        var storage = await NewStorage();
        await storage.SaveReceiptTemplateAsync("{это не json");

        Assert.Same(ReceiptTemplate.Default, ReceiptTemplate.Parse(await storage.GetReceiptTemplateAsync()));
    }

    [Fact]
    public async Task Logo_RoundTrips()
    {
        var storage = await NewStorage();

        await storage.SaveReceiptLogoAsync("AAECAw==");

        Assert.Equal("AAECAw==", await storage.GetReceiptLogoAsync());
    }

    /// <summary>InitializeAsync обязателен: именно он создаёт таблицу Settings,
    /// из которой всё это читается. Без него тест падает не про то.</summary>
    private static async Task<OfflineStorageService> NewStorage()
    {
        var storage = new OfflineStorageService(
            Path.Combine(Path.GetTempPath(), $"vvcash-receipt-{System.Guid.NewGuid():N}.db"));
        await storage.InitializeAsync();
        return storage;
    }
}
```

- [ ] **Step 3: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptTemplateStorageTest"`
Expected: FAIL — методов нет.

- [ ] **Step 4: Объявить методы в интерфейсе**

В `src/VvCash/Services/Data/IOfflineStorageService.cs`, рядом с методами про `CashFeatures`:

```csharp
    /// <summary>Сырое значение опции receipt_template, как его вернул сервер.
    /// Именно сырое, а не разобранное: разбор — дело ReceiptTemplate.Parse, и
    /// держать его в двух местах незачем. Пусто — шаблон никогда не приезжал.</summary>
    Task SaveReceiptTemplateAsync(string raw);
    Task<string> GetReceiptTemplateAsync();

    /// <summary>Растровый логотип в base64, уже сведённый в один бит бэкофисом.
    /// Отдельно от шаблона: 7–8 КБ не должны ездить внутри каждого шаблона.</summary>
    Task SaveReceiptLogoAsync(string base64);
    Task<string> GetReceiptLogoAsync();
```

- [ ] **Step 5: Реализовать по образцу фичефлагов**

В `src/VvCash/Services/Data/OfflineStorageService.cs`, рядом с `SaveCashFeaturesAsync`:

```csharp
    public Task SaveReceiptTemplateAsync(string raw) => SaveSettingAsync("ReceiptTemplate", raw);

    public Task<string> GetReceiptTemplateAsync() => GetSettingAsync("ReceiptTemplate");

    public Task SaveReceiptLogoAsync(string base64) => SaveSettingAsync("ReceiptLogo", base64);

    public Task<string> GetReceiptLogoAsync() => GetSettingAsync("ReceiptLogo");

    private async Task SaveSettingAsync(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ($Key, $Value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
        ";
        command.Parameters.AddWithValue("$Key", key);
        command.Parameters.AddWithValue("$Value", value);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> GetSettingAsync(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $Key";
        command.Parameters.AddWithValue("$Key", key);

        return await command.ExecuteScalarAsync() as string ?? string.Empty;
    }
```

- [ ] **Step 6: Починить шесть заглушек, которые сломались об интерфейс**

Четыре новых метода в `IOfflineStorageService` ломают сборку тестов: интерфейс реализуют шесть подделок, и каждая теперь неполна.

```bash
grep -rn ": IOfflineStorageService" tests/VvCash.Tests/
```

Ожидаемо шесть попаданий: `CashFeatureServiceTest`, `ExpenseDocumentServiceTest`, `PosViewModelSellerGateTest`, `SellerRosterServiceTest`, `SettingsViewModelTest`, `SyncServiceTest`.

В пять из них — все, кроме `SyncServiceTest`, — допишите заглушки, которые ничего не помнят: этим тестам шаблон не нужен вовсе.

```csharp
        public Task SaveReceiptTemplateAsync(string raw) => Task.CompletedTask;
        public Task<string> GetReceiptTemplateAsync() => Task.FromResult(string.Empty);
        public Task SaveReceiptLogoAsync(string base64) => Task.CompletedTask;
        public Task<string> GetReceiptLogoAsync() => Task.FromResult(string.Empty);
```

В `tests/VvCash.Tests/SyncServiceTest.cs` заглушка обязана **помнить** записанное — на ней держатся тесты синхронизации из Task 10:

```csharp
        public string ReceiptTemplate = string.Empty;
        public string ReceiptLogo = string.Empty;

        public Task SaveReceiptTemplateAsync(string raw) { ReceiptTemplate = raw; return Task.CompletedTask; }
        public Task<string> GetReceiptTemplateAsync() => Task.FromResult(ReceiptTemplate);
        public Task SaveReceiptLogoAsync(string base64) { ReceiptLogo = base64; return Task.CompletedTask; }
        public Task<string> GetReceiptLogoAsync() => Task.FromResult(ReceiptLogo);
```

- [ ] **Step 7: Прогнать тесты**

Run: `& ./run-tests.ps1`
Expected: PASS. Весь набор целиком, а не по фильтру: правка интерфейса задевает шесть чужих файлов, и падение любого из них надо увидеть сейчас, а не через две задачи.

- [ ] **Step 8: Commit**

```bash
git add src/VvCash/Services/Data/IOfflineStorageService.cs src/VvCash/Services/Data/OfflineStorageService.cs tests/VvCash.Tests/
git commit -m "feat(receipt): cache the template and logo alongside the feature flags"
```

---

## Task 10: Синхронизация с сервером и служба шаблона

**Files:**
- Create: `src/VvCash/Services/IReceiptTemplateService.cs`
- Create: `src/VvCash/Services/ReceiptTemplateService.cs`
- Modify: `src/VvCash/Services/Data/SyncService.cs:327` и рядом с `SyncFeaturesAsync`
- Modify: `src/VvCash/App.axaml.cs:328` и `:425`
- Modify: `tests/VvCash.Tests/SyncServiceTest.cs`

Ответ `GET /cashes/config/get/` — группы с опциями; нужная опознаётся по `code`:

```json
{"status":0,"body":[{"id":"...","name":"Чек","options":[
  {"id":"...","name":"receiptTemplate","description":"...","value":"{\"version\":1,...}",
   "code":"receipt_template","value_type":"json"}]}]}
```

- [ ] **Step 1: Написать падающий тест**

Тесты идут в **существующий** `tests/VvCash.Tests/SyncServiceTest.cs`, а не в новый файл: там уже есть `FakeSettings`, `FakeStorage`, `FakeExpenseDocuments`, общий `StubHttpMessageHandler` и помощник `Build(handler, storage)`. Своя копия этой обвязки была бы пятой в репозитории.

`FakeStorage` уже умеет помнить шаблон — это сделано в Task 9, Step 6.

Допишите в класс `SyncServiceTest`:

```csharp
    private const string ConfigOk = """
    {"status":0,"body":[{"id":"g1","name":"Чек","options":[
      {"id":"o1","name":"receiptTemplate","description":"","value":"{\"version\":1,\"width\":42,\"blocks\":[]}",
       "code":"receipt_template","value_type":"json"}]}]}
    """;

    [Fact]
    public async Task SyncReceiptTemplateAsync_CachesTheRawValue_OnSuccess()
    {
        var storage = new FakeStorage();
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, ConfigOk)), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":42", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_KeepsTheCache_OnAnHttpFailure()
    {
        // Потеря эндпоинта не должна откатывать магазин на дефолтный чек.
        var storage = new FakeStorage { ReceiptTemplate = """{"version":1,"width":48,"blocks":[]}""" };
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.InternalServerError, "")), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":48", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_KeepsTheCache_OnANegativeBackendStatus()
    {
        var storage = new FakeStorage { ReceiptTemplate = """{"version":1,"width":48,"blocks":[]}""" };
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, """{"status":-1,"body":null}""")), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":48", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_KeepsTheCache_WhenTheOptionIsAbsent()
    {
        // Тенант, где миграция ещё не прогнана: опции с этим кодом просто нет.
        var storage = new FakeStorage { ReceiptTemplate = """{"version":1,"width":48,"blocks":[]}""" };
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, """{"status":0,"body":[]}""")), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":48", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_IgnoresAnOptionWithAnEmptyCode()
    {
        // Каждая опция, засеянная до 20260728000800, приезжает с code = "" —
        // сегодня их два десятка. Совпадение по пустой строке склеило бы их все.
        var body = """
        {"status":0,"body":[{"id":"g1","name":"Прочее","options":[
          {"id":"o9","name":"storeName","description":"","value":"Лавка","code":"","value_type":"string"}]}]}
        """;
        var storage = new FakeStorage();
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, body)), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Equal(string.Empty, storage.ReceiptTemplate);
    }
```

Если `System.Net.HttpStatusCode` в этом файле ещё не подключён — добавьте `using System.Net;`.

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SyncServiceTest"`
Expected: FAIL — метода `SyncReceiptTemplateAsync` нет.

- [ ] **Step 3: Написать синхронизацию**

В `src/VvCash/Services/Data/SyncService.cs`, рядом с `SyncFeaturesAsync`:

```csharp
    /// <summary>Забирает шаблон чека и логотип из конфига кассы. Любой отказ
    /// оставляет закэшированное: потеря эндпоинта не должна откатывать магазин
    /// на дефолтный чек, а отсутствие опции — нормальное состояние тенанта, где
    /// миграция ещё не прогнана.
    ///
    /// internal, а не private: SyncServiceTest вызывает её напрямую —
    /// прогонять ради неё весь SyncAsync с товарами и остатками значит проверять
    /// не то.</summary>
    internal async Task SyncReceiptTemplateAsync(string baseUrl)
    {
        try
        {
            var url = $"{baseUrl}cashes/config/get/";
            Console.WriteLine($"[SyncService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] receipt template: HTTP {(int)response.StatusCode}, keeping cache");
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 0)
            {
                Console.WriteLine("[SyncService] receipt template: backend status != 0, keeping cache");
                return;
            }
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("[SyncService] receipt template: no body, keeping cache");
                return;
            }

            var template = FindOptionValue(body, "receipt_template");
            if (template != null) await _storageService.SaveReceiptTemplateAsync(template);

            var logo = FindOptionValue(body, "receipt_logo");
            if (logo != null) await _storageService.SaveReceiptLogoAsync(logo);

            Console.WriteLine($"[SyncService] receipt template: {(template == null ? "absent" : "cached")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] receipt template sync error: {ex.Message}");
        }
    }

    /// <summary>Ищет значение опции по коду. Пустой код пропускается: каждая
    /// опция, засеянная до 20260728000800, приезжает с code = "" — сегодня их
    /// два десятка, и совпадение по пустой строке склеило бы их все в одну.</summary>
    private static string? FindOptionValue(JsonElement groups, string code)
    {
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("code", out var c)) continue;
                var value = c.GetString();
                if (string.IsNullOrEmpty(value) || value != code) continue;

                return option.TryGetProperty("value", out var v) ? v.GetString() : null;
            }
        }
        return null;
    }
```

- [ ] **Step 4: Позвать её из общей синхронизации**

В `src/VvCash/Services/Data/SyncService.cs:327`, следом за `await SyncFeaturesAsync(baseUrl);`:

```csharp
            await SyncReceiptTemplateAsync(baseUrl);
```

- [ ] **Step 5: Прогнать тесты синхронизации**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SyncServiceTest"`
Expected: PASS, включая пять новых тестов.

- [ ] **Step 6: Завести службу шаблона**

Создайте `src/VvCash/Services/IReceiptTemplateService.cs`:

```csharp
using System.Threading.Tasks;
using VvCash.Models.Receipt;

namespace VvCash.Services;

public interface IReceiptTemplateService
{
    /// <summary>Действующий шаблон. До первого RefreshAsync — Default: касса на
    /// старте обязана уметь печатать, а не ждать сети.</summary>
    ReceiptTemplate Current { get; }

    /// <summary>Растровый логотип в base64, пусто — его нет.</summary>
    string Logo { get; }

    Task RefreshAsync();
}
```

Создайте `src/VvCash/Services/ReceiptTemplateService.cs`:

```csharp
using System.Threading.Tasks;
using VvCash.Models.Receipt;
using VvCash.Services.Data;

namespace VvCash.Services;

/// <summary>Устроен как CashFeatureService и по той же причине: касса
/// мид-запуска обязана отрисовать рабочий экран и напечатать чек, а не бросить.</summary>
public class ReceiptTemplateService : IReceiptTemplateService
{
    private readonly IOfflineStorageService _storage;

    public ReceiptTemplateService(IOfflineStorageService storage) => _storage = storage;

    public ReceiptTemplate Current { get; private set; } = ReceiptTemplate.Default;

    public string Logo { get; private set; } = string.Empty;

    public async Task RefreshAsync()
    {
        Current = ReceiptTemplate.Parse(await _storage.GetReceiptTemplateAsync());
        Logo = await _storage.GetReceiptLogoAsync();
    }
}
```

- [ ] **Step 7: Зарегистрировать службу**

В `src/VvCash/App.axaml.cs`, следом за строкой 328 (`services.AddSingleton<ICashFeatureService, CashFeatureService>();`):

```csharp
        services.AddSingleton<IReceiptTemplateService, ReceiptTemplateService>();
```

- [ ] **Step 8: Связать шаблон с составом принтеров**

Сейчас на строке 425 регистрация по типу:

```csharp
        services.AddSingleton<IPrinterService, CompositePrinterService>();
```

Ей нужен третий аргумент конструктора, поэтому она становится фабрикой:

```csharp
        // Фабрика, а не регистрация по типу: составу принтеров нужен поставщик
        // шаблона. Поставщик, а не значение — шаблон приезжает синхронизацией в
        // произвольный момент и читается в момент печати, поэтому его смена
        // состав принтеров не пересобирает.
        services.AddSingleton<IPrinterService>(sp => new CompositePrinterService(
            sp.GetRequiredService<ISettingsService>(),
            printerFactory: null,
            template: () => sp.GetRequiredService<IReceiptTemplateService>().Current));
```

- [ ] **Step 9: Заряжать шаблон на старте и после каждой синхронизации**

`IReceiptTemplateService` до первого `RefreshAsync` отдаёт `Default`, поэтому его надо позвать — иначе закэшированный шаблон не доедет до печати никогда.

Найдите в `App.axaml.cs` место, где после построения провайдера уже дёргают сервисы:

```bash
grep -n "Services.GetRequiredService<IOfflineStorageService>()" src/VvCash/App.axaml.cs
```

Рядом добавьте:

```csharp
                // Шаблон читается из кэша один раз на старте и заново после каждой
                // успешной синхронизации. Подписка на ProductsSynced, а не инъекция
                // службы в PosViewModel: у той вью-модели полтора десятка мест
                // построения в тестах, и ещё один параметр конструктора обошёлся бы
                // дороже, чем эта строка.
                var templates = Services.GetRequiredService<IReceiptTemplateService>();
                await templates.RefreshAsync();
                Services.GetRequiredService<ISyncService>().ProductsSynced +=
                    async (_, _) => await templates.RefreshAsync();
```

Если в этом месте нет `async`-контекста, поставьте `templates.RefreshAsync().GetAwaiter().GetResult();` вместо `await` — стартовое чтение локальной SQLite занимает миллисекунды и окно этим не задержит.

- [ ] **Step 10: Прогнать всё**

Run: `& ./run-tests.ps1`
Expected: PASS.

- [ ] **Step 11: Проверить в живом приложении**

Соберите и запустите кассу (в `build/verify`, чтобы запущенный экземпляр не держал вывод):

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Ожидаемое: касса стартует, в логе видна строка `[SyncService] GET …cashes/config/get/`, а чек печатается как раньше — на тенанте без прогнанной миграции опции с кодом нет, и это штатный путь.

- [ ] **Step 12: Commit**

```bash
git add src/VvCash/Services/IReceiptTemplateService.cs src/VvCash/Services/ReceiptTemplateService.cs src/VvCash/Services/Data/SyncService.cs src/VvCash/App.axaml.cs tests/VvCash.Tests/SyncServiceTest.cs
git commit -m "feat(receipt): sync the receipt template from the cash config endpoint"
```

---

## Task 11: Растровый логотип доезжает до принтера

**Files:**
- Modify: `src/VvCash/Services/Rendering/ReceiptRenderer.cs`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs`
- Test: `tests/VvCash.Tests/ReceiptLogoTest.cs`

Логотип живёт в отдельной опции, поэтому рендереру его надо передать — сам он его не достанет.

- [ ] **Step 1: Написать падающий тест**

Создайте `tests/VvCash.Tests/ReceiptLogoTest.cs`:

```csharp
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
    /// пересчёт в одном месте лучше, чем в двух репозиториях.</summary>
    private const string Logo = """{"widthBytes":6,"height":2,"raster":"AAAAAAAAAAAAAA=="}""";

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
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptLogoTest"`
Expected: FAIL — у `Render` нет третьего параметра.

- [ ] **Step 3: Принять логотип в рендерере**

В `src/VvCash/Services/Rendering/ReceiptRenderer.cs` смените сигнатуру и ветку логотипа:

```csharp
    public static IReadOnlyList<ReceiptOp> Render(ReceiptTemplate template, SaleReceiptData sale,
        string? logoJson = null)
```

Пробросьте `logoJson` в `RenderBlock` (добавьте параметр) и замените ветку `LogoBlock`:

```csharp
            case LogoBlock logo:
                ops.Add(new AlignOp(logo.Align));
                if (logo.Source == LogoSource.Nv)
                {
                    ops.Add(new NvLogoOp(logo.NvSlot));
                    break;
                }
                var bitmap = ParseLogo(logoJson);
                if (bitmap != null) ops.Add(bitmap);
                break;
```

и добавьте в конец класса:

```csharp
    /// <summary>Разбирает опцию receipt_logo. Любая беда — нет логотипа, а не
    /// исключение: блок включён, а картинка ещё не доехала — это наполовину
    /// настроенная касса, а не повод не напечатать чек.</summary>
    private static BitmapOp? ParseLogo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            var widthBytes = root.GetProperty("widthBytes").GetInt32();
            var height = root.GetProperty("height").GetInt32();
            var raster = Convert.FromBase64String(root.GetProperty("raster").GetString() ?? "");

            if (widthBytes <= 0 || height <= 0 || raster.Length < widthBytes * height) return null;

            return new BitmapOp(raster, widthBytes, height);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        {
            Console.WriteLine($"[ReceiptRenderer] логотип не разобран, печатаю без него: {ex.Message}");
            return null;
        }
    }
```

- [ ] **Step 4: Пробросить логотип от службы до принтера**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs` смените поле и конструктор так, чтобы поставщик отдавал пару. Замените `Func<ReceiptTemplate>` на:

```csharp
    private readonly Func<(ReceiptTemplate Template, string Logo)> _template;
```

в конструкторе:

```csharp
        Func<(ReceiptTemplate Template, string Logo)>? template = null)
    {
        ...
        _template = template ?? (() => (ReceiptTemplate.Default, string.Empty));
    }
```

и в `BuildConfiguredSaleReceipt`:

```csharp
    public byte[] BuildConfiguredSaleReceipt(IEnumerable<CartItem> items,
        decimal subtotal, decimal discount, decimal total,
        string? discountName = null, string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null, string? queueNumber = null)
    {
        var (template, logo) = _template();
        var sale = new SaleReceiptData(new List<CartItem>(items), subtotal, discount, total,
            discountName, documentNumber, warehouseName, sellerName, saleDate, queueNumber);
        return EscPosEmitter.Emit(ReceiptRenderer.Render(template, sale, logo), _codePage);
    }
```

В `CompositePrinterService` смените тип параметра `template` на тот же кортеж, а в `App.axaml.cs` — регистрацию:

```csharp
            template: () =>
            {
                var t = sp.GetRequiredService<IReceiptTemplateService>();
                return (t.Current, t.Logo);
            }));
```

Поправьте `ReceiptTemplateWiringTest` под новую сигнатуру: `() => current` становится `() => (current, "")`.

- [ ] **Step 5: Прогнать всё**

Run: `& ./run-tests.ps1`
Expected: PASS, включая замок из Task 1.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/ src/VvCash/App.axaml.cs tests/VvCash.Tests/ReceiptLogoTest.cs tests/VvCash.Tests/ReceiptTemplateWiringTest.cs
git commit -m "feat(receipt): print the synced bitmap logo"
```

---

## Task 12: Эталон для превью в бэкофисе

Превью в bozor — вторая реализация раскладки, на TypeScript. Этот файл — то единственное, что ловит их расхождение.

**Files:**
- Create: `tests/VvCash.Tests/Fixtures/receipt-golden.json`
- Create: `tests/VvCash.Tests/ReceiptPreviewGoldenTest.cs`
- Create: `scripts/sync-receipt-fixture.ps1`

- [ ] **Step 1: Написать тест, который порождает и проверяет эталон**

Создайте `tests/VvCash.Tests/ReceiptPreviewGoldenTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "receipt-golden.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static SaleReceiptData DemoSale() => new(
        SaleReceiptGoldenTest.GoldenItems(),
        Subtotal: 5435m, Discount: 435m, Total: 5000m,
        DiscountName: "Акция «Ремонт»",
        DocumentNumber: "A-42", WarehouseName: "Склад №1",
        SellerName: "Иванов", SaleDate: "01.09.2026 12:30");

    [Fact]
    public void PreviewGolden_MatchesWhatTheRendererProduces()
    {
        var expected = ReceiptRenderer.Render(ReceiptTemplate.Default, DemoSale())
            .OfType<TextOp>().Select(o => o.Line).ToArray();

        if (Environment.GetEnvironmentVariable("VVCASH_UPDATE_GOLDEN") == "1")
        {
            var payload = new
            {
                rendererVersion = RendererVersion,
                template = ReceiptTemplate.Default,
                sale = new
                {
                    subtotal = 5435m, discount = 435m, total = 5000m,
                    discountName = "Акция «Ремонт»", documentNumber = "A-42",
                    warehouseName = "Склад №1", sellerName = "Иванов",
                    saleDate = "01.09.2026 12:30",
                    items = new[]
                    {
                        new { name = "Плитка", quantity = "53", lineTotal = "5300.00",
                              secondaryUnit = "12.72 м²" },
                        new { name = "Клей", quantity = "3", lineTotal = "135.00",
                              secondaryUnit = (string?)null },
                    },
                },
                expectedLines = expected,
            };

            var dir = Path.Combine(FindRepoRoot(), "tests", "VvCash.Tests", "Fixtures");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "receipt-golden.json"),
                JsonSerializer.Serialize(payload, Options));
            return;
        }

        Assert.True(File.Exists(FixturePath),
            $"Эталона нет: {FixturePath}. Сгенерируйте его с VVCASH_UPDATE_GOLDEN=1.");

        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        Assert.Equal(RendererVersion, doc.RootElement.GetProperty("rendererVersion").GetInt32());
        Assert.Equal(expected,
            doc.RootElement.GetProperty("expectedLines").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "vv-cash.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("vv-cash.slnx не найден выше по дереву");
    }
}
```

- [ ] **Step 2: Прогнать и увидеть падение**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptPreviewGoldenTest"`
Expected: FAIL — «Эталона нет».

- [ ] **Step 3: Сгенерировать эталон**

```bash
$env:VVCASH_UPDATE_GOLDEN='1'; & ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptPreviewGoldenTest"; $env:VVCASH_UPDATE_GOLDEN=$null
```

Expected: PASS, появился `tests/VvCash.Tests/Fixtures/receipt-golden.json`.

- [ ] **Step 4: Прочитать эталон глазами**

```bash
cat tests/VvCash.Tests/Fixtures/receipt-golden.json
```

Expected: в `expectedLines` виден настоящий чек — `VV CASH POS`, `Doc #A-42`, строки товаров с выровненными ценами, `TOTAL:`, подвал. Если строки пустые или их две — рендерер отдал не то, и эталон закреплять нельзя.

- [ ] **Step 5: Прогнать начисто**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~ReceiptPreviewGoldenTest"`
Expected: PASS.

- [ ] **Step 6: Написать скрипт копирования в bozor**

Создайте `scripts/sync-receipt-fixture.ps1`:

```powershell
#!/usr/bin/env pwsh
# Кладёт эталон чека в bozor, где по нему проверяется превью.
#
# Канонический экземпляр — здесь. Обратного направления нет намеренно: раскладку
# определяет боевой рендерер на C#, а не превью.
#
# Правите рендерер — перегенерируйте эталон (VVCASH_UPDATE_GOLDEN=1), поднимите
# RendererVersion в ReceiptPreviewGoldenTest, прогоните этот скрипт и закоммитьте
# результат в bozor. Забудете — расхождение превью и бумаги не поймает никто.
param([string]$BozorPath = "C:/work/bozor")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$source = Join-Path $root "tests/VvCash.Tests/Fixtures/receipt-golden.json"
$targetDir = Join-Path $BozorPath "src/app/dialogs/cash/receipt-template/__fixtures__"

if (-not (Test-Path $source)) { throw "Эталона нет: $source" }
New-Item -ItemType Directory -Force $targetDir | Out-Null
Copy-Item $source (Join-Path $targetDir "receipt-golden.json") -Force
Write-Host "Эталон скопирован в $targetDir"
```

- [ ] **Step 7: Commit**

```bash
git add tests/VvCash.Tests/ReceiptPreviewGoldenTest.cs tests/VvCash.Tests/Fixtures/receipt-golden.json scripts/sync-receipt-fixture.ps1
git commit -m "test(receipt): publish a golden fixture for the back-office preview"
```

---

## Task 13: Финальная проверка

- [ ] **Step 1: Полный прогон**

Run: `& ./run-tests.ps1`
Expected: PASS целиком. При случайном падении по гонке Avalonia Dispatcher — прочитайте стектрейс и перезапустите; если падение повторяется в одном и том же тесте, это ваша правка.

- [ ] **Step 2: Убедиться, что литералов ширины не осталось в чеке продажи**

```bash
grep -n ", 32)" src/VvCash/Services/Hardware/EscPosPrinterService.cs
```

Expected: остались только вызовы из `BuildReturnReceipt`, `BuildExchangeReceipt` и `BuildPreReceipt` — эти документы планом не затрагиваются. Ни одного в чеке продажи.

- [ ] **Step 3: Убедиться, что сборка проходит**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Expected: `Build succeeded`, ноль предупреждений про nullable в новых файлах.

- [ ] **Step 4: Проверить дефолтный путь на живой кассе**

Запустите приложение, пробейте продажу, напечатайте чек. Ожидаемое: чек не отличается от того, что печатался до всей этой работы. Тенант без прогнанной миграции опции не отдаёт, шаблон не приезжает, печатается `Default`.

- [ ] **Step 5: Проверить шаблон руками, не дожидаясь бэкенда**

Пока миграция не прогнана, подсуньте шаблон в кэш напрямую:

```bash
python -c "
import sqlite3,json,sys
t={'version':1,'width':32,'blocks':[
 {'type':'text','content':'МОЙ МАГАЗИН','align':'center','bold':True},
 {'type':'line'},{'type':'items'},{'type':'line'},{'type':'totals'},
 {'type':'text','content':'Спасибо!','align':'center'},{'type':'feed','lines':2}]}
db=sys.argv[1]
c=sqlite3.connect(db)
c.execute(\"INSERT INTO Settings(Key,Value) VALUES('ReceiptTemplate',?) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value\",(json.dumps(t,ensure_ascii=False),))
c.commit()" <путь-к-базе-кассы>
```

Путь к базе смотрите в `OfflineStorageService` — там же, где касса её создаёт. Перезапустите кассу и напечатайте чек: в шапке должно стоять `МОЙ МАГАЗИН`, в подвале `Спасибо!`.

- [ ] **Step 6: Обновить статус спеки**

В `docs/superpowers/specs/2026-09-01-configurable-receipt-template-design.md` смените строку статуса на:

```markdown
**Статус:** касса реализована; сервер и бэкофис — отдельными планами
```

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-09-01-configurable-receipt-template-design.md
git commit -m "docs(receipt): mark the register half of the template work as done"
```

---

## Чего этот план не делает

- Миграция `cloudmarket-server`, `code`/`value_type` на опции `receiptTemplate`, валидация `json` в `validateConfigValue` — отдельный план в том репозитории. **До него шаблон с сервера не приедет**, и это ожидаемое состояние: касса печатает дефолт.
- Конструктор блоков и ASCII-превью в `bozor` — отдельный план там же. Скрипт `scripts/sync-receipt-fixture.ps1` из Task 12 готов принять эталон, когда дойдёт до дела.
- Пречек, возврат, обмен, талон остаются на старом пути. `EscPosEmitter` им доступен, но перевод их раскладки — не эта работа.
- Растровый дизеринг PNG. Касса печатает уже готовый однобитный растр; сведение делает браузер в бэкофисе.
