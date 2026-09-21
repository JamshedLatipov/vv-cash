# Configurable Queue Numbers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Номер талона очереди настраивается: буква кассы (`A-123`), диапазон чисел, перемешивание вкл/выкл, число касс на точке вместо константы 5.

**Architecture:** Шесть настроек собираются в снимок `QueueNumberOptions`; формула среза живёт в одном месте (`QueueNumberSlice`) и используется и пулом, и предпросмотром в настройках. `NumberPool` читает снимок на каждой выдаче и пересобирает пул, когда меняется `PoolKey` (день + настройки формы), а не только день. Число в `QueueOrder` остаётся `int`, буква едет рядом полем `Prefix`, наружу уходит вычисляемый `Label`.

**Tech Stack:** .NET 10, Avalonia 11.2.3, `Microsoft.Data.Sqlite`, xunit, ванильный JS в `Assets/Web`.

**Спека:** [`docs/superpowers/specs/2026-09-22-configurable-queue-numbers-design.md`](../specs/2026-09-22-configurable-queue-numbers-design.md)

**Статус:** выполнено 2026-09-22, ветка feat/queue-number-format, 1337/1337 тестов зелёные.

---

## Как запускать тесты

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest"
```

`pwsh` на этой машине нет — запускать через `&`, не `pwsh ./run-tests.ps1`. Скрипт
собирает в `build/verify-tests`, чтобы запущенное приложение не держало вывод.
Скрипт останавливается на первом красном тесте и не печатает сводку — красный
тест выглядит как сломанный прогон; читать стек.

Сборка приложения при запущенном экземпляре падает на блокировке файла —
собирать в отдельную папку:

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Полный прогон изредка роняет случайный тест через гонку Avalonia Dispatcher.
Упал тест не по теме — смотреть стек, а не диф.

Тесты на числа: машина с `ru-RU`, поэтому строки в VM и тестах — только
`CultureInfo.InvariantCulture`.

---

## Карта файлов

| Файл | Что |
|---|---|
| **Создать** `src/VvCash/Services/Queue/QueueNumberOptions.cs` | Снимок шести настроек, константы, клэмпы, `Default`, `From`, `ShapesTheLegacyPool` |
| **Создать** `src/VvCash/Services/Queue/QueueNumberSlice.cs` | Срез кассы по возрастанию: `%`-срез при шаффле, блоки — по порядку; сводка для предпросмотра |
| **Создать** `tests/VvCash.Tests/QueueNumberOptionsTest.cs` | Клэмпы, `ShapesTheLegacyPool` |
| **Создать** `tests/VvCash.Tests/QueueNumberSliceTest.cs` | Формулы среза |
| Изменить `src/VvCash/Services/Queue/IQueueSettings.cs` | +5 свойств |
| Изменить `src/VvCash/Services/SettingsService.cs` | `SettingsData` +5 полей, геттеры с клэмпами, `Load()` |
| Изменить `src/VvCash/Services/Queue/NumberPool.cs` | Ctor на `Func<QueueNumberOptions>`, `PoolKey`, усыновление `Day`, исчерпание: свежий → свободный → живой, кулдаун удалён (см. Task 5) |
| Изменить `src/VvCash/Models/QueueOrder.cs` | `Prefix`, `Label`, `FormatLabel` |
| Изменить `src/VvCash/Services/Queue/QueueStorage.cs` | Колонка `Prefix` |
| Изменить `src/VvCash/Services/Queue/IQueueClient.cs`, `QueueClient.cs` | `IssueNumberAsync → string?`, ctor на `Func<QueueNumberOptions>`, `Prefix` на заказе |
| Изменить `src/VvCash/ViewModels/PosViewModel.cs:2626-2663` | Печать `label` |
| Изменить `src/VvCash/App.axaml.cs:486-524` | DI: `NumberPool`, `QueueClient` |
| Изменить `src/VvCash/Assets/Web/kds.html`, `board.html` | `o.label` |
| Изменить `src/VvCash/ViewModels/SettingsViewModel.cs` | Пять полей + предпросмотр |
| Изменить `src/VvCash/Views/SettingsView.axaml:386-398` | Поля и предпросмотр |
| Изменить `src/VvCash/Assets/i18n/{ru,en,uz,kk,tg}.json` | Ключи |
| Тесты: `QueueSettingsTest`, `NumberPoolTest`, `QueueClientTest`, `QueueStorageTest`, `QueueServerTest`, `SettingsViewModelTest`, `PosViewModelSellerGateTest`, `QueueServerHostTest` | Обновить фейки и конструкторы, добавить случаи |

---

### Task 1: `QueueNumberOptions` — снимок настроек и клэмпы

**Files:**
- Create: `src/VvCash/Services/Queue/QueueNumberOptions.cs`
- Create: `tests/VvCash.Tests/QueueNumberOptionsTest.cs`

- [x] **Step 1: Write the failing tests**

```csharp
// tests/VvCash.Tests/QueueNumberOptionsTest.cs
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueNumberOptionsTest
{
    [Fact]
    public void DefaultMatchesTheConstantsThePoolShippedWith()
    {
        var options = QueueNumberOptions.Default(tillIndex: 2, secret: "s");

        Assert.Equal(2, options.TillIndex);
        Assert.Equal(5, options.TillCount);
        Assert.Equal(100, options.Min);
        Assert.Equal(999, options.Max);
        Assert.True(options.Shuffle);
        Assert.Equal(string.Empty, options.Prefix);
        Assert.Equal("s", options.Secret);
        Assert.True(options.ShapesTheLegacyPool);
    }

    [Theory]
    [InlineData(4, 100, 999, true, false)]   // TillCount
    [InlineData(5, 1, 999, true, false)]     // Min
    [InlineData(5, 100, 500, true, false)]   // Max
    [InlineData(5, 100, 999, false, false)]  // Shuffle
    [InlineData(5, 100, 999, true, true)]
    public void OnlyTheShippedShapeCountsAsLegacy(
        int tillCount, int min, int max, bool shuffle, bool expected)
    {
        var options = new QueueNumberOptions(0, tillCount, min, max, shuffle, "", "s");

        Assert.Equal(expected, options.ShapesTheLegacyPool);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    [InlineData(50, 9)]
    [InlineData(-3, 1)]
    public void TillCountIsClampedToOneThroughNine(int raw, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampTillCount(raw));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(9999, 9999)]
    [InlineData(10000, 9999)]
    [InlineData(-7, 1)]
    public void MinIsClampedToOneThroughFourDigits(int raw, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampMin(raw));
    }

    [Theory]
    [InlineData(999, 100, 999)]
    [InlineData(50, 100, 100)]    // below Min → Min
    [InlineData(99999, 100, 9999)]
    [InlineData(0, 1, 1)]
    public void MaxIsClampedBetweenMinAndFourDigits(int raw, int min, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampMax(raw, min));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(9, 5, 4)]
    [InlineData(-3, 5, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(0, 1, 0)]
    public void TillIndexIsClampedIntoTheTillCount(int raw, int tillCount, int expected)
    {
        Assert.Equal(expected, QueueNumberOptions.ClampTillIndex(raw, tillCount));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  A- ", "A-")]
    [InlineData("ABCD", "ABC")]
    [InlineData("Кас", "Кас")]
    [InlineData("  ABCDEF  ", "ABC")]
    public void PrefixIsTrimmedAndCutToThreeCharacters(string? raw, string expected)
    {
        Assert.Equal(expected, QueueNumberOptions.NormalizePrefix(raw));
    }
}
```

- [x] **Step 2: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueNumberOptionsTest"`
Expected: сборка тестов падает — `QueueNumberOptions` не существует.

- [x] **Step 3: Write the record**

```csharp
// src/VvCash/Services/Queue/QueueNumberOptions.cs
using System;

namespace VvCash.Services.Queue;

/// <summary>Снимок настроек формы номера очереди — то, от чего NumberPool
/// считает срез и ключ пересборки, а QueueClient берёт индекс кассы и
/// префикс. Один record, а не шесть свойств IQueueSettings, читаемых
/// по очереди: пулу нужны согласованные значения на одну выдачу, а
/// настройки могут поменяться между двумя чтениями.
///
/// Prefix и Secret в PoolKey не входят (см. NumberPool.PoolKey): префикс
/// прибавляется к уже выданному числу, секрет влияет только на порядок при
/// следующей пересборке.</summary>
public sealed record QueueNumberOptions(
    int TillIndex, int TillCount, int Min, int Max, bool Shuffle,
    string Prefix, string Secret)
{
    /// <summary>Константы, с которыми пул уехал на точки до этой настройки.
    /// Значения по умолчанию SettingsData — они же, и это не совпадение:
    /// settings.json без новых полей обязан читаться как сегодняшнее
    /// поведение.</summary>
    public const int DefaultTillCount = 5;
    public const int DefaultMin = 100;
    public const int DefaultMax = 999;

    public const int MinTillCount = 1;
    public const int MaxTillCount = 9;
    public const int MinNumber = 1;
    /// <summary>Четыре цифры: талон печатает номер двойной шириной, и 3
    /// символа префикса плюс 4 цифры — предел, который помещается на 58-мм
    /// ленте (см. EscPosPrinterService.BuildTicket).</summary>
    public const int MaxNumber = 9999;
    public const int MaxPrefixLength = 3;

    public static QueueNumberOptions From(IQueueSettings settings) => new(
        settings.TillIndex, settings.TillCount, settings.QueueNumberMin, settings.QueueNumberMax,
        settings.QueueNumberShuffle, settings.QueueNumberPrefix, settings.QueueSecret);

    /// <summary>Сегодняшние константы — для тестов и для места, где
    /// IQueueSettings нет.</summary>
    public static QueueNumberOptions Default(int tillIndex, string secret) =>
        new(tillIndex, DefaultTillCount, DefaultMin, DefaultMax, true, string.Empty, secret);

    /// <summary>Истинно, когда пул на диске, собранный кодом до этой
    /// настройки (он знал только константы), совпал бы с пулом от этих
    /// настроек. NumberPool по этому признаку усыновляет старый пул вместо
    /// пересборки посреди дня — см. его EnsurePoolAsync.</summary>
    public bool ShapesTheLegacyPool =>
        TillCount == DefaultTillCount && Min == DefaultMin && Max == DefaultMax && Shuffle;

    // Клэмпы — здесь, а не в SettingsService: предпросмотр в настройках
    // обязан показывать то, что реально сохранится, и делить формулу на два
    // места значило бы, что экран однажды покажет одно, а талон напечатает
    // другое.

    public static int ClampTillCount(int raw) => Math.Clamp(raw, MinTillCount, MaxTillCount);

    public static int ClampMin(int raw) => Math.Clamp(raw, MinNumber, MaxNumber);

    public static int ClampMax(int raw, int min) => Math.Clamp(raw, min, MaxNumber);

    public static int ClampTillIndex(int raw, int tillCount) => Math.Clamp(raw, 0, tillCount - 1);

    public static string NormalizePrefix(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        return trimmed.Length <= MaxPrefixLength ? trimmed : trimmed[..MaxPrefixLength];
    }
}
```

Тесты не соберутся, пока в `IQueueSettings` нет пяти свойств (`From`
обращается к ним). Чтобы Task 1 был зелёным сам по себе, добавить свойства
в интерфейс **здесь же** — реализация в `SettingsService` и фейках следует
в Task 3, но интерфейс без реализаций компилятор не пропустит. Поэтому в
этом шаге добавить свойства и в интерфейс, и во все четыре реализации
минимально (автосвойства без клэмпов); клэмпы — Task 3.

```csharp
// src/VvCash/Services/Queue/IQueueSettings.cs — добавить в интерфейс после TillIndex:

    /// <summary>Сколько касс делят диапазон номеров. Заменяет константу
    /// NumberPool.Tills = 5. Зажимается в 1..9. Точке, где у каждой кассы
    /// своя буква, ставить 1: буква сама разводит кассы.</summary>
    int TillCount { get; set; }

    /// <summary>Буква кассы перед числом, до 3 символов, прибавляется как
    /// есть: «A-» даёт «A-123». Пусто — буквы нет.</summary>
    string QueueNumberPrefix { get; set; }

    /// <summary>Границы диапазона чисел, включительно. 1..9999, Max ≥ Min.</summary>
    int QueueNumberMin { get; set; }
    int QueueNumberMax { get; set; }

    /// <summary>Выключенное перемешивание выдаёт номера по порядку — и
    /// позволяет посчитать оборот по двум талонам. Осознанный выбор точки.</summary>
    bool QueueNumberShuffle { get; set; }
```

Минимальные реализации (без клэмпов, они придут в Task 3):

```csharp
// src/VvCash/Services/SettingsService.cs — в SettingsData после TillIndex:
    public int TillCount { get; set; } = QueueNumberOptions.DefaultTillCount;
    public string QueueNumberPrefix { get; set; } = string.Empty;
    public int QueueNumberMin { get; set; } = QueueNumberOptions.DefaultMin;
    public int QueueNumberMax { get; set; } = QueueNumberOptions.DefaultMax;
    public bool QueueNumberShuffle { get; set; } = true;

// в SettingsService после TillIndex:
    public int TillCount
    {
        get => _data.TillCount;
        set => _data.TillCount = value;
    }

    public string QueueNumberPrefix
    {
        get => _data.QueueNumberPrefix;
        set => _data.QueueNumberPrefix = value ?? string.Empty;
    }

    public int QueueNumberMin
    {
        get => _data.QueueNumberMin;
        set => _data.QueueNumberMin = value;
    }

    public int QueueNumberMax
    {
        get => _data.QueueNumberMax;
        set => _data.QueueNumberMax = value;
    }

    public bool QueueNumberShuffle
    {
        get => _data.QueueNumberShuffle;
        set => _data.QueueNumberShuffle = value;
    }
```

Три фейка в тестах — добавить в каждый после `public int TillIndex { get; set; }`:

```csharp
        public int TillCount { get; set; } = 5;
        public string QueueNumberPrefix { get; set; } = string.Empty;
        public int QueueNumberMin { get; set; } = 100;
        public int QueueNumberMax { get; set; } = 999;
        public bool QueueNumberShuffle { get; set; } = true;
```

в `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` (класс `FakeQueueSettings`),
`tests/VvCash.Tests/QueueServerHostTest.cs` (класс `FakeSettings`),
`tests/VvCash.Tests/SettingsViewModelTest.cs` (класс `FakeSettings`).

- [x] **Step 4: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueNumberOptionsTest"`
Expected: все зелёные.

- [x] **Step 5: Commit**

```bash
git add src/VvCash/Services/Queue/QueueNumberOptions.cs src/VvCash/Services/Queue/IQueueSettings.cs src/VvCash/Services/SettingsService.cs tests/VvCash.Tests/QueueNumberOptionsTest.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs tests/VvCash.Tests/QueueServerHostTest.cs tests/VvCash.Tests/SettingsViewModelTest.cs
git commit -m "feat(queue): snapshot the number-format settings as QueueNumberOptions"
```

---

### Task 2: `QueueNumberSlice` — формула среза в одном месте

**Files:**
- Create: `src/VvCash/Services/Queue/QueueNumberSlice.cs`
- Create: `tests/VvCash.Tests/QueueNumberSliceTest.cs`

- [x] **Step 1: Write the failing tests**

```csharp
// tests/VvCash.Tests/QueueNumberSliceTest.cs
using System.Linq;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueNumberSliceTest
{
    private static QueueNumberOptions Options(
        int tillIndex = 0, int tillCount = 5, int min = 100, int max = 999, bool shuffle = true)
        => new(tillIndex, tillCount, min, max, shuffle, "", "s");

    /// <summary>Регрессия: при настройках по умолчанию срез обязан совпасть с
    /// прежней константной формулой n % 5 == TillIndex — иначе кассы,
    /// обновившиеся в разное время одного дня, столкнутся.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void TheDefaultShapeIsTheLegacyModuloSlice(int tillIndex)
    {
        var slice = QueueNumberSlice.Ascending(Options(tillIndex));

        var legacy = Enumerable.Range(100, 900).Where(n => n % 5 == tillIndex).ToArray();
        Assert.Equal(legacy, slice);
        Assert.Equal(180, slice.Length);
    }

    [Fact]
    public void ShuffledSlicesOfAllTillsPartitionTheRange()
    {
        var all = Enumerable.Range(0, 3)
            .SelectMany(i => QueueNumberSlice.Ascending(Options(i, tillCount: 3, min: 7, max: 63)))
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(Enumerable.Range(7, 57).ToArray(), all);
    }

    [Fact]
    public void SequentialSlicesAreContiguousBlocks()
    {
        var first = QueueNumberSlice.Ascending(Options(0, tillCount: 2, min: 1, max: 99, shuffle: false));
        var second = QueueNumberSlice.Ascending(Options(1, tillCount: 2, min: 1, max: 99, shuffle: false));

        Assert.Equal(Enumerable.Range(1, 50).ToArray(), first);
        Assert.Equal(Enumerable.Range(51, 49).ToArray(), second);
    }

    [Fact]
    public void ASingleTillOwnsTheWholeRange()
    {
        Assert.Equal(Enumerable.Range(1, 99).ToArray(),
            QueueNumberSlice.Ascending(Options(0, tillCount: 1, min: 1, max: 99)));
        Assert.Equal(Enumerable.Range(1, 99).ToArray(),
            QueueNumberSlice.Ascending(Options(0, tillCount: 1, min: 1, max: 99, shuffle: false)));
    }

    [Fact]
    public void ATillPastTheRangeGetsAnEmptySlice()
    {
        Assert.Empty(QueueNumberSlice.Ascending(Options(4, tillCount: 5, min: 1, max: 3)));
        Assert.Empty(QueueNumberSlice.Ascending(Options(4, tillCount: 5, min: 1, max: 3, shuffle: false)));
    }

    [Fact]
    public void ADegenerateRangeOrTillCountYieldsNothingRatherThanThrowing()
    {
        Assert.Empty(QueueNumberSlice.Ascending(Options(0, min: 500, max: 100)));
        Assert.Empty(QueueNumberSlice.Ascending(Options(0, tillCount: 0)));
    }

    [Fact]
    public void TheSummaryNamesTheEndsWithThePrefixAndCounts()
    {
        var options = new QueueNumberOptions(1, 2, 1, 99, false, "A-", "s");

        var summary = QueueNumberSlice.Summarize(options);

        Assert.NotNull(summary);
        Assert.Equal("A-51", summary!.Value.First);
        Assert.Equal("A-99", summary.Value.Last);
        Assert.Equal(49, summary.Value.Count);
    }

    [Fact]
    public void TheSummaryOfAnEmptySliceIsNull()
    {
        Assert.Null(QueueNumberSlice.Summarize(Options(4, tillCount: 5, min: 1, max: 3)));
    }
}
```

- [x] **Step 2: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueNumberSliceTest"`
Expected: сборка падает — `QueueNumberSlice` не существует.

- [x] **Step 3: Write the slice**

```csharp
// src/VvCash/Services/Queue/QueueNumberSlice.cs
using System;
using System.Globalization;
using System.Linq;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Какие числа диапазона принадлежат этой кассе. Единственное место
/// с формулой: NumberPool раскладывает по ней пул, а предпросмотр в
/// настройках показывает по ней первый и последний номер — и они обязаны
/// совпадать.
///
/// Два правила, а не одно: при перемешивании срез по остатку
/// ((n − Min) % TillCount == TillIndex) — при 100–999 на 5 касс это в
/// точности прежнее n % 5, так что обновление не сталкивает кассы, которые
/// обновились в разное время одного дня. Без перемешивания — сплошные блоки
/// по ceil(count / TillCount), чтобы номера шли 1, 2, 3, а не 1, 3, 5: здесь
/// вид среза виден клиенту, а при шаффле — нет.</summary>
public static class QueueNumberSlice
{
    /// <summary>Номера этой кассы по возрастанию. Пусто, если срез пуст
    /// (касса за концом короткого диапазона) или настройки вырождены — не
    /// бросает: настройки нормализует SettingsService, а тесты и предпросмотр
    /// могут собрать record напрямую.</summary>
    public static int[] Ascending(QueueNumberOptions o)
    {
        var count = o.Max - o.Min + 1;
        if (count <= 0 || o.TillCount < 1 || o.TillIndex < 0) return Array.Empty<int>();

        var range = Enumerable.Range(o.Min, count);
        if (o.Shuffle)
        {
            return range.Where(n => (n - o.Min) % o.TillCount == o.TillIndex).ToArray();
        }

        var block = (count + o.TillCount - 1) / o.TillCount;
        return range.Where(n => (n - o.Min) / block == o.TillIndex).ToArray();
    }

    /// <summary>Первый и последний номер среза с префиксом и их число — для
    /// строки предпросмотра в настройках. Null для пустого среза.</summary>
    public static (string First, string Last, int Count)? Summarize(QueueNumberOptions o)
    {
        var slice = Ascending(o);
        if (slice.Length == 0) return null;
        return (QueueOrder.FormatLabel(o.Prefix, slice[0]), QueueOrder.FormatLabel(o.Prefix, slice[^1]), slice.Length);
    }
}
```

`QueueOrder.FormatLabel` ещё не существует — добавить его в модель сейчас
(остальное на модели — `Prefix`, `Label` — придёт в Task 6, но формат
нужен уже здесь, и заводить второй — значит однажды получить два разных):

```csharp
// src/VvCash/Models/QueueOrder.cs — в класс QueueOrder, после Lines:

    /// <summary>То, что видит клиент: префикс кассы и число, как есть, без
    /// разделителя — разделитель, если нужен, часть префикса («A-»).
    /// Invariant: на этой машине ru-RU, и ToString() без культуры однажды
    /// напечатает не то.</summary>
    public static string FormatLabel(string prefix, int number) =>
        prefix + number.ToString(CultureInfo.InvariantCulture);
```

и `using System.Globalization;` в начало файла.

- [x] **Step 4: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueNumberSliceTest"`
Expected: все зелёные.

- [x] **Step 5: Commit**

```bash
git add src/VvCash/Services/Queue/QueueNumberSlice.cs src/VvCash/Models/QueueOrder.cs tests/VvCash.Tests/QueueNumberSliceTest.cs
git commit -m "feat(queue): one slice formula for the pool and the settings preview"
```

---

### Task 3: `SettingsService` — клэмпы и `Load()`

**Files:**
- Modify: `src/VvCash/Services/SettingsService.cs`
- Modify: `tests/VvCash.Tests/QueueSettingsTest.cs`

- [x] **Step 1: Write the failing tests**

Добавить в `QueueSettingsTest` (после `TillIndexIsClampedIntoTheSlice`):

```csharp
    [Fact]
    public void AnUntouchedRegisterKeepsTheShippedNumberShape()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("{}"));

        Assert.Equal(5, settings.TillCount);
        Assert.Equal(100, settings.QueueNumberMin);
        Assert.Equal(999, settings.QueueNumberMax);
        Assert.True(settings.QueueNumberShuffle);
        Assert.Equal(string.Empty, settings.QueueNumberPrefix);
        Assert.True(QueueNumberOptions.From(settings).ShapesTheLegacyPool);
    }

    [Fact]
    public void TheRangeIsClampedAndMaxNeverFallsBelowMin()
    {
        IQueueSettings settings = new SettingsService(WriteSettings(
            """{ "QueueNumberMin": 0, "QueueNumberMax": 99999 }"""));
        Assert.Equal(1, settings.QueueNumberMin);
        Assert.Equal(9999, settings.QueueNumberMax);

        IQueueSettings inverted = new SettingsService(WriteSettings(
            """{ "QueueNumberMin": 500, "QueueNumberMax": 200 }"""));
        Assert.Equal(500, inverted.QueueNumberMin);
        Assert.Equal(500, inverted.QueueNumberMax);
    }

    [Fact]
    public void TillCountIsClampedToOneThroughNine()
    {
        IQueueSettings zero = new SettingsService(WriteSettings("""{ "TillCount": 0 }"""));
        IQueueSettings huge = new SettingsService(WriteSettings("""{ "TillCount": 50 }"""));

        Assert.Equal(1, zero.TillCount);
        Assert.Equal(9, huge.TillCount);
    }

    [Fact]
    public void TillIndexIsClampedByTheConfiguredTillCount()
    {
        IQueueSettings settings = new SettingsService(WriteSettings(
            """{ "TillCount": 2, "TillIndex": 3 }"""));

        Assert.Equal(1, settings.TillIndex);
    }

    [Fact]
    public void ThePrefixIsTrimmedAndCut()
    {
        IQueueSettings settings = new SettingsService(WriteSettings(
            """{ "QueueNumberPrefix": "  ABCD " }"""));
        IQueueSettings missing = new SettingsService(WriteSettings(
            """{ "QueueNumberPrefix": null }"""));

        Assert.Equal("ABC", settings.QueueNumberPrefix);
        Assert.Equal(string.Empty, missing.QueueNumberPrefix);
    }
```

- [x] **Step 2: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueSettingsTest"`
Expected: `TheRangeIsClampedAndMaxNeverFallsBelowMin`, `TillCountIsClampedToOneThroughNine`, `TillIndexIsClampedByTheConfiguredTillCount`, `ThePrefixIsTrimmedAndCut` красные (нет клэмпов); `AnUntouchedRegisterKeepsTheShippedNumberShape` зелёный уже после Task 1.

- [x] **Step 3: Replace the getters with clamping ones**

В `SettingsService` заменить `TillIndex` и пять минимальных свойств из Task 1:

```csharp
    /// <summary>Зажимается в 0..TillCount-1, а не принимается как есть:
    /// значение из settings.json правится руками, и вне диапазона касса начнёт
    /// делить по чужому классу вычетов пула.</summary>
    public int TillIndex
    {
        get => QueueNumberOptions.ClampTillIndex(_data.TillIndex, TillCount);
        set => _data.TillIndex = value;
    }

    /// <summary>Клэмпы — в QueueNumberOptions, не здесь: предпросмотр в
    /// настройках считает по тем же функциям, и делить их на два места
    /// значило бы однажды показать одно, а напечатать другое.</summary>
    public int TillCount
    {
        get => QueueNumberOptions.ClampTillCount(_data.TillCount);
        set => _data.TillCount = value;
    }

    public string QueueNumberPrefix
    {
        get => QueueNumberOptions.NormalizePrefix(_data.QueueNumberPrefix);
        set => _data.QueueNumberPrefix = value ?? string.Empty;
    }

    public int QueueNumberMin
    {
        get => QueueNumberOptions.ClampMin(_data.QueueNumberMin);
        set => _data.QueueNumberMin = value;
    }

    public int QueueNumberMax
    {
        get => QueueNumberOptions.ClampMax(_data.QueueNumberMax, QueueNumberMin);
        set => _data.QueueNumberMax = value;
    }

    public bool QueueNumberShuffle
    {
        get => _data.QueueNumberShuffle;
        set => _data.QueueNumberShuffle = value;
    }
```

В `Load()` заменить строку `_data.TillIndex = Math.Clamp(_data.TillIndex, 0, NumberPool.Tills - 1);` на:

```csharp
                if (_data.QueueNumberPrefix == null)
                {
                    _data.QueueNumberPrefix = string.Empty;
                }
                _data.TillCount = QueueNumberOptions.ClampTillCount(_data.TillCount);
                _data.TillIndex = QueueNumberOptions.ClampTillIndex(_data.TillIndex, _data.TillCount);
                _data.QueueNumberMin = QueueNumberOptions.ClampMin(_data.QueueNumberMin);
                _data.QueueNumberMax = QueueNumberOptions.ClampMax(_data.QueueNumberMax, _data.QueueNumberMin);
```

`NumberPool.Tills` после этого нигде в `SettingsService` не упоминается —
сама константа уходит в Task 4.

- [x] **Step 4: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueSettingsTest"`
Expected: все зелёные, включая прежний `TillIndexIsClampedIntoTheSlice` (9 → 4 при TillCount 5).

- [x] **Step 5: Commit**

```bash
git add src/VvCash/Services/SettingsService.cs tests/VvCash.Tests/QueueSettingsTest.cs
git commit -m "feat(settings): clamp the queue number shape on read"
```

---

### Task 4: `NumberPool` читает `QueueNumberOptions`, пересобирает по `PoolKey`

**Files:**
- Modify: `src/VvCash/Services/Queue/NumberPool.cs`
- Modify: `src/VvCash/App.axaml.cs:486-490`
- Modify: `tests/VvCash.Tests/NumberPoolTest.cs`
- Modify: `tests/VvCash.Tests/QueueClientTest.cs` (6 конструкторов `NumberPool`)

- [x] **Step 1: Update the test helper and the 8 constructions so the file compiles against the new ctor**

В `NumberPoolTest`:

```csharp
    private static NumberPool Pool(int tillIndex = 0, string? db = null, Func<DateTime>? now = null)
        => Pool(() => QueueNumberOptions.Default(tillIndex, "secret"), db, now);

    private static NumberPool Pool(Func<QueueNumberOptions> options, string? db = null, Func<DateTime>? now = null)
        => new(new QueueStorage(db ?? TempDb()), options, now ?? (() => new DateTime(2026, 8, 31, 10, 0, 0)));
```

В `QueueClientTest` заменить все шесть `new NumberPool(storage, 0, "secret", Now)` на
`new NumberPool(storage, () => QueueNumberOptions.Default(0, "secret"), Now)`.

- [x] **Step 2: Write the failing tests**

Добавить в `NumberPoolTest` в конец класса:

```csharp
    /// <summary>Читает строку QueueState напрямую — PoolKey и Day не входят в
    /// публичный контракт пула, но именно они и есть предмет тестов усыновления
    /// ниже.</summary>
    private static async Task<string?> StateAsync(string db, string key)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM QueueState WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task ASequentialPoolIssuesItsBlockInOrder()
    {
        var pool = Pool(() => new QueueNumberOptions(1, 2, 1, 99, false, "", "secret"));

        var issued = new List<int>();
        for (var i = 0; i < 49; i++) issued.Add(await Issue(pool));

        Assert.Equal(Enumerable.Range(51, 49).ToArray(), issued);
    }

    [Fact]
    public async Task ASingleTillDrawsFromTheWholeRange()
    {
        var pool = Pool(() => new QueueNumberOptions(0, 1, 1, 30, true, "", "secret"));

        var issued = new List<int>();
        for (var i = 0; i < 30; i++) issued.Add(await Issue(pool));

        Assert.Equal(Enumerable.Range(1, 30), issued.OrderBy(n => n));
    }

    [Fact]
    public async Task ChangingTheRangeMidDayRebuildsThePoolOnTheNextIssue()
    {
        var db = TempDb();
        var options = QueueNumberOptions.Default(0, "secret");
        var pool = Pool(() => options, db);

        var before = await Issue(pool);
        Assert.InRange(before, 100, 999);

        options = options with { Min = 1, Max = 30 };
        var after = await Issue(pool);

        Assert.InRange(after, 1, 30);
    }

    [Fact]
    public async Task TheSameOptionsAgainDoNotRebuildThePool()
    {
        var db = TempDb();
        var pool = Pool(() => QueueNumberOptions.Default(0, "secret"), db);

        var first = await Issue(pool);
        var second = await Issue(pool);
        // Новый экземпляр над тем же файлом — перезапуск кассы.
        var restarted = Pool(() => QueueNumberOptions.Default(0, "secret"), db);
        var third = await Issue(restarted);

        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
    }

    [Fact]
    public async Task ChangingTheTillIndexMidDayRebuildsThePool()
    {
        var db = TempDb();
        var options = QueueNumberOptions.Default(0, "secret");
        var pool = Pool(() => options, db);

        Assert.Equal(0, await Issue(pool) % 5);

        options = options with { TillIndex = 3 };

        Assert.Equal(3, await Issue(pool) % 5);
    }

    /// <summary>База, которую этот код застаёт на действующей кассе: ключ Day,
    /// ключа PoolKey нет, три номера уже роздано с утра. Пересобрать нельзя —
    /// порядок детерминирован по дню, IssueSeq обнулится, и те же три номера
    /// уйдут второму клиенту. Пул усыновляется: продолжает с четвёртого.</summary>
    [Fact]
    public async Task ALegacyPoolFromTodayIsAdoptedNotRebuilt()
    {
        var db = TempDb();
        var day = new DateTime(2026, 9, 22, 10, 0, 0);
        var legacy = Pool(() => QueueNumberOptions.Default(0, "secret"), db, () => day);

        var morning = new List<int>();
        for (var i = 0; i < 3; i++) morning.Add(await Issue(legacy));

        // Превращаем базу в «старую»: PoolKey стираем, Day пишем, как писал прежний код.
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM QueueState WHERE Key = 'PoolKey';
                INSERT INTO QueueState (Key, Value) VALUES ('Day', '2026-09-22')
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            await cmd.ExecuteNonQueryAsync();
        }

        var updated = Pool(() => QueueNumberOptions.Default(0, "secret"), db, () => day);
        var fourth = await Issue(updated);

        Assert.DoesNotContain(fourth, morning);
        Assert.NotNull(await StateAsync(db, "PoolKey"));
        Assert.Equal("2026-09-22", await StateAsync(db, "Day"));
    }

    [Fact]
    public async Task ALegacyPoolIsRebuiltWhenTheSettingsAlreadyDiffer()
    {
        var db = TempDb();
        var day = new DateTime(2026, 9, 22, 10, 0, 0);
        var legacy = Pool(() => QueueNumberOptions.Default(0, "secret"), db, () => day);
        await Issue(legacy);

        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM QueueState WHERE Key = 'PoolKey';
                INSERT INTO QueueState (Key, Value) VALUES ('Day', '2026-09-22')
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            await cmd.ExecuteNonQueryAsync();
        }

        var narrowed = Pool(() => QueueNumberOptions.Default(0, "secret") with { Max = 500 }, db, () => day);

        Assert.InRange(await Issue(narrowed), 100, 500);
    }

    [Fact]
    public async Task AnEmptySliceThrowsRatherThanIssuingSomeoneElsesNumber()
    {
        var pool = Pool(() => new QueueNumberOptions(4, 5, 1, 3, true, "", "secret"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue(pool));
    }
```

- [x] **Step 3: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest"`
Expected: сборка падает — конструктор `NumberPool(QueueStorage, Func<QueueNumberOptions>, Func<DateTime>)` не существует.

- [x] **Step 4: Rewrite NumberPool's constructor, slice and pool-key logic**

В `NumberPool.cs`:

1. Удалить `internal const int Tills = 5;` вместе с его докстрингом, и `FirstNumber`/`LastNumber`.
2. Обновить класс-докстринг: «Пул номеров для одной кассы» — убрать «трёхзначных», добавить: «Форму пула (диапазон, число касс, перемешивание) задаёт QueueNumberOptions, снимок берётся на каждой выдаче; пересборка — по PoolKey, см. EnsurePoolAsync».
3. Поля и конструктор:

```csharp
    private readonly QueueStorage _storage;
    private readonly Func<QueueNumberOptions> _options;
    private readonly Func<DateTime> _now;

    // ... _semaphore без изменений ...

    /// <summary>options читается на каждой выдаче, а не один раз: экран
    /// настроек сохраняет форму номера, и следующий талон обязан выйти уже
    /// в ней — без перезапуска и без ожидания завтра. Снимок берётся один раз
    /// в начале IssueAsync, под семафором, чтобы PoolKey и срез считались от
    /// одних и тех же значений.</summary>
    public NumberPool(QueueStorage storage, Func<QueueNumberOptions> options, Func<DateTime> now)
    {
        _storage = storage;
        _options = options;
        _now = now;
    }
```

4. В `IssueAsync` — снимок и вызов пересборки:

```csharp
    public async Task<int> IssueAsync(Guid orderId)
    {
        await _storage.InitializeAsync();

        await _semaphore.WaitAsync();
        try
        {
            var options = _options();

            using var connection = new SqliteConnection(_storage.ConnectionString);
            await connection.OpenAsync();

            await EnsurePoolAsync(connection, options);

            using var transaction = connection.BeginTransaction();

            var seq = await ReadSeqAsync(connection, transaction) + 1;

            var number = await SelectNumberToIssueAsync(connection, transaction, seq)
                ?? throw new InvalidOperationException(
                    $"NumberPool for till {options.TillIndex} has no numbers to issue — the slice is empty.");
            // ... остальное тело без изменений ...
```

5. Заменить `EnsureTodaysPoolAsync` и `ShuffledSlice` целиком:

```csharp
    /// <summary>Ключ, по которому пул считается «тем же»: день и всё, что
    /// меняет состав или порядок среза. Prefix не входит — он прибавляется к
    /// уже выданному числу. Secret не входит — он влияет только на порядок
    /// при следующей пересборке, а порядок уже лежит в таблице.</summary>
    internal static string PoolKey(string day, QueueNumberOptions o) => string.Join('|',
        day,
        o.Min.ToString(CultureInfo.InvariantCulture),
        o.Max.ToString(CultureInfo.InvariantCulture),
        o.Shuffle ? "shuffle" : "sequential",
        o.TillCount.ToString(CultureInfo.InvariantCulture),
        o.TillIndex.ToString(CultureInfo.InvariantCulture));

    /// <summary>Пересобирает пул, когда сохранённый PoolKey не совпал с
    /// вычисленным — по смене дня (местное время: граница дня — граница смены
    /// в зале, а не часовой пояс сервера) или по смене любой из настроек
    /// формы. Так настройки применяются на следующем талоне. Удаление и
    /// вставка — в одной транзакции, чтобы падение посреди пересборки не
    /// могло оставить таблицу наполовину старой, наполовину новой.
    ///
    /// Наследие: до этой настройки ключом был Day, и на действующей кассе
    /// PoolKey нет. Пересобрать просто так нельзя — порядок детерминирован по
    /// дню, IssueSeq обнулится, и касса заново раздаст утренние номера. Если
    /// Day — сегодня и настройки совпадают с константами прежнего кода
    /// (ShapesTheLegacyPool), пул на диске и есть пул от этих настроек:
    /// записать PoolKey, таблицу не трогать. Иначе — пересобрать. Day после
    /// этого не пишется и не читается; ветка отмирает на следующий день.</summary>
    private async Task EnsurePoolAsync(SqliteConnection connection, QueueNumberOptions options)
    {
        var today = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var poolKey = PoolKey(today, options);

        var storedKey = await ReadStateAsync(connection, "PoolKey");
        if (storedKey == poolKey) return;

        if (storedKey == null && options.ShapesTheLegacyPool
            && await ReadStateAsync(connection, "Day") == today)
        {
            await WriteStateAsync(connection, "PoolKey", poolKey);
            return;
        }

        var slice = QueueNumberSlice.Ascending(options);
        if (options.Shuffle) Shuffle(slice, today, options);

        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM NumberPool";
            await delete.ExecuteNonQueryAsync();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO NumberPool (Number, Position, IssuedSeq, ReleasedAtSeq) VALUES ($Number, $Position, NULL, NULL)";
            var numberParam = insert.Parameters.Add("$Number", SqliteType.Integer);
            var positionParam = insert.Parameters.Add("$Position", SqliteType.Integer);

            for (var position = 0; position < slice.Length; position++)
            {
                numberParam.Value = slice[position];
                positionParam.Value = position;
                await insert.ExecuteNonQueryAsync();
            }
        }

        using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = @"
                INSERT INTO QueueState (Key, Value) VALUES ('IssueSeq', '0')
                    ON CONFLICT(Key) DO UPDATE SET Value = '0';
                INSERT INTO QueueState (Key, Value) VALUES ('PoolKey', $PoolKey)
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            ";
            state.Parameters.AddWithValue("$PoolKey", poolKey);
            await state.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<string?> ReadStateAsync(SqliteConnection connection, string key)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Value FROM QueueState WHERE Key = $Key";
        read.Parameters.AddWithValue("$Key", key);
        return (await read.ExecuteScalarAsync()) as string;
    }

    private static async Task WriteStateAsync(SqliteConnection connection, string key, string value)
    {
        using var write = connection.CreateCommand();
        write.CommandText = @"
            INSERT INTO QueueState (Key, Value) VALUES ($Key, $Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        write.Parameters.AddWithValue("$Key", key);
        write.Parameters.AddWithValue("$Value", value);
        await write.ExecuteNonQueryAsync();
    }

    /// <summary>Фишер—Йетс на потоке QueueShuffleKeystream (день, индекс
    /// кассы, общий секрет — см. его докстринг о том, почему не
    /// System.Random). Детерминированно по дню, так что перезапуск посреди
    /// дня (TheShuffleIsStableAcrossRestartsWithinADay) воспроизводит тот же
    /// порядок; на секрете, а не только на дате, чтобы порядок нельзя было
    /// предсказать по одному талону и дате на нём.</summary>
    private static void Shuffle(int[] slice, string day, QueueNumberOptions options)
    {
        var keystream = new QueueShuffleKeystream(day, options.TillIndex, options.Secret);
        for (var i = slice.Length - 1; i > 0; i--)
        {
            var j = keystream.NextIndex(i + 1);
            (slice[i], slice[j]) = (slice[j], slice[i]);
        }
    }
```

6. `using System.Linq;` больше не нужен (`Enumerable.Range` ушёл в `QueueNumberSlice`) — удалить.

- [x] **Step 5: Update the DI registration**

В `App.axaml.cs` заменить регистрацию `INumberPool`:

```csharp
        // Singleton, not transient: NumberPool serialises issue/release through its own
        // in-process semaphore (see its own class remarks), which only means anything with
        // exactly one live instance for the whole till. The options snapshot is taken on
        // every issue (see NumberPool's constructor remarks), so a number-format edit on the
        // settings screen shows on the very next ticket — no restart, no waiting for
        // tomorrow.
        services.AddSingleton<INumberPool>(sp =>
        {
            var settings = sp.GetRequiredService<IQueueSettings>();
            return new NumberPool(
                sp.GetRequiredService<QueueStorage>(), () => QueueNumberOptions.From(settings), () => DateTime.Now);
        });
```

- [x] **Step 6: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest|FullyQualifiedName~QueueClientTest"`
Expected: все зелёные — старые (срез 100–999 % 5, перешаффл по дню) и новые. (Кулдаун на
этом шаге ещё жив — его снесёт Task 5; как он выглядит после сноса — свежий →
свободный → живой, без кулдауна — см. там.)

Затем сборка приложения: `dotnet build src/VvCash/VvCash.csproj -o build/verify` — `0 Error(s)`.

- [x] **Step 7: Commit**

```bash
git add src/VvCash/Services/Queue/NumberPool.cs src/VvCash/App.axaml.cs tests/VvCash.Tests/NumberPoolTest.cs tests/VvCash.Tests/QueueClientTest.cs
git commit -m "feat(queue): rebuild the number pool whenever its shape changes, not only at day rollover"
```

---

### Task 5: Исчерпание — свободный номер раньше живого, кулдаун удалён

> **Выполнено с отклонением от первой редакции плана** (коммиты `9409a3b`,
> и docs-фикс следом). Первая редакция сохраняла кулдаун и делила третью
> ветку на 3а/3б; при исполнении выяснилось, что под порядком «свежий →
> свободный → живой» кулдаун доказуемо мёртв, а пять существующих тестов
> фиксировали не ширину кулдауна, а «отбери 49 живых номеров прежде чем
> вернуть свободный». Спека переписана (`c9879e3`, решение 6). Ниже —
> фактическое содержание задачи.

**Files:**
- Modify: `src/VvCash/Services/Queue/NumberPool.cs` — `CooldownIssues` и
  ветка 2 удалены; `SelectNumberToIssueAsync(connection, transaction)` без
  `seq`: свежий по `Position` → свободный по `ReleasedAtSeq` → живой по
  `IssuedSeq`; докстринги `SelectNumberToIssueAsync`, `ReleaseAsync`,
  `ReadSeqAsync` без кулдауна.
- Modify: `src/VvCash/Services/Queue/INumberPool.cs` — докстринг `ReleaseAsync`.
- Modify: `tests/VvCash.Tests/NumberPoolTest.cs` —
  `AFreedNumberReturnsAfterEveryFreshAndEarlierFreedNumber` (100 из 180
  выдано, первый освобождён → 80 свежих, затем он; A и B освобождены с
  выдачей между ними → A, затем B),
  `ARepeatedReleaseDoesNotMoveTheNumberBackInTheFreedQueue`,
  `AStaleReleaseForAReissuedNumberDoesNotFreeALiveOrder` (переиздание на
  следующей выдаче), `ExhaustionPrefersAFreedNumberOverAliveOne`,
  `ExhaustionWithNothingFreedTakesTheOldestLiveNumber`.
- Modify: `tests/VvCash.Tests/QueueClientTest.cs` —
  `RepeatedFlushesOfTheSameClosedOrderDoNotMoveItInTheFreedQueue`,
  `AStaleClosedOrderReplayDoesNotFreeANumberIssuedToSomeoneElse` без цикла
  на 49 выдач.

Ловушка, пойманная при исполнении: два `ReleaseAsync` подряд без выдачи
между ними получают один `ReleasedAtSeq`, и `ORDER BY ReleasedAtSeq LIMIT 1`
решает по rowid — тест на порядок возврата обязан разделять освобождения
выдачей.

- [x] Реализовано, проверено на старом коде (6 дискриминирующих тестов
  красные на `NumberPool.cs` из `c9879e3`), закоммичено.

---

### Task 6: `QueueOrder.Prefix` / `Label` и колонка в хранилище

**Files:**
- Modify: `src/VvCash/Models/QueueOrder.cs`
- Modify: `src/VvCash/Services/Queue/QueueStorage.cs`
- Modify: `tests/VvCash.Tests/QueueStorageTest.cs`

- [x] **Step 1: Write the failing tests**

В `QueueStorageTest` после `ASavedOrderAppearsInTheListing`:

```csharp
    [Fact]
    public async Task ThePrefixSurvivesARoundTrip()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order();
        order.Prefix = "A-";

        await storage.SaveOrderAsync(order, order.CreatedAt);

        var stored = Assert.Single(await storage.GetLiveOrdersAsync());
        Assert.Equal("A-", stored.Prefix);
        Assert.Equal("A-305", stored.Label);
    }

    /// <summary>queue.db, созданный до колонки Prefix: строка вставлена мимо
    /// SaveOrderAsync, без Prefix вовсе — так выглядит заказ, записанный
    /// прежней версией. Читается пустым префиксом, а не падает на NULL.</summary>
    [Fact]
    public async Task AnOrderWrittenBeforeThePrefixColumnReadsAsUnprefixed()
    {
        var db = TempDb();
        var storage = new QueueStorage(db);
        await storage.InitializeAsync();

        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO QueueOrders (Id, Number, TillIndex, State, CreatedAt, Lines, ReceivedAt)
                VALUES ($Id, 305, 0, 'New', '2026-09-22T10:00:00.0000000', '[]', '2026-09-22T10:00:00.0000000')";
            cmd.Parameters.AddWithValue("$Id", Guid.NewGuid().ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var stored = Assert.Single(await storage.GetLiveOrdersAsync());
        Assert.Equal(string.Empty, stored.Prefix);
        Assert.Equal("305", stored.Label);
    }
```

- [x] **Step 2: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueStorageTest"`
Expected: сборка падает — у `QueueOrder` нет `Prefix`/`Label`.

- [x] **Step 3: Extend the model**

В `QueueOrder` после `public int Number { get; set; }`:

```csharp
    /// <summary>Буква кассы, как она была на момент постановки заказа: та
    /// касса, что пробила заказ, кладёт сюда свой QueueNumberPrefix.
    /// Число остаётся числом (Number) — пул, ReleaseAsync и SQL выбора
    /// работают с ним, а буква — только оформление.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Что видит клиент на талоне, табло и экране кухни. Только
    /// геттер: System.Text.Json сериализует его в JSON сервера
    /// (kds.html/board.html читают o.label) и игнорирует на входе.</summary>
    public string Label => FormatLabel(Prefix, Number);
```

`FormatLabel` уже есть с Task 2.

- [x] **Step 4: Add the column**

В `QueueStorage.InitializeCoreAsync` (метод с `CREATE TABLE`) после
`await AddColumnIfMissingAsync(command, "ALTER TABLE QueueOrders ADD COLUMN ReceivedAt TEXT;");`:

```csharp
        // Prefix — буква кассы (см. QueueOrder.Prefix). Заказы, записанные до
        // колонки, читаются пустым префиксом — так они и были напечатаны.
        await AddColumnIfMissingAsync(command, "ALTER TABLE QueueOrders ADD COLUMN Prefix TEXT;");
```

В схеме `CREATE TABLE IF NOT EXISTS QueueOrders` добавить строку `Prefix TEXT,`
после `Number INTEGER NOT NULL,` (для свежих баз; на старых — ALTER выше).

`SaveOrderAsync`:

```csharp
        command.CommandText = @"
            INSERT INTO QueueOrders
                (Id, Number, Prefix, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines, ReceivedAt)
            VALUES
                ($Id, $Number, $Prefix, $TillIndex, $State, $CreatedAt, $ReadyAt, $ClosedAt, $SaleDocumentNumber, $Lines, $ReceivedAt)
            ON CONFLICT(Id) DO NOTHING;
        ";
```

`BindOrder` — после `$Number`:

```csharp
        command.Parameters.AddWithValue("$Prefix", order.Prefix ?? string.Empty);
```

`OrderColumnsSelect` и `ReadOrder` — `Prefix` последней колонкой, чтобы не
сдвигать индексы остальных:

```csharp
    private const string OrderColumnsSelect =
        "SELECT Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines, Prefix FROM QueueOrders";

    private static QueueOrder ReadOrder(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Number = reader.GetInt32(1),
        TillIndex = reader.GetInt32(2),
        State = Enum.Parse<QueueOrderState>(reader.GetString(3)),
        CreatedAt = ParseDate(reader.GetString(4))!.Value,
        ReadyAt = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
        ClosedAt = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
        SaleDocumentNumber = reader.GetString(7),
        Lines = JsonSerializer.Deserialize<List<QueueOrderLine>>(reader.GetString(8)) ?? new(),
        Prefix = reader.IsDBNull(9) ? string.Empty : reader.GetString(9)
    };
```

Все три `SELECT`, возвращающие `QueueOrder` (`GetLiveOrdersAsync`,
`GetRecentlyClosedOrdersAsync`, `GetOrderAsync`), идут через `OrderColumnsSelect`.
Два других запроса к `QueueOrders` (`SELECT Id, ClosedAt …` в чистке и
`SELECT Id, CreatedAt, ReceivedAt …` в `CloseStaleOrdersAsync`) заказ не
собирают — их не трогать.

- [x] **Step 5: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueStorageTest|FullyQualifiedName~QueueServerTest|FullyQualifiedName~QueueDayRolloverTest|FullyQualifiedName~QueueOrderRetentionTest"`
Expected: все зелёные.

- [x] **Step 6: Commit**

```bash
git add src/VvCash/Models/QueueOrder.cs src/VvCash/Services/Queue/QueueStorage.cs tests/VvCash.Tests/QueueStorageTest.cs
git commit -m "feat(queue): carry the till prefix on the order and in queue.db"
```

---

### Task 7: `QueueClient` — префикс на заказе, `IssueNumberAsync` отдаёт label

**Files:**
- Modify: `src/VvCash/Services/Queue/IQueueClient.cs`
- Modify: `src/VvCash/Services/Queue/QueueClient.cs`
- Modify: `src/VvCash/App.axaml.cs:516-524`
- Modify: `tests/VvCash.Tests/QueueClientTest.cs`
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` (класс `FakeQueueClient`)

- [x] **Step 1: Update the constructions so the tests compile against the new ctor**

В `QueueClientTest` заменить все `tillIndex: 0` в `new QueueClient(...)` на
`() => QueueNumberOptions.Default(0, "secret")` (семь мест: `Build` и строки
~233, 298, 320, 501, 534, 564).

В `PosViewModelSellerGateTest.FakeQueueClient`:

```csharp
        public string? IssueNumberAsyncResult { get; set; }

        // ...

        public Task<string?> IssueNumberAsync()
        {
            IssueNumberAsyncCallCount++;
            return Task.FromResult(IssueNumberAsyncResult ?? Result?.Label);
        }
```

- [x] **Step 2: Write the failing tests**

В `QueueClientTest` после `AnOrderGetsANumberAndReachesTheServer`:

```csharp
    [Fact]
    public async Task TheOrderCarriesThisTillsPrefixAndIndex()
    {
        var storage = new QueueStorage(TempDb());
        var options = QueueNumberOptions.Default(2, "secret") with { Prefix = "B-" };
        var pool = new NumberPool(storage, () => options, Now);
        var client = new QueueClient(storage, pool, new FakeTransport(), () => options, Now);

        var order = await client.EnqueueAsync(Sale());

        Assert.NotNull(order);
        Assert.Equal("B-", order!.Prefix);
        Assert.Equal(2, order.TillIndex);
        Assert.Equal("B-" + order.Number, order.Label);
    }

    [Fact]
    public async Task IssueNumberAloneReturnsThePrefixedLabel()
    {
        var storage = new QueueStorage(TempDb());
        var options = QueueNumberOptions.Default(0, "secret") with { Prefix = "A-" };
        var pool = new NumberPool(storage, () => options, Now);
        var client = new QueueClient(storage, pool, new FakeTransport(), () => options, Now);

        var label = await client.IssueNumberAsync();

        Assert.NotNull(label);
        Assert.StartsWith("A-", label);
        Assert.InRange(int.Parse(label!["A-".Length..]), 100, 999);
    }

    /// <summary>Индекс кассы читается на каждом заказе, не один раз: пул уже
    /// выдаёт номера нового индекса (см. NumberPoolTest.ChangingTheTillIndexMidDayRebuildsThePool),
    /// и заказ с прежним индексом на сервере искал бы закрытые не там.</summary>
    [Fact]
    public async Task FlushAsksForClosedOrdersOnTheCurrentTillIndex()
    {
        var storage = new QueueStorage(TempDb());
        var options = QueueNumberOptions.Default(0, "secret");
        var pool = new NumberPool(storage, () => options, Now);
        var transport = new FakeTransport();
        var client = new QueueClient(storage, pool, transport, () => options, Now);

        options = options with { TillIndex = 3 };
        await client.FlushAsync();

        Assert.Equal(3, transport.LastRequestedTillIndex);
    }
```

- [x] **Step 3: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueClientTest"`
Expected: сборка падает — конструктор с `Func<QueueNumberOptions>` не существует, `IssueNumberAsync` возвращает `int?`.

- [x] **Step 4: Change the interface**

В `IQueueClient` заменить `Task<int?> IssueNumberAsync();` на:

```csharp
    /// <summary>Только номер — ни заказа, ни строки в буфере, ни попытки отправки.
    /// Для кассы с QueueRole.Off, у которой всё же настроен талонный или кухонный
    /// принтер (см. PosViewModel.ProceedToPayAsync): бумаге нужен номер, но
    /// отправлять его некуда — сервера нет, а исходящий буфер на выключенной
    /// очереди никто не дренирует (QueueFlushLoop не запускается при Off). Тот же
    /// fail-open, что и у EnqueueAsync: сбой самой БД очереди логируется и
    /// глотается, возвращает null, а не бросает исключение.
    ///
    /// Возвращает готовую строку для печати — с буквой кассы, — а не число:
    /// заказа эта ветка не создаёт и номер никогда не освобождает, так что
    /// число как таковое никому за пределами пула не нужно.</summary>
    Task<string?> IssueNumberAsync();
```

- [x] **Step 5: Change the client**

В `QueueClient`:

```csharp
    private readonly IQueueStorage _storage;
    private readonly INumberPool _pool;
    private readonly IQueueTransport _transport;
    /// <summary>Тот же снимок, что читает NumberPool — TillIndex и Prefix
    /// берутся из него на каждом заказе, а не один раз в конструкторе: иначе
    /// пул уже выдаёт номера нового индекса, а на заказе стоит старый.</summary>
    private readonly Func<QueueNumberOptions> _options;
    private readonly Func<DateTime> _now;

    // ...

    public QueueClient(IQueueStorage storage, INumberPool pool, IQueueTransport transport, Func<QueueNumberOptions> options, Func<DateTime> now)
    {
        _storage = storage;
        _pool = pool;
        _transport = transport;
        _options = options;
        _now = now;
    }

    // докстринг IssueNumberAsync — без изменений, метод:
    public async Task<string?> IssueNumberAsync()
    {
        var number = await TryIssueNumberAsync(Guid.NewGuid());
        return number == null ? null : QueueOrder.FormatLabel(_options().Prefix, number.Value);
    }

    public async Task<QueueOrder?> EnqueueAsync(SaleReceiptData sale)
    {
        var options = _options();
        var orderId = Guid.NewGuid();
        var number = await TryIssueNumberAsync(orderId);
        if (number == null)
        {
            // ... комментарий без изменений ...
            return null;
        }

        var order = new QueueOrder
        {
            Id = orderId,
            Number = number.Value,
            Prefix = options.Prefix,
            TillIndex = options.TillIndex,
            // ... остальное без изменений ...
```

В `FlushAsync`: `foreach (var closed in await _transport.GetClosedAsync(_options().TillIndex))`.

- [x] **Step 6: Update the DI registration**

В `App.axaml.cs`:

```csharp
        services.AddSingleton<IQueueClient>(sp =>
        {
            var settings = sp.GetRequiredService<IQueueSettings>();
            return new QueueClient(
                sp.GetRequiredService<IQueueStorage>(),
                sp.GetRequiredService<INumberPool>(),
                sp.GetRequiredService<IQueueTransport>(),
                () => QueueNumberOptions.From(settings),
                () => DateTime.Now);
        });
```

- [x] **Step 7: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueClientTest"`
Expected: все зелёные. `PosViewModelSellerGateTest` ещё **не** соберётся — `PosViewModel` присваивает `int?` из `IssueNumberAsync`; это Task 8, и до него не коммитить полный прогон. Сборка тестового проекта целиком упадёт на `PosViewModel.cs`, поэтому Step 7 и Task 8 делаются подряд, коммит — один на оба.

- [x] **Step 8: Continue to Task 8 before committing**

---

### Task 8: `PosViewModel` печатает label

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:2626-2663`
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`

- [x] **Step 1: Replace the number branch**

Заменить блок от `int? queueNumberValue;` до закрывающей скобки `if (queueNumberValue != null) { … }` на:

```csharp
                            string? queueNumber;
                            string queueNumberTime;
                            if (queueRoleOn)
                            {
                                // Очередь включена: заказ должен доехать до сервера, чтобы
                                // его увидели KDS и табло — даже если на этой кассе печатать
                                // талон/бегунок нечем (тот самый случай "кухонный экран без
                                // кухонного принтера").
                                var queueOrder = await _queueClient.EnqueueAsync(queueSale);
                                queueNumber = queueOrder?.Label;
                                queueNumberTime = queueOrder?.CreatedAt.ToString("HH:mm") ?? DateTime.Now.ToString("HH:mm");
                            }
                            else
                            {
                                // Очередь выключена: серверу этот заказ показать некому, так
                                // что не создаём его вовсе — только номер для бумаги.
                                queueNumber = await _queueClient.IssueNumberAsync();
                                queueNumberTime = DateTime.Now.ToString("HH:mm");
                            }

                            // Null только тогда, когда не удалось получить сам номер (см.
                            // IQueueClient docstring) — печатать талон и бегунок тогда
                            // нечем: талон без номера хуже, чем его отсутствие, а бегунок
                            // без номера теряет смысл, ради которого его вообще заводили.
                            // Чек клиенту уже напечатан строкой выше и от этого никак не
                            // зависит. Принтеры сами отфильтруют себя по роли
                            // (CompositePrinterService), так что вызывать их безопасно и
                            // тогда, когда ни один принтер не держит Ticket/KitchenOrder —
                            // именно так заказ доезжает до KDS без бумаги на этой кассе.
                            // Строка уже с буквой кассы (QueueOrder.Label) — здесь ничего
                            // не форматируется.
                            if (queueNumber != null)
                            {
                                await _printerService.PrintTicketAsync(
                                    queueNumber,
                                    time: queueNumberTime,
                                    warehouseName: null);
                                await _printerService.PrintKitchenOrderAsync(queueSale, queueNumber);
                            }
```

`using System.Globalization;` в `PosViewModel.cs` после этого не используется
(единственное обращение к `CultureInfo` было здесь) — удалить строку 4.

- [x] **Step 2: Write the failing test**

В `PosViewModelSellerGateTest` после `Pay_WithTicketAndKitchenPrintersConfiguredAndQueueOn_EnqueuesOnceAndPrintsBothCarryingTheNumber`:

```csharp
    /// <summary>The ticket and the kitchen slip print the order's Label, not its bare
    /// Number: the till letter has to reach paper, and PosViewModel must not format
    /// the number itself.</summary>
    [Fact]
    public void Pay_WithATillPrefix_PrintsThePrefixedLabelOnTicketAndSlip()
    {
        using var vm = CreateViewModel(out var deps, d =>
        {
            d.QueueSettings.QueueRole = QueueRole.Client;
            d.QueueClient.Result!.Prefix = "A-";
            d.SettingsService.Printers.Add(new PrinterConfig
            {
                IsEnabled = true,
                Roles = PrintRole.Ticket | PrintRole.KitchenOrder
            });
        });
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        var ticket = Assert.Single(deps.PrinterService.Tickets);
        Assert.Equal("A-305", ticket.Number);
        var kitchen = Assert.Single(deps.PrinterService.KitchenOrders);
        Assert.Equal("A-305", kitchen.QueueNumber);
    }
```

`deps.QueueClient` — `FakeQueueClient`, `deps.QueueSettings` —
`FakeQueueSettings` (класс `Deps`, строки ~779-780); `Result` у фейка по
умолчанию заказ с `Number = 305`, префикс ставится на нём до оплаты.

- [x] **Step 3: Run to verify**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest.Pay_"`
Expected: все зелёные, включая новый и прежние `"305"`-ассерты (пустой префикс даёт `"305"`).

- [x] **Step 4: Build the app and commit Tasks 7+8 together**

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: `0 Error(s)`.

```bash
git add src/VvCash/Services/Queue/IQueueClient.cs src/VvCash/Services/Queue/QueueClient.cs src/VvCash/App.axaml.cs src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/QueueClientTest.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(queue): print the prefixed label on the ticket and the kitchen slip"
```

---

### Task 9: Сервер отдаёт `label`, страницы его показывают

**Files:**
- Modify: `src/VvCash/Assets/Web/kds.html:209,243-244`
- Modify: `src/VvCash/Assets/Web/board.html:129,136`
- Modify: `tests/VvCash.Tests/QueueServerTest.cs`

- [x] **Step 1: Write the failing test**

В `QueueServerTest` после `AFreshServerHasNoOrders`:

```csharp
    /// <summary>kds.html и board.html читают o.label — то, что видит клиент,
    /// с буквой кассы. Сырой JSON, а не ReadFromJsonAsync&lt;QueueOrder&gt;: тот
    /// молча проигнорирует отсутствующее поле, потому что у Label нет сеттера.</summary>
    [Fact]
    public async Task TheListingCarriesThePrefixedLabel()
    {
        var order = Order();
        order.Prefix = "A-";
        var posted = await _client.PostAsJsonAsync("orders", order);
        posted.EnsureSuccessStatusCode();

        using var listing = JsonDocument.Parse(await _client.GetStringAsync("orders"));
        var first = Assert.Single(listing.RootElement.EnumerateArray());

        Assert.Equal("A-", first.GetProperty("prefix").GetString());
        Assert.Equal("A-305", first.GetProperty("label").GetString());
        Assert.Equal(305, first.GetProperty("number").GetInt32());
    }
```

`PostAsJsonAsync("orders", order)` без options — так уже постят соседние
тесты этого файла (строки ~209, 222); `System.Text.Json` и
`System.Net.Http.Json` в usings есть.

- [x] **Step 2: Run to verify it passes already**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest.TheListingCarriesThePrefixedLabel"`
Expected: зелёный — `Label` get-only, `JsonSerializerDefaults.Web` его сериализует, а `POST /orders` десериализует `QueueOrder` с `prefix`. Тест здесь фиксирует контракт для страниц, а не ловит дефект.

- [x] **Step 3: Switch the pages to `label`**

`board.html`:

```javascript
        document.getElementById('cooking').innerHTML = cooking
            .map(o => `<li>${escapeHtml(o.label)}</li>`)
            .join('');
        // ...
        document.getElementById('ready').innerHTML = ready
            .map(o => `<li class="${!isFirstSnapshot && !seen.has(o.id) ? 'fresh' : ''}">${escapeHtml(o.label)}</li>`)
            .join('');
```

В `board.html` нет `escapeHtml` — добавить рядом с `render` ту же функцию,
что в `kds.html:184` (скопировать дословно). Префикс — строка из настроек
кассы, а не число, и в HTML он обязан идти через экранирование.

`kds.html`:

```javascript
                    <span class="number">${escapeHtml(o.label)}</span>
```

и в подтверждении отмены ничего не меняется — `card.querySelector('.number').textContent`
уже берёт то, что отрисовано, теперь это label.

- [x] **Step 4: Run the static and server tests**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerStaticTest|FullyQualifiedName~QueueServerTest|FullyQualifiedName~QueueServerSocketTest"`
Expected: все зелёные.

- [x] **Step 5: Commit**

```bash
git add src/VvCash/Assets/Web/kds.html src/VvCash/Assets/Web/board.html tests/VvCash.Tests/QueueServerTest.cs
git commit -m "feat(queue): show the prefixed label on the kitchen screen and the board"
```

---

### Task 10: `SettingsViewModel` — поля и предпросмотр

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs` (свойства ~305-316, загрузка ~460-466, сохранение ~994-1011)
- Modify: `tests/VvCash.Tests/SettingsViewModelTest.cs`

- [x] **Step 1: Write the failing tests**

В `SettingsViewModelTest` после `Save_SkipsUnreadablePortAndTillIndexRatherThanOverwriting`:

```csharp
    [Fact]
    public void Constructor_LoadsTheNumberShapeFromTheService()
    {
        var settings = new FakeSettings
        {
            TillCount = 2,
            QueueNumberPrefix = "A-",
            QueueNumberMin = 1,
            QueueNumberMax = 99,
            QueueNumberShuffle = false
        };

        var vm = BuildWith(settings);

        Assert.Equal("2", vm.TillCountText);
        Assert.Equal("A-", vm.QueueNumberPrefix);
        Assert.Equal("1", vm.QueueNumberMinText);
        Assert.Equal("99", vm.QueueNumberMaxText);
        Assert.False(vm.QueueNumberShuffle);
    }

    [Fact]
    public void Save_WritesTheNumberShapeBackToTheService()
    {
        var vm = Build(out var settings);
        vm.BackendUrl = "https://api.example.test/v1/";
        vm.TillCountText = "3";
        vm.QueueNumberPrefix = "B-";
        vm.QueueNumberMinText = "10";
        vm.QueueNumberMaxText = "500";
        vm.QueueNumberShuffle = false;

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal(3, settings.TillCount);
        Assert.Equal("B-", settings.QueueNumberPrefix);
        Assert.Equal(10, settings.QueueNumberMin);
        Assert.Equal(500, settings.QueueNumberMax);
        Assert.False(settings.QueueNumberShuffle);
    }

    [Fact]
    public void Save_SkipsUnreadableNumberFieldsRatherThanOverwriting()
    {
        var settings = new FakeSettings { TillCount = 4, QueueNumberMin = 50, QueueNumberMax = 60 };
        var vm = BuildWith(settings);
        vm.BackendUrl = "https://api.example.test/v1/";
        vm.TillCountText = "many";
        vm.QueueNumberMinText = "";
        vm.QueueNumberMaxText = "lots";

        vm.SaveCommand.Execute(null);

        Assert.Equal(4, settings.TillCount);
        Assert.Equal(50, settings.QueueNumberMin);
        Assert.Equal(60, settings.QueueNumberMax);
    }

    /// <summary>Предпросмотр считает по тем же клэмпам и той же формуле среза,
    /// что и пул (QueueNumberOptions / QueueNumberSlice), — иначе экран однажды
    /// покажет одно, а талон напечатает другое. Проверяется структурная
    /// сводка, не локализованная строка: I18nService в тестах не
    /// инициализирован и отдаёт «[ключ]».</summary>
    [Fact]
    public void QueueNumberPreview_FollowsTheEnteredShape()
    {
        var vm = Build(out _);
        vm.QueueNumberPrefix = "A-";
        vm.TillCountText = "2";
        vm.TillIndexText = "1";
        vm.QueueNumberMinText = "1";
        vm.QueueNumberMaxText = "99";
        vm.QueueNumberShuffle = false;

        var preview = vm.QueueNumberPreviewData;

        Assert.NotNull(preview);
        Assert.Equal("A-51", preview!.Value.First);
        Assert.Equal("A-99", preview.Value.Last);
        Assert.Equal(49, preview.Value.Count);
    }

    [Fact]
    public void QueueNumberPreview_IsNullForAnEmptySliceOrUnreadableInput()
    {
        var vm = Build(out _);
        vm.TillCountText = "5";
        vm.TillIndexText = "4";
        vm.QueueNumberMinText = "1";
        vm.QueueNumberMaxText = "3";
        Assert.Null(vm.QueueNumberPreviewData);

        vm.QueueNumberMaxText = "not a number";
        Assert.Null(vm.QueueNumberPreviewData);
    }

    [Fact]
    public void QueueNumberPreview_RaisesPropertyChangedWhenAnyShapeFieldChanges()
    {
        var vm = Build(out _);
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.QueueNumberPreview)) raised++; };

        vm.QueueNumberPrefix = "A";
        vm.TillCountText = "2";
        vm.TillIndexText = "1";
        vm.QueueNumberMinText = "1";
        vm.QueueNumberMaxText = "50";
        vm.QueueNumberShuffle = false;

        Assert.Equal(6, raised);
    }
```

- [x] **Step 2: Run to verify they fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"`
Expected: сборка падает — нет `TillCountText`, `QueueNumberPrefix`, `QueueNumberPreviewData`.

- [x] **Step 3: Add the properties**

В `SettingsViewModel` заменить объявление `_tillIndexText` и добавить после него:

```csharp
    /// <summary>Номер этой кассы в пуле номеров очереди (см.
    /// IQueueSettings.TillIndex) — строкой по той же причине, что и
    /// QueuePortText выше. Виден на экране ВСЕГДА, а не только у клиента:
    /// сервер тоже продаёт и тоже выдаёт номера из своего среза. Две кассы
    /// с одинаковым номером начнут выдавать покупателям одинаковые номера —
    /// по этой же причине IQueueSettings.TillIndex зажимает его в
    /// 0..TillCount-1, а не принимает как есть.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreviewData))]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreview))]
    private string _tillIndexText = "0";

    /// <summary>Форма номера талона (см. IQueueSettings): буква кассы, число
    /// касс, диапазон, перемешивание. Каждое поле пересчитывает предпросмотр
    /// ниже — это единственное место, где опечатка в диапазоне видна до того,
    /// как талон не напечатался.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreviewData))]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreview))]
    private string _queueNumberPrefix = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreviewData))]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreview))]
    private string _tillCountText = QueueNumberOptions.DefaultTillCount.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreviewData))]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreview))]
    private string _queueNumberMinText = QueueNumberOptions.DefaultMin.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreviewData))]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreview))]
    private string _queueNumberMaxText = QueueNumberOptions.DefaultMax.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreviewData))]
    [NotifyPropertyChangedFor(nameof(QueueNumberPreview))]
    private bool _queueNumberShuffle = true;

    /// <summary>Первый и последний номер среза этой кассы и их число — по
    /// введённым значениям, через те же клэмпы и ту же формулу, что и пул
    /// (QueueNumberOptions, QueueNumberSlice). Null — срез пуст или поле не
    /// читается как число. Отдельно от строки QueueNumberPreview, чтобы
    /// проверять структуру, а не локализованный текст.</summary>
    public (string First, string Last, int Count)? QueueNumberPreviewData
    {
        get
        {
            if (!int.TryParse(TillCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tillCountRaw)
                || !int.TryParse(TillIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tillIndexRaw)
                || !int.TryParse(QueueNumberMinText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minRaw)
                || !int.TryParse(QueueNumberMaxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRaw))
            {
                return null;
            }

            var tillCount = QueueNumberOptions.ClampTillCount(tillCountRaw);
            var min = QueueNumberOptions.ClampMin(minRaw);
            var options = new QueueNumberOptions(
                QueueNumberOptions.ClampTillIndex(tillIndexRaw, tillCount),
                tillCount,
                min,
                QueueNumberOptions.ClampMax(maxRaw, min),
                QueueNumberShuffle,
                QueueNumberOptions.NormalizePrefix(QueueNumberPrefix),
                string.Empty);
            return QueueNumberSlice.Summarize(options);
        }
    }

    public string QueueNumberPreview => QueueNumberPreviewData is { } p
        ? string.Format(CultureInfo.InvariantCulture, I18nService.Instance["QueueNumberPreview"], p.First, p.Last, p.Count)
        : I18nService.Instance["QueueNumberPreviewEmpty"];
```

Убедиться, что в файле есть `using System.Globalization;` и `using VvCash.Services.Queue;` (второй уже есть, строка 16).

- [x] **Step 4: Load and save**

В конструкторе после `TillIndexText = queueSettings.TillIndex.ToString();`:

```csharp
            TillCountText = queueSettings.TillCount.ToString(CultureInfo.InvariantCulture);
            QueueNumberPrefix = queueSettings.QueueNumberPrefix;
            QueueNumberMinText = queueSettings.QueueNumberMin.ToString(CultureInfo.InvariantCulture);
            QueueNumberMaxText = queueSettings.QueueNumberMax.ToString(CultureInfo.InvariantCulture);
            QueueNumberShuffle = queueSettings.QueueNumberShuffle;
```

В сохранении после `if (int.TryParse(TillIndexText, out var tillIndex)) queueSettings.TillIndex = tillIndex;`:

```csharp
            // Те же правила, что у TillIndexText: нечитаемое пропускается, не
            // затирает сохранённое; клэмпы — на чтении в SettingsService.
            if (int.TryParse(TillCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tillCount))
                queueSettings.TillCount = tillCount;
            queueSettings.QueueNumberPrefix = QueueNumberPrefix;
            if (int.TryParse(QueueNumberMinText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var queueNumberMin))
                queueSettings.QueueNumberMin = queueNumberMin;
            if (int.TryParse(QueueNumberMaxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var queueNumberMax))
                queueSettings.QueueNumberMax = queueNumberMax;
            queueSettings.QueueNumberShuffle = QueueNumberShuffle;
```

Докстринг на строках ~308-316 (упоминает `NumberPool.Tills`) уже заменён в
Step 3; комментарий в сохранении на строке ~1007 (`0..NumberPool.Tills-1`)
поправить на `0..TillCount-1`.

- [x] **Step 5: Run to verify they pass**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"`
Expected: все зелёные.

- [x] **Step 6: Commit**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs tests/VvCash.Tests/SettingsViewModelTest.cs
git commit -m "feat(settings): edit the queue number shape with a live preview"
```

---

### Task 11: `SettingsView.axaml` и i18n

**Files:**
- Modify: `src/VvCash/Views/SettingsView.axaml:386-398`
- Modify: `src/VvCash/Assets/i18n/ru.json`, `en.json`, `uz.json`, `kk.json`, `tg.json`
- Modify: `tests/VvCash.Tests/I18nLocaleTest.cs`

- [x] **Step 0: Write the failing locale test**

В `I18nLocaleTest` после `DisplayProbeProgress_CarriesAllThreePlaceholders`
(тот же `Locales`/`Load`, что и там):

```csharp
    [Fact]
    public void QueueNumberKeys_ExistInEveryLocale()
    {
        // Непереведённый ключ кассир видит как "[ключ]" — не падение, а
        // подпись, которая ничего не подписывает.
        string[] keys =
        {
            "QueueNumberPrefix", "TillCount", "QueueNumberMin", "QueueNumberMax",
            "QueueNumberShuffle", "QueueNumberPreview", "QueueNumberPreviewEmpty",
            "QueueSettingsNotice",
        };

        foreach (var locale in Locales)
        {
            var map = Load(locale);
            foreach (var key in keys)
            {
                Assert.True(map.ContainsKey(key), $"{locale}.json: нет ключа {key}");
                Assert.False(string.IsNullOrWhiteSpace(map[key]), $"{locale}.json: {key} пуст");
            }
        }
    }

    [Fact]
    public void QueueNumberPreview_CarriesAllThreePlaceholders()
    {
        // string.Format с первым номером, последним и их числом — перевод,
        // потерявший {2}, покажет «A-51 … A-99» без «49 номеров».
        foreach (var locale in Locales)
        {
            var value = Load(locale)["QueueNumberPreview"];
            Assert.Contains("{0}", value);
            Assert.Contains("{1}", value);
            Assert.Contains("{2}", value);
        }
    }
```

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~I18nLocaleTest.QueueNumber"`
Expected: оба красные — ключей нет.

- [x] **Step 1: Replace the role/till grid**

Заменить `<Grid ColumnDefinitions="*, *" RowDefinitions="Auto, Auto">…</Grid>` (строки 386-398) на:

```xml
                                <Grid ColumnDefinitions="*, *, *" RowDefinitions="Auto, Auto">
                                    <TextBlock Grid.Row="0" Grid.Column="0" Text="{Binding [QueueRole], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
                                    <!-- Буква кассы, число касс и номер кассы видны всегда, не
                                         только у клиента: сервер тоже продаёт и тоже выдаёт
                                         номера из своего среза, а касса с выключенной очередью
                                         всё равно печатает талон. Две кассы с одинаковым
                                         номером начнут выдавать одинаковые номера. -->
                                    <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding [QueueNumberPrefix], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
                                    <TextBlock Grid.Row="0" Grid.Column="2" Text="{Binding [TillNumber], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,0,4"/>

                                    <ComboBox Grid.Row="1" Grid.Column="0" ItemsSource="{Binding QueueRoles}" SelectedItem="{Binding QueueRole, Mode=TwoWay}" Classes="PrinterCombo" Margin="0,0,8,0"/>
                                    <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding QueueNumberPrefix, Mode=TwoWay}" Classes="PrinterInput" MaxLength="3" Watermark="A-" Margin="0,0,8,0"/>
                                    <TextBox Grid.Row="1" Grid.Column="2" Text="{Binding TillIndexText, Mode=TwoWay}" Classes="PrinterInput"/>
                                </Grid>

                                <Grid ColumnDefinitions="*, *, *" RowDefinitions="Auto, Auto">
                                    <TextBlock Grid.Row="0" Grid.Column="0" Text="{Binding [TillCount], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
                                    <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding [QueueNumberMin], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
                                    <TextBlock Grid.Row="0" Grid.Column="2" Text="{Binding [QueueNumberMax], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,0,4"/>

                                    <TextBox Grid.Row="1" Grid.Column="0" Text="{Binding TillCountText, Mode=TwoWay}" Classes="PrinterInput" Margin="0,0,8,0"/>
                                    <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding QueueNumberMinText, Mode=TwoWay}" Classes="PrinterInput" Margin="0,0,8,0"/>
                                    <TextBox Grid.Row="1" Grid.Column="2" Text="{Binding QueueNumberMaxText, Mode=TwoWay}" Classes="PrinterInput"/>
                                </Grid>

                                <CheckBox IsChecked="{Binding QueueNumberShuffle, Mode=TwoWay}"
                                          Content="{Binding [QueueNumberShuffle], Source={x:Static services:I18nService.Instance}}"/>

                                <!-- Предпросмотр: первый и последний номер среза этой кассы по
                                     введённым значениям — те же клэмпы и формула, что у пула.
                                     Единственное место, где пустой срез виден до сохранения. -->
                                <TextBlock Text="{Binding QueueNumberPreview}" FontSize="12" Foreground="{StaticResource Slate500Brush}" TextWrapping="Wrap"/>
```

Привязки в этом репозитории рефлективные (не compiled) — опечатка в пути
собирается чисто и молча не работает. Сверить каждое имя с
`SettingsViewModel`: `QueueNumberPrefix`, `TillIndexText`, `TillCountText`,
`QueueNumberMinText`, `QueueNumberMaxText`, `QueueNumberShuffle`,
`QueueNumberPreview`.

- [x] **Step 2: Add the i18n keys**

`ru.json` — после `"TillNumber": "Номер кассы",` и заменить `QueueSettingsNotice`:

```json
  "QueueNumberPrefix": "Буква кассы",
  "TillCount": "Касс на точке",
  "QueueNumberMin": "Номера от",
  "QueueNumberMax": "до",
  "QueueNumberShuffle": "Перемешивать номера",
  "QueueNumberPreview": "Эта касса: {0} … {1}, {2} номеров",
  "QueueNumberPreviewEmpty": "Эта касса не получит ни одного номера — проверьте диапазон, число касс и номер кассы",
  "QueueSettingsNotice": "Роль, порт и секрет применяются сразу после сохранения. Формат номера — на следующем талоне; меняйте его между сменами: пересборка пула забывает, какие номера сейчас на руках. Если у каждой кассы своя буква, ставьте «Касс на точке» = 1.",
```

`en.json`:

```json
  "QueueNumberPrefix": "Till letter",
  "TillCount": "Tills on site",
  "QueueNumberMin": "Numbers from",
  "QueueNumberMax": "to",
  "QueueNumberShuffle": "Shuffle numbers",
  "QueueNumberPreview": "This till: {0} … {1}, {2} numbers",
  "QueueNumberPreviewEmpty": "This till would get no numbers at all — check the range, the till count and the till number",
  "QueueSettingsNotice": "Role, port and secret take effect as soon as you save. The number format applies from the next ticket; change it between shifts: rebuilding the pool forgets which numbers are currently out. If every till has its own letter, set \"Tills on site\" to 1.",
```

`uz.json`:

```json
  "QueueNumberPrefix": "Kassa harfi",
  "TillCount": "Nuqtadagi kassalar",
  "QueueNumberMin": "Raqamlar",
  "QueueNumberMax": "gacha",
  "QueueNumberShuffle": "Raqamlarni aralashtirish",
  "QueueNumberPreview": "Bu kassa: {0} … {1}, {2} ta raqam",
  "QueueNumberPreviewEmpty": "Bu kassa birorta ham raqam olmaydi — diapazon, kassalar soni va kassa raqamini tekshiring",
  "QueueSettingsNotice": "Rol, port va sir saqlangach darhol kuchga kiradi. Raqam formati — keyingi talondan; uni smenalar orasida o'zgartiring: pul qayta yig'ilganda qaysi raqamlar qo'lda ekani unutiladi. Har bir kassaning o'z harfi bo'lsa, «Nuqtadagi kassalar» = 1 qo'ying.",
```

`kk.json`:

```json
  "QueueNumberPrefix": "Касса әрпі",
  "TillCount": "Нүктедегі кассалар",
  "QueueNumberMin": "Нөмірлер",
  "QueueNumberMax": "дейін",
  "QueueNumberShuffle": "Нөмірлерді араластыру",
  "QueueNumberPreview": "Бұл касса: {0} … {1}, {2} нөмір",
  "QueueNumberPreviewEmpty": "Бұл касса бірде-бір нөмір алмайды — диапазонды, касса санын және касса нөмірін тексеріңіз",
  "QueueSettingsNotice": "Рөл, порт және құпия сөз сақталған бойда күшіне енеді. Нөмір пішімі — келесі талоннан; оны ауысымдар арасында өзгертіңіз: пул қайта жиналғанда қай нөмірлер қолда екені ұмытылады. Әр кассаның өз әрпі болса, «Нүктедегі кассалар» = 1 қойыңыз.",
```

`tg.json`:

```json
  "QueueNumberPrefix": "Ҳарфи касса",
  "TillCount": "Кассаҳо дар нуқта",
  "QueueNumberMin": "Рақамҳо аз",
  "QueueNumberMax": "то",
  "QueueNumberShuffle": "Омехта кардани рақамҳо",
  "QueueNumberPreview": "Ин касса: {0} … {1}, {2} рақам",
  "QueueNumberPreviewEmpty": "Ин касса ягон рақам намегирад — диапазон, шумораи кассаҳо ва рақами кассаро санҷед",
  "QueueSettingsNotice": "Нақш, порт ва рамзи махфӣ баробари захира амалӣ мешаванд. Формати рақам — аз талони оянда; онро байни бастҳо иваз кунед: ҳангоми азнавсозии ҳавз кадом рақамҳо дар даст буданашон фаромӯш мешавад. Агар ҳар касса ҳарфи худро дошта бошад, «Кассаҳо дар нуқта» = 1 гузоред.",
```

В каждом файле сохранить валидный JSON (запятые). Проверить:

```bash
Get-ChildItem src/VvCash/Assets/i18n/*.json | ForEach-Object { Get-Content $_ -Raw | ConvertFrom-Json | Out-Null; "$($_.Name) ok" }
```

Expected: пять строк `… ok`.

- [x] **Step 3: Run the locale test, build, eyeball**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~I18nLocaleTest"`
Expected: все зелёные.

Run: `dotnet build src/VvCash/VvCash.csproj -o build/verify`
Expected: `0 Error(s)`.

Запустить приложение (`run` skill или `build/verify/VvCash.exe`), открыть
Настройки → Очередь заказов, ввести букву `A-`, касс `2`, номер кассы `1`,
номера от `1` до `99`, снять галку перемешивания — строка предпросмотра
должна показать `Эта касса: A-51 … A-99, 49 номеров`. Поставить номер
кассы `4`, диапазон `1`–`3`, касс `5` — строка про «ни одного номера».

- [x] **Step 4: Commit**

```bash
git add src/VvCash/Views/SettingsView.axaml src/VvCash/Assets/i18n/ru.json src/VvCash/Assets/i18n/en.json src/VvCash/Assets/i18n/uz.json src/VvCash/Assets/i18n/kk.json src/VvCash/Assets/i18n/tg.json tests/VvCash.Tests/I18nLocaleTest.cs
git commit -m "feat(settings): queue number shape fields on the settings screen"
```

---

### Task 12: Полный прогон и статус спеки

**Files:**
- Modify: `docs/superpowers/specs/2026-09-22-configurable-queue-numbers-design.md:5`
- Modify: `docs/superpowers/specs/2026-08-31-order-queue-design.md` (раздел «Номера» — одна ссылка)

- [x] **Step 1: Full test run**

Run: `& ./run-tests.ps1`
Expected: зелёный. Упавший тест не из списка выше — прочитать стек: гонка
Avalonia Dispatcher даёт `InvalidOperationException` из `Dispatcher`, не из
кода очереди; перезапустить один раз.

- [x] **Step 2: Grep for leftovers**

```bash
grep -rn "NumberPool.Tills\|EnsureTodaysPoolAsync\|ShuffledSlice\|queueNumberValue" src tests --include=*.cs
```

Expected: пусто.

- [x] **Step 3: Mark the spec implemented**

В новой спеке строку `**Статус:** спека написана, ждёт ревью` → `**Статус:** реализовано, ветка feat/queue-number-format`.

В спеке 2026-08-31, в начале раздела `## Номера`, добавить абзац:

```markdown
> Обновлено 2026-09-22: диапазон, число касс, перемешивание и буква кассы
> стали настройками — см. `2026-09-22-configurable-queue-numbers-design.md`.
> Ниже — исходное поведение, которое осталось поведением по умолчанию.
```

- [x] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-09-22-configurable-queue-numbers-design.md docs/superpowers/specs/2026-08-31-order-queue-design.md
git commit -m "docs(queue): mark the configurable number format implemented"
```
