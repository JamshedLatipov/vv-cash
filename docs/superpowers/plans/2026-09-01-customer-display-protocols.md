# Customer Display Protocols Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Протокол дисплея покупателя выбирается в настройках, а рабочая комбинация «протокол × скорость» ищется автоподбором.

**Architecture:** Протокол становится сборщиком байтов (`IDisplayProtocol`) с каталогом по образцу `EscPosCodePages`. `VfdDisplayService` остаётся единственным владельцем порта и очереди и делегирует сборку кадра протоколу. Перебор строит `DisplayProbePlan` (чистая функция) и гоняет его из `SettingsViewModel`, отправляя на табло номер комбинации.

**Tech Stack:** .NET 10, Avalonia 11.2.3, CommunityToolkit.Mvvm, xUnit. Тесты: `& ./run-tests.ps1` (не `pwsh` — его на машине нет).

**Spec:** [2026-09-01-customer-display-protocols-design.md](../specs/2026-09-01-customer-display-protocols-design.md)

---

## Файловая структура

**Создаются:**

| Файл | Ответственность |
|---|---|
| `src/VvCash/Services/Hardware/DisplayText.cs` | Набивка колонок и формат суммы — общее для текстовых протоколов |
| `src/VvCash/Services/Hardware/IDisplayProtocol.cs` | Контракт: смысл → байты |
| `src/VvCash/Services/Hardware/Protocols/EscPosDisplayProtocol.cs` | ESC/POS, побайтово нынешнее поведение |
| `src/VvCash/Services/Hardware/Protocols/Cd5220DisplayProtocol.cs` | CD5220 |
| `src/VvCash/Services/Hardware/Protocols/NumericDisplayProtocol.cs` | Сегментное табло, только цифры |
| `src/VvCash/Services/Hardware/Protocols/RawDisplayProtocol.cs` | Голый текст без команд |
| `src/VvCash/Services/Hardware/DisplayProtocols.cs` | Каталог: `All`, `Default`, `Resolve` |
| `src/VvCash/Models/SerialFraming.cs` | Формат кадра + каталог `SerialFramings` |
| `src/VvCash/Services/Hardware/DisplayProbePlan.cs` | 28 комбинаций в фиксированном порядке |
| `tests/VvCash.Tests/DisplayProtocolTest.cs` | Побайтовый выход всех четырёх |
| `tests/VvCash.Tests/DisplayProbePlanTest.cs` | Состав и порядок плана |

**Изменяются:**

| Файл | Что |
|---|---|
| `src/VvCash/Services/Hardware/VfdDisplayService.cs` | Протокол, формат кадра, DTR/RTS; `SendAsync` на `byte[]` |
| `src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs` | Донести три новые настройки |
| `src/VvCash/Services/ISettingsService.cs` | Три новых свойства |
| `src/VvCash/Services/SettingsService.cs` | Хранение, геттеры, нормализация |
| `src/VvCash/ViewModels/SettingsViewModel.cs` | Поля, сохранение, проверка, перебор |
| `src/VvCash/Views/SettingsView.axaml` | Сетка 4×4 + панель перебора |
| `src/VvCash/Assets/i18n/{ru,en,kk,tg,uz}.json` | 11 новых ключей |
| `tests/VvCash.Tests/CustomerDisplayTest.cs` | Переезд `Build*Frame` в `EscPosDisplayProtocol` |
| `tests/VvCash.Tests/SettingsViewModelTest.cs` | Round-trip настроек, перебор, применение номера |
| `tests/VvCash.Tests/I18nLocaleTest.cs` | Новые ключи во всех локалях |

---

## Task 1: Общий текст и протокол ESC/POS

Переезд без изменения поведения. `ESCPOS` — единственная реализация, про которую известно, что она работает на живом железе в точках, поэтому её выход фиксируется побайтово.

**Files:**
- Create: `src/VvCash/Services/Hardware/DisplayText.cs`
- Create: `src/VvCash/Services/Hardware/IDisplayProtocol.cs`
- Create: `src/VvCash/Services/Hardware/Protocols/EscPosDisplayProtocol.cs`
- Create: `tests/VvCash.Tests/DisplayProtocolTest.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/VvCash.Tests/DisplayProtocolTest.cs`:

```csharp
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using VvCash.Services.Hardware.Protocols;
using Xunit;

namespace VvCash.Tests;

public class DisplayProtocolTest
{
    /// <summary>Байт в байт то, что уходило до появления протоколов: ESC @, ESC t n,
    /// затем 40 символов двумя колонками по 20. Это единственная реализация, про
    /// которую известно, что она работает на живом железе в магазинах, и переезд в
    /// отдельный класс не имеет права её сдвинуть. Ожидание собрано здесь вручную, а
    /// не вызовом того же кода — иначе тест подтверждал бы сам себя.</summary>
    [Fact]
    public void EscPos_ItemFrame_IsByteIdenticalToTheShippedFormat()
    {
        var protocol = new EscPosDisplayProtocol();
        var cp = EscPosCodePages.Cp866;

        var actual = protocol.BuildItem("Молоко", 50m, cp);

        var expected = new List<byte> { 0x1B, 0x40, 0x1B, 0x74, cp.EscTSelector };
        expected.AddRange(cp.Encoding.GetBytes("Молоко".PadRight(20) + "50.00".PadRight(20)));

        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void EscPos_TotalFrame_SaysTotalAndCarriesNoCurrency()
    {
        var protocol = new EscPosDisplayProtocol();
        var text = EscPosDisplayProtocol.BuildTotalFrame(100m);

        Assert.Equal(40, text.Length);
        Assert.StartsWith("TOTAL", text);
        Assert.Contains("100.00", text);
        Assert.DoesNotContain("$", text);
    }

    /// <summary>Пробник обязан читаться и тогда, когда кодовая страница выбрана
    /// неверно — иначе он проверял бы заодно и её, то есть отвечал бы сразу на два
    /// вопроса и ни на один внятно. Поэтому в нём нет ни ESC t, ни байтов старше
    /// 0x7F.</summary>
    [Fact]
    public void EscPos_Probe_IsPlainAsciiAndSelectsNoCodePage()
    {
        var bytes = new EscPosDisplayProtocol().BuildProbe(17);

        Assert.DoesNotContain((byte)0x74, bytes);          // ESC t не отправлялся
        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }
}
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: FAIL — компиляция не проходит, `EscPosDisplayProtocol` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/Hardware/DisplayText.cs`:

```csharp
using System.Globalization;

namespace VvCash.Services.Hardware;

/// <summary>Набивка колонок и формат суммы — то общее, что было приватным внутри
/// VfdDisplayService и понадобилось трём текстовым протоколам сразу.
///
/// Ширина колонки и «доллара нет» переехали сюда вместе: символ валюты был зашит в
/// "$" на кассах, которые долларов не берут, и правился один раз здесь же.</summary>
internal static class DisplayText
{
    public const int Columns = 20;

    public static string Pad(string text)
        => text.Length >= Columns ? text[..Columns] : text.PadRight(Columns);

    public static string Money(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);
}
```

`src/VvCash/Services/Hardware/IDisplayProtocol.cs`:

```csharp
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Один диалект табло покупателя: превращает смысл в байты и ничего не знает
/// о портах.
///
/// Отдельно от VfdDisplayService, потому что порт на кассе один, а диалектов много.
/// Транспорт с его очередью, таймаутом и catch, который держит хвост очереди живым,
/// остаётся в одном экземпляре; меняется только то, какие байты в него кладут.
///
/// Все методы чистые, поэтому проверяются без открытия порта — тем же приёмом, что
/// раньше давали статические Build*Frame.</summary>
public interface IDisplayProtocol
{
    /// <summary>То, что ложится в настройки. Хранится он, а не DisplayName: правка
    /// подписи в интерфейсе не должна ломать настроенную кассу.</summary>
    string Id { get; }

    /// <summary>Не переводится и живёт в коде — как у EscPosCodePage: название
    /// протокола опознаётся независимо от письменности.</summary>
    string DisplayName { get; }

    byte[] BuildLine(string line1, string line2, EscPosCodePage codePage);

    /// <summary>Второй параметр — итог по чеку, а не цена товара. См. одноимённое
    /// предупреждение в ICustomerDisplayService.ShowItemAsync.</summary>
    byte[] BuildItem(string name, decimal total, EscPosCodePage codePage);

    byte[] BuildTotal(decimal total, EscPosCodePage codePage);

    byte[] BuildClear(EscPosCodePage codePage);

    /// <summary>Кадр автоподбора: номер комбинации и ничего больше.
    ///
    /// Без кодовой страницы намеренно. Цифры одинаковы во всех однобайтовых таблицах,
    /// а пробник обязан читаться в том числе тогда, когда таблица выбрана неверно —
    /// иначе он проверяет заодно и её.</summary>
    byte[] BuildProbe(int number);
}
```

`src/VvCash/Services/Hardware/Protocols/EscPosDisplayProtocol.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>Epson ESC/POS — то, что касса шлёт с самого начала и что работает на
/// табло в точках.
///
/// Реализация сознательно консервативная. Инициализацию (ESC @) и выбор кодовой
/// страницы (ESC t n) понимают практически все VFD; команды позиционирования курсора
/// у моделей расходятся сильнее, чем у принтеров, поэтому их здесь нет — 40 символов
/// двумя строками по 20, и модель раскладывает их сама.</summary>
public sealed class EscPosDisplayProtocol : IDisplayProtocol
{
    public string Id => "ESCPOS";
    public string DisplayName => "ESC/POS (Epson)";

    /// <summary>Кадр строкой, отдельно от кодирования: разметку можно проверить, не
    /// думая про байты и не открывая порт.</summary>
    public static string BuildFrame(string line1, string line2)
        => DisplayText.Pad(line1) + DisplayText.Pad(line2);

    public static string BuildItemFrame(string name, decimal total)
        => BuildFrame(name, DisplayText.Money(total));

    public static string BuildTotalFrame(decimal total)
        => BuildFrame("TOTAL", DisplayText.Money(total));

    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
        => Encode(BuildFrame(line1, line2), codePage);

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => Encode(BuildItemFrame(name, total), codePage);

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => Encode(BuildTotalFrame(total), codePage);

    public byte[] BuildClear(EscPosCodePage codePage)
        => Encode(new string(' ', DisplayText.Columns * 2), codePage);

    public byte[] BuildProbe(int number)
    {
        // Только ESC @ и ASCII: ни ESC t, ни байтов старше 0x7F — см. BuildProbe
        // в IDisplayProtocol.
        var body = Encoding.ASCII.GetBytes(
            BuildFrame("PROBE", number.ToString(CultureInfo.InvariantCulture)));

        var bytes = new List<byte> { 0x1B, 0x40 };
        bytes.AddRange(body);
        return bytes.ToArray();
    }

    /// <summary>ESC @, затем ESC t n, затем текст. Без инициализации дисплей копит
    /// мусор от предыдущей строки; без кодовой страницы кириллица уходит в ASCII и
    /// превращается в вопросительные знаки.</summary>
    private static byte[] Encode(string text, EscPosCodePage codePage)
    {
        var bytes = new List<byte> { 0x1B, 0x40, 0x1B, 0x74, codePage.EscTSelector };
        bytes.AddRange(codePage.Encoding.GetBytes(text));
        return bytes.ToArray();
    }
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: PASS, 3 теста.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/DisplayText.cs src/VvCash/Services/Hardware/IDisplayProtocol.cs src/VvCash/Services/Hardware/Protocols/EscPosDisplayProtocol.cs tests/VvCash.Tests/DisplayProtocolTest.cs
git commit -m "feat(display): extract the ESC/POS frame into a protocol"
```

---

## Task 2: Протокол CD5220

**Files:**
- Create: `src/VvCash/Services/Hardware/Protocols/Cd5220DisplayProtocol.cs`
- Modify: `tests/VvCash.Tests/DisplayProtocolTest.cs`

- [ ] **Step 1: Написать падающий тест**

Дописать в `DisplayProtocolTest.cs`:

```csharp
    [Fact]
    public void Cd5220_WritesEachLineWithItsOwnCommandAndTerminator()
    {
        // ESC Q A - верхняя строка, ESC Q B - нижняя, каждая закрывается CR. Строки
        // добиваются пробелами до 20: часть клонов не гасит остаток строки по CR
        // сама, и без набивки на табло оставался бы хвост предыдущего товара.
        var protocol = new Cd5220DisplayProtocol();
        var cp = EscPosCodePages.Cp866;

        var actual = protocol.BuildItem("Молоко", 50m, cp);

        var expected = new List<byte> { 0x1B, 0x51, 0x41 };
        expected.AddRange(cp.Encoding.GetBytes("Молоко".PadRight(20)));
        expected.Add(0x0D);
        expected.AddRange(new byte[] { 0x1B, 0x51, 0x42 });
        expected.AddRange(cp.Encoding.GetBytes("50.00".PadRight(20)));
        expected.Add(0x0D);

        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void Cd5220_Probe_IsPlainAscii()
    {
        var bytes = new Cd5220DisplayProtocol().BuildProbe(17);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~Cd5220"`
Expected: FAIL — `Cd5220DisplayProtocol` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/Hardware/Protocols/Cd5220DisplayProtocol.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>CD5220 — второй по распространённости диалект после ESC/POS.
///
/// Отличается тем, что строка адресуется своей командой и закрывается CR, а не
/// сорока символами подряд: ESC Q A для верхней, ESC Q B для нижней.</summary>
public sealed class Cd5220DisplayProtocol : IDisplayProtocol
{
    private const byte Cr = 0x0D;

    public string Id => "CD5220";
    public string DisplayName => "CD5220";

    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
        => Encode(line1, line2, codePage);

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => Encode(name, DisplayText.Money(total), codePage);

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => Encode("TOTAL", DisplayText.Money(total), codePage);

    public byte[] BuildClear(EscPosCodePage codePage)
        => Encode(string.Empty, string.Empty, codePage);

    public byte[] BuildProbe(int number)
    {
        var bytes = new List<byte> { 0x1B, 0x51, 0x41 };
        bytes.AddRange(Encoding.ASCII.GetBytes(DisplayText.Pad("PROBE")));
        bytes.Add(Cr);
        bytes.AddRange(new byte[] { 0x1B, 0x51, 0x42 });
        bytes.AddRange(Encoding.ASCII.GetBytes(
            DisplayText.Pad(number.ToString(CultureInfo.InvariantCulture))));
        bytes.Add(Cr);
        return bytes.ToArray();
    }

    /// <summary>Строки добиваются пробелами до 20, хотя CR у части моделей гасит
    /// остаток сам. У другой части не гасит, и тогда на табло остаётся хвост
    /// предыдущего товара — набивка стоит двадцати байт и снимает вопрос.</summary>
    private static byte[] Encode(string line1, string line2, EscPosCodePage codePage)
    {
        var bytes = new List<byte> { 0x1B, 0x51, 0x41 };
        bytes.AddRange(codePage.Encoding.GetBytes(DisplayText.Pad(line1)));
        bytes.Add(Cr);
        bytes.AddRange(new byte[] { 0x1B, 0x51, 0x42 });
        bytes.AddRange(codePage.Encoding.GetBytes(DisplayText.Pad(line2)));
        bytes.Add(Cr);
        return bytes.ToArray();
    }
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: PASS, 5 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/Protocols/Cd5220DisplayProtocol.cs tests/VvCash.Tests/DisplayProtocolTest.cs
git commit -m "feat(display): add the CD5220 protocol"
```

---

## Task 3: Протокол сегментного табло

Здесь живёт решение «на цифровом всегда виден итог». Снаружи о существовании цифровых табло не знает никто.

**Files:**
- Create: `src/VvCash/Services/Hardware/Protocols/NumericDisplayProtocol.cs`
- Modify: `tests/VvCash.Tests/DisplayProtocolTest.cs`

- [ ] **Step 1: Написать падающий тест**

Дописать в `DisplayProtocolTest.cs`:

```csharp
    [Fact]
    public void Numeric_DropsTheItemNameAndShowsTheTotal()
    {
        // Сегментное табло букв не умеет физически. Название отбрасывается здесь, а
        // не в PosViewModel: тот шлёт один и тот же кадр всем табло и о разнице
        // между ними не знает.
        var bytes = new NumericDisplayProtocol().BuildItem("Молоко", 50m, EscPosCodePages.Cp866);

        Assert.Equal("50.00\r", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_LineWithNoDigits_Clears()
    {
        // ShowLineAsync("Thank you!", "Come again!") после оплаты. Цифр в нём нет,
        // показывать нечего - табло уходит в ноль, а не остаётся с суммой прошлого
        // покупателя.
        var bytes = new NumericDisplayProtocol().BuildLine("Thank you!", "Come again!", EscPosCodePages.Cp866);

        Assert.Equal("0.00\r", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_LineWithDigits_ShowsThem()
    {
        // Кнопка проверки шлёт "TEST 8888.88" второй строкой именно ради этой ветки:
        // на сегментном табло 8888.88 зажигает все сегменты - классическая
        // самопроверка панели, которую ни с покоем, ни с суммой не спутать.
        var bytes = new NumericDisplayProtocol().BuildLine("VV CASH", "TEST 8888.88", EscPosCodePages.Cp866);

        Assert.Equal("8888.88\r", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Numeric_Probe_IsPlainAscii()
    {
        var bytes = new NumericDisplayProtocol().BuildProbe(17);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~Numeric_"`
Expected: FAIL — `NumericDisplayProtocol` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/Hardware/Protocols/NumericDisplayProtocol.cs`:

```csharp
using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>Сегментное LED-табло: 6–8 разрядов, только цифры.
///
/// Букв такая панель не умеет физически, поэтому название товара здесь отбрасывается,
/// и на табло всегда виден итог по чеку. Решение живёт в протоколе, а не в
/// PosViewModel: тот шлёт один и тот же кадр любому табло и о разнице между ними не
/// знает.
///
/// Команд не шлёт вовсе — эти панели принимают цифры и CR.</summary>
public sealed class NumericDisplayProtocol : IDisplayProtocol
{
    private const string Empty = "0.00";

    public string Id => "NUMERIC";
    public string DisplayName => "LED / 7-segment";

    /// <summary>Из текста берутся цифры и точка, всё прочее выбрасывается. Нижняя
    /// строка вперёд верхней: у всех вызовов сумма стоит именно в ней.</summary>
    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
    {
        var digits = DigitsOf(line2);
        if (digits.Length == 0) digits = DigitsOf(line1);
        return Ascii(digits.Length == 0 ? Empty : digits);
    }

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => Ascii(DisplayText.Money(total));

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => Ascii(DisplayText.Money(total));

    public byte[] BuildClear(EscPosCodePage codePage) => Ascii(Empty);

    public byte[] BuildProbe(int number)
        => Ascii(number.ToString(CultureInfo.InvariantCulture));

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value + "\r");

    private static string DigitsOf(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsDigit(c) || c == '.') sb.Append(c);
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: PASS, 9 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/Protocols/NumericDisplayProtocol.cs tests/VvCash.Tests/DisplayProtocolTest.cs
git commit -m "feat(display): add the numeric segment-display protocol"
```

---

## Task 4: Протокол без команд

**Files:**
- Create: `src/VvCash/Services/Hardware/Protocols/RawDisplayProtocol.cs`
- Modify: `tests/VvCash.Tests/DisplayProtocolTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
    [Fact]
    public void Raw_SendsFortyCharactersAndNotOneCommandByte()
    {
        // Для табло, которые принимают текст как есть. Ни одного управляющего байта:
        // именно этим RAW и отличается от ESCPOS, и именно это надо сторожить -
        // добавь сюда ESC @ "на всякий случай", и протокол перестанет быть собой.
        var cp = EscPosCodePages.Cp866;
        var actual = new RawDisplayProtocol().BuildItem("Молоко", 50m, cp);

        Assert.Equal(cp.Encoding.GetBytes("Молоко".PadRight(20) + "50.00".PadRight(20)), actual);
        Assert.All(actual, b => Assert.True(b >= 0x20));
    }

    [Fact]
    public void Raw_Probe_IsPlainAscii()
    {
        var bytes = new RawDisplayProtocol().BuildProbe(17);

        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        Assert.Contains("17", Encoding.ASCII.GetString(bytes));
    }
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~Raw_Sends"`
Expected: FAIL — `RawDisplayProtocol` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/Hardware/Protocols/RawDisplayProtocol.cs`:

```csharp
using System.Globalization;
using System.Text;
using VvCash.Models;

namespace VvCash.Services.Hardware.Protocols;

/// <summary>Голый текст: 40 символов, ни одного управляющего байта.
///
/// Для табло, которые принимают строку как есть. Отсутствие команд здесь — не
/// упрощение, а само содержание протокола: любая добавленная сюда команда сделает его
/// вторым ESCPOS и лишит смысла.</summary>
public sealed class RawDisplayProtocol : IDisplayProtocol
{
    public string Id => "RAW";
    public string DisplayName => "Plain text (no commands)";

    public byte[] BuildLine(string line1, string line2, EscPosCodePage codePage)
        => codePage.Encoding.GetBytes(DisplayText.Pad(line1) + DisplayText.Pad(line2));

    public byte[] BuildItem(string name, decimal total, EscPosCodePage codePage)
        => BuildLine(name, DisplayText.Money(total), codePage);

    public byte[] BuildTotal(decimal total, EscPosCodePage codePage)
        => BuildLine("TOTAL", DisplayText.Money(total), codePage);

    public byte[] BuildClear(EscPosCodePage codePage)
        => codePage.Encoding.GetBytes(new string(' ', DisplayText.Columns * 2));

    public byte[] BuildProbe(int number)
        => Encoding.ASCII.GetBytes(
            DisplayText.Pad("PROBE") + DisplayText.Pad(number.ToString(CultureInfo.InvariantCulture)));
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: PASS, 11 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/Protocols/RawDisplayProtocol.cs tests/VvCash.Tests/DisplayProtocolTest.cs
git commit -m "feat(display): add the plain-text protocol"
```

---

## Task 5: Каталог протоколов

**Files:**
- Create: `src/VvCash/Services/Hardware/DisplayProtocols.cs`
- Modify: `tests/VvCash.Tests/DisplayProtocolTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AEDEX")]
    public void Resolve_EmptyOrUnknown_IsEscPos(string? id)
    {
        // Это и есть то, что делает обновление пустым для уже настроенных касс: у них
        // в settings.json нового ключа нет вовсе, и они обязаны продолжить работать
        // ровно так же, как работали.
        Assert.Same(DisplayProtocols.EscPos, DisplayProtocols.Resolve(id));
    }

    [Fact]
    public void Resolve_KnownId_IsCaseInsensitive()
    {
        Assert.Same(DisplayProtocols.Cd5220, DisplayProtocols.Resolve("cd5220"));
    }

    [Fact]
    public void Catalog_HoldsFourProtocolsWithDistinctIds()
    {
        Assert.Equal(4, DisplayProtocols.All.Count);
        Assert.Equal(4, DisplayProtocols.All.Select(p => p.Id).Distinct().Count());
        Assert.Same(DisplayProtocols.EscPos, DisplayProtocols.Default);
    }
```

Дописать в начало файла `using System.Linq;`.

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: FAIL — `DisplayProtocols` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/Hardware/DisplayProtocols.cs`:

```csharp
using System;
using System.Collections.Generic;
using VvCash.Services.Hardware.Protocols;

namespace VvCash.Services.Hardware;

/// <summary>Каталог диалектов табло, по образцу EscPosCodePages.
///
/// Не редактируется из интерфейса: кассир не должен иметь возможности задать диалект,
/// которого не существует. Новая запись — правка этого файла и релиз.
///
/// AEDEX сюда сознательно не попал: точных байтов протокола нет, проверить негде, а
/// реализация по памяти удлинила бы автоподбор с 28 шагов до 35 и при этом
/// называлась бы поддержкой. Понадобится — добавляется одной строкой.</summary>
public static class DisplayProtocols
{
    public static readonly IDisplayProtocol EscPos = new EscPosDisplayProtocol();
    public static readonly IDisplayProtocol Cd5220 = new Cd5220DisplayProtocol();
    public static readonly IDisplayProtocol Numeric = new NumericDisplayProtocol();
    public static readonly IDisplayProtocol Raw = new RawDisplayProtocol();

    public static IReadOnlyList<IDisplayProtocol> All { get; } =
        Array.AsReadOnly(new[] { EscPos, Cd5220, Numeric, Raw });

    /// <summary>Чем становится касса, у которой настройку не трогали. ESC/POS —
    /// то, что она слала до появления этого каталога.</summary>
    public static IDisplayProtocol Default => EscPos;

    /// <summary>Единственное место, где Id превращается в запись. Функцией, а не
    /// веткой по месту: правило «пусто или незнакомо — значит ESC/POS» должно быть
    /// одно и проверяться тестом.</summary>
    public static IDisplayProtocol Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var protocol in All)
            {
                if (string.Equals(protocol.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return protocol;
                }
            }
        }

        return Default;
    }
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProtocolTest"`
Expected: PASS, 17 тестов (Theory даёт четыре случая).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/DisplayProtocols.cs tests/VvCash.Tests/DisplayProtocolTest.cs
git commit -m "feat(display): add the protocol catalogue"
```

---

## Task 6: Формат кадра

**Files:**
- Create: `src/VvCash/Models/SerialFraming.cs`
- Create: `tests/VvCash.Tests/SerialFramingTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/SerialFramingTest.cs`:

```csharp
using System.IO.Ports;
using System.Linq;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class SerialFramingTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9N2")]
    public void Resolve_EmptyOrUnknown_Is8N1(string? id)
    {
        // 8N1 - то, что даёт голый конструктор SerialPort, то есть нынешнее поведение
        // кассы. Обновление не должно его сдвинуть.
        Assert.Same(SerialFramings.EightN1, SerialFramings.Resolve(id));
    }

    [Fact]
    public void EightN1_MatchesWhatABareSerialPortWouldUse()
    {
        Assert.Equal(8, SerialFramings.EightN1.DataBits);
        Assert.Equal(Parity.None, SerialFramings.EightN1.Parity);
        Assert.Equal(StopBits.One, SerialFramings.EightN1.StopBits);
    }

    [Fact]
    public void SevenE1_IsTheOtherOneThatShowsUpOnPoleDisplays()
    {
        Assert.Equal(7, SerialFramings.SevenE1.DataBits);
        Assert.Equal(Parity.Even, SerialFramings.SevenE1.Parity);
        Assert.Equal(StopBits.One, SerialFramings.SevenE1.StopBits);
    }

    [Fact]
    public void Catalog_HoldsTwoFramingsWithDistinctIds()
    {
        Assert.Equal(2, SerialFramings.All.Count);
        Assert.Equal(2, SerialFramings.All.Select(f => f.Id).Distinct().Count());
        Assert.Same(SerialFramings.EightN1, SerialFramings.Default);
    }
}
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SerialFramingTest"`
Expected: FAIL — `SerialFraming` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Models/SerialFraming.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO.Ports;

namespace VvCash.Models;

/// <summary>Как устроен байт на проводе: сколько бит данных, есть ли чётность,
/// сколько стоп-бит.
///
/// Отдельная настройка, потому что касса открывала порт голым конструктором
/// SerialPort, то есть всегда 8N1, а часть табло покупателя работает на 7E1 и на 8N1
/// не отвечает вовсе.</summary>
public sealed class SerialFraming
{
    public SerialFraming(string id, string displayName, int dataBits, Parity parity, StopBits stopBits)
    {
        Id = id;
        DisplayName = displayName;
        DataBits = dataBits;
        Parity = parity;
        StopBits = stopBits;
    }

    /// <summary>То, что ложится в настройки.</summary>
    public string Id { get; }

    /// <summary>Не переводится: «8N1» опознаётся независимо от письменности.</summary>
    public string DisplayName { get; }

    public int DataBits { get; }
    public Parity Parity { get; }
    public StopBits StopBits { get; }
}

/// <summary>Каталог, по образцу EscPosCodePages. Две записи — те, что встречаются на
/// табло покупателя; остальные комбинации в природе на этих железках не попадались, и
/// пустой список выбора лучше, чем длинный список неверных ответов.</summary>
public static class SerialFramings
{
    public static readonly SerialFraming EightN1 = new("8N1", "8N1", 8, Parity.None, StopBits.One);
    public static readonly SerialFraming SevenE1 = new("7E1", "7E1", 7, Parity.Even, StopBits.One);

    public static IReadOnlyList<SerialFraming> All { get; } =
        Array.AsReadOnly(new[] { EightN1, SevenE1 });

    /// <summary>Что даёт голый конструктор SerialPort — то есть нынешнее поведение.</summary>
    public static SerialFraming Default => EightN1;

    public static SerialFraming Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var framing in All)
            {
                if (string.Equals(framing.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return framing;
                }
            }
        }

        return Default;
    }
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SerialFramingTest"`
Expected: PASS, 6 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/SerialFraming.cs tests/VvCash.Tests/SerialFramingTest.cs
git commit -m "feat(display): add the serial framing catalogue"
```

---

## Task 7: Транспорт принимает протокол, формат кадра и DTR/RTS

Очередь, таймаут и catch не трогаются — меняется только то, кто собирает байты и с какими параметрами открывается порт.

**Files:**
- Modify: `src/VvCash/Services/Hardware/VfdDisplayService.cs`
- Modify: `tests/VvCash.Tests/CustomerDisplayTest.cs`

- [ ] **Step 1: Написать падающий тест**

В `tests/VvCash.Tests/CustomerDisplayTest.cs` заменить `Vfd_DoesNotPrintACurrencySymbol` и `Vfd_RendersTwentyColumnsPerLine` на ссылки к протоколу и дописать новые:

```csharp
    [Fact]
    public void Vfd_DoesNotPrintACurrencySymbol()
    {
        // Магазины не берут доллары; на чеке это уже чинили. Кадр переехал в
        // EscPosDisplayProtocol, проверка осталась той же.
        Assert.DoesNotContain("$", EscPosDisplayProtocol.BuildTotalFrame(100m));
        Assert.Contains("100.00", EscPosDisplayProtocol.BuildTotalFrame(100m));

        Assert.DoesNotContain("$", EscPosDisplayProtocol.BuildItemFrame("Молоко", 50m));
        Assert.Contains("50.00", EscPosDisplayProtocol.BuildItemFrame("Молоко", 50m));
    }

    [Fact]
    public void Vfd_RendersTwentyColumnsPerLine()
    {
        var frame = EscPosDisplayProtocol.BuildFrame("Молоко", "50.00");

        Assert.Equal(40, frame.Length);
        Assert.StartsWith("Молоко" + new string(' ', 14), frame);
    }

    [Fact]
    public void Vfd_DefaultsToTheShippedProtocolAndFraming()
    {
        // Три необязательных параметра конструктора существуют ради вызовов, которым
        // нечего про них сказать. Их умолчания обязаны совпадать с тем, как касса
        // работала до появления протоколов, иначе «необязательный» означало бы
        // «молча меняющий поведение».
        var display = new VfdDisplayService("COM-does-not-exist", 9600, EscPosCodePages.Cp866);

        Assert.Same(DisplayProtocols.EscPos, display.Protocol);
        Assert.Same(SerialFramings.EightN1, display.Framing);
        Assert.False(display.DtrRts);
    }

    [Fact]
    public async Task Vfd_ProbeOnADeadPort_ReportsFailureLikeAnyOtherSend()
    {
        // Пробник идёт через ту же очередь и тот же catch, что и остальные кадры —
        // отдельного пути в порт у него нет.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, EscPosCodePages.Cp866);

        Assert.False(await display.ShowProbeAsync(7));
    }
```

Дописать в начало файла `using VvCash.Services.Hardware.Protocols;`.

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayTest"`
Expected: FAIL — `EscPosDisplayProtocol.BuildFrame` не виден тесту / `display.Protocol` не существует.

- [ ] **Step 3: Реализовать**

В `src/VvCash/Services/Hardware/VfdDisplayService.cs` заменить всё от объявления класса до конца файла, сохранив комментарии очереди и `SendNowAsync` дословно:

```csharp
public class VfdDisplayService : ICustomerDisplayService
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly EscPosCodePage _codePage;
    private readonly IDisplayProtocol _protocol;
    private readonly SerialFraming _framing;
    private readonly bool _dtrRts;

    /// <summary>Параметры этого экземпляра — только для теста, по образцу
    /// CompositePrinterService.Printers: иначе строка, которая доносит их из настроек
    /// до железа, не покрыта ничем, и подмена конструктора на захардкоженные значения
    /// прошла бы мимо всех тестов незамеченной.</summary>
    internal string PortName => _portName;
    internal int BaudRate => _baudRate;
    internal EscPosCodePage CodePage => _codePage;
    internal IDisplayProtocol Protocol => _protocol;
    internal SerialFraming Framing => _framing;
    internal bool DtrRts => _dtrRts;

    /// <summary>Отправки выстраиваются в цепочку, а не идут параллельно. Task.Run
    /// снял блокировку UI-потока — и вместе с ней неявную сериализацию, на которой
    /// держались неожидаемые вызовы: одно нажатие «очистить корзину» поднимает
    /// CartChanged дважды, ClearCustomerDiscount третий раз, плюс ClearAsync — четыре
    /// одновременные отправки в один COM-порт. Второй поток получил бы
    /// UnauthorizedAccessException на Open(), кадр молча потерялся бы, и какой именно
    /// уцелеет — было бы неопределено.
    ///
    /// Цепочкой, а не семафором: у двухстрочного дисплея важен порядок кадров —
    /// «товар, затем итог» и «итог, затем спасибо» читаются покупателем как
    /// последовательность, а семафор такой гарантии не даёт.</summary>
    private readonly object _queueGate = new();
    private Task<bool> _tail = Task.FromResult(true);

    /// <summary>Последние три параметра необязательные, и это не приглашение их не
    /// указывать: их умолчания в точности повторяют поведение кассы до появления
    /// протоколов, поэтому вызов, которому нечего про них сказать, ничего и не
    /// меняет. Что настройки действительно доезжают до железа, стережёт
    /// ConfiguredCustomerDisplayService и его тест, а не эти умолчания.</summary>
    public VfdDisplayService(
        string portName,
        int baudRate,
        EscPosCodePage codePage,
        IDisplayProtocol? protocol = null,
        SerialFraming? framing = null,
        bool dtrRts = false)
    {
        _portName = portName;
        _baudRate = baudRate;
        _codePage = codePage;
        _protocol = protocol ?? DisplayProtocols.Default;
        _framing = framing ?? SerialFramings.Default;
        _dtrRts = dtrRts;
    }

    public Task<bool> ShowLineAsync(string line1, string line2)
        => SendAsync(_protocol.BuildLine(line1, line2, _codePage));

    public Task<bool> ShowItemAsync(string name, decimal total)
        => SendAsync(_protocol.BuildItem(name, total, _codePage));

    public Task<bool> ShowTotalAsync(decimal total)
        => SendAsync(_protocol.BuildTotal(total, _codePage));

    public Task<bool> ClearAsync() => SendAsync(_protocol.BuildClear(_codePage));

    /// <summary>Кадр автоподбора. Не в ICustomerDisplayService: он нужен одному экрану
    /// настроек, а продаже — никогда, и место ему на конкретном классе, а не в
    /// контракте, который реализуют ещё и Null с Configured.</summary>
    public Task<bool> ShowProbeAsync(int number) => SendAsync(_protocol.BuildProbe(number));

    /// <summary>Ставит отправку в хвост очереди и возвращает её задачу, не дожидаясь
    /// её здесь. Лок защищает только само связывание с хвостом — ни одного await
    /// внутри него, компилятор бы и не разрешил, — а ContinueWith на
    /// TaskScheduler.Default сам уводит SendNowAsync с потока вызывающего, даже
    /// когда _tail уже завершён к моменту вызова: без ExecuteSynchronously
    /// продолжение всегда планируется через переданный планировщик, а не
    /// выполняется на месте. Поэтому отдельный Task.Run больше не нужен.
    ///
    /// SendNowAsync никогда не бросает — см. её catch — поэтому _tail всегда
    /// оказывается в состоянии RanToCompletion, и упавшая отправка не может
    /// застрять сама и подвесить очередь для всех, кто встанет за ней.</summary>
    private Task<bool> SendAsync(byte[] frame)
    {
        lock (_queueGate)
        {
            var queued = _tail.ContinueWith(
                _ => SendNowAsync(frame),
                TaskScheduler.Default).Unwrap();

            _tail = queued;
            return queued;
        }
    }

    private async Task<bool> SendNowAsync(byte[] frame)
    {
        try
        {
            // WriteTimeout: по умолчанию бесконечен, а порт может открыться и
            // при этом никогда не вычитать буфер — мёртвый, но ещё
            // перечисленный VFD, либо аппаратное управление потоком. Без этой
            // строки WriteAsync висит вечно, using port так и не отрабатывает,
            // дескриптор утекает на каждое сканирование, а бросить нечего —
            // catch тут не помощник. 40 байт на 9600 бод — это ~42мс на
            // проводе, 500мс — с большим запасом.
            //
            // DtrEnable/RtsEnable ставятся до Open(): часть табло без поднятых
            // линий данные не принимает, а некоторые от них ещё и питаются.
            using var port = new SerialPort(
                _portName, _baudRate, _framing.Parity, _framing.DataBits, _framing.StopBits)
            {
                WriteTimeout = 500,
                DtrEnable = _dtrRts,
                RtsEnable = _dtrRts,
            };
            port.Open();

            await port.BaseStream.WriteAsync(frame, 0, frame.Length);
            return true;
        }
        catch (Exception ex)
        {
            // Логируется, но не глотается: возвращённый false — единственное, по
            // чему кнопка проверки отличит рабочий дисплей от мёртвого порта. Это
            // же catch — единственное, что держит _tail в RanToCompletion и не
            // даёт упавшему звену подвесить очередь.
            Console.WriteLine($"VFD error: {ex.Message}");
            return false;
        }
    }
}
```

Обновить `using`-и файла на:

```csharp
using System;
using System.IO.Ports;
using System.Threading.Tasks;
using VvCash.Models;
```

и XML-комментарий класса на:

```csharp
/// <summary>Табло покупателя на последовательном порту.
///
/// Владеет портом и очередью отправок; какие именно байты уходят, решает
/// IDisplayProtocol. Разделение появилось из-за того, что порт на кассе один, а
/// диалектов у табло много: транспорт с его таймаутом и обработкой ошибок обязан
/// остаться в одном экземпляре, иначе исправление в нём теряется в одной из копий.</summary>
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayTest|FullyQualifiedName~DisplayProtocolTest"`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/VfdDisplayService.cs tests/VvCash.Tests/CustomerDisplayTest.cs
git commit -m "feat(display): let the transport take a protocol, framing and DTR/RTS"
```

---

## Task 8: Три новые настройки

**Files:**
- Modify: `src/VvCash/Services/ISettingsService.cs`
- Modify: `src/VvCash/Services/SettingsService.cs`
- Modify: **21 фейка `ISettingsService` в 19 тестовых файлах** — полный список в Step 3

> Три новых члена интерфейса ломают компиляцию каждого фейка. Их двадцать один, а не
> три-четыре, как кажется по названиям файлов: `SellerRosterServiceTest.cs` содержит
> два. Пропустить хоть один — красная сборка, а не красный тест.

- [ ] **Step 1: Написать падающий тест**

Дописать в `tests/VvCash.Tests/SettingsViewModelTest.cs`:

```csharp
    [Fact]
    public void CustomerDisplayProtocolAndFraming_RoundTripThroughSettings()
    {
        // Значения нарочно не дефолтные: подмена Save на захардкоженную запись,
        // забывшую один из трёх ключей, обязана провалить проверку, а не остаться
        // зелёной на совпадении со значением по умолчанию.
        var settings = new FakeSettings();
        var vm = BuildWith(settings);

        vm.SelectedDisplayProtocol = DisplayProtocols.Numeric;
        vm.SelectedDisplayFraming = SerialFramings.SevenE1;
        vm.CustomerDisplayDtrRts = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal("NUMERIC", settings.CustomerDisplayProtocolId);
        Assert.Equal("7E1", settings.CustomerDisplayFramingId);
        Assert.True(settings.CustomerDisplayDtrRts);
    }

    [Fact]
    public void CustomerDisplayProtocolAndFraming_AreReadBackOnOpen()
    {
        var settings = new FakeSettings
        {
            CustomerDisplayProtocolId = "CD5220",
            CustomerDisplayFramingId = "7E1",
            CustomerDisplayDtrRts = true,
        };

        var vm = BuildWith(settings);

        Assert.Same(DisplayProtocols.Cd5220, vm.SelectedDisplayProtocol);
        Assert.Same(SerialFramings.SevenE1, vm.SelectedDisplayFraming);
        Assert.True(vm.CustomerDisplayDtrRts);
    }
```

Дописать в начало файла `using VvCash.Services.Hardware;`.

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayProtocolAndFraming"`
Expected: FAIL — свойств нет ни на `ISettingsService`, ни на view model.

- [ ] **Step 3: Реализовать**

В `src/VvCash/Services/ISettingsService.cs` после `CustomerDisplayCodePageId`:

```csharp
    /// <summary>Id записи из DisplayProtocols. Пусто на кассе, где настройку не
    /// трогали; Resolve читает пустое и незнакомое как ESC/POS, поэтому обновление
    /// существующей кассы ничего не меняет.</summary>
    string CustomerDisplayProtocolId { get; set; }

    /// <summary>Id записи из SerialFramings. Пусто — 8N1, то есть то, что давал голый
    /// конструктор SerialPort до появления этой настройки.</summary>
    string CustomerDisplayFramingId { get; set; }

    /// <summary>Поднимать ли DTR и RTS при открытии порта. Часть табло без этого
    /// данные не принимает, а некоторые от этих линий ещё и питаются. По умолчанию
    /// false — так вёл себя SerialPort раньше.</summary>
    bool CustomerDisplayDtrRts { get; set; }
```

В `src/VvCash/Services/SettingsService.cs` в `SettingsData` после `CustomerDisplayCodePageId`:

```csharp
    public string CustomerDisplayProtocolId { get; set; } = string.Empty;
    public string CustomerDisplayFramingId { get; set; } = string.Empty;
    public bool CustomerDisplayDtrRts { get; set; }
```

Там же, рядом с прочими аксессорами после `CustomerDisplayCodePageId`:

```csharp
    public string CustomerDisplayProtocolId
    {
        get => _data.CustomerDisplayProtocolId;
        set => _data.CustomerDisplayProtocolId = value;
    }

    public string CustomerDisplayFramingId
    {
        get => _data.CustomerDisplayFramingId;
        set => _data.CustomerDisplayFramingId = value;
    }

    public bool CustomerDisplayDtrRts
    {
        get => _data.CustomerDisplayDtrRts;
        set => _data.CustomerDisplayDtrRts = value;
    }
```

В нормализации после блока `CustomerDisplayCodePageId`:

```csharp
                if (_data.CustomerDisplayProtocolId == null)
                {
                    _data.CustomerDisplayProtocolId = string.Empty;
                }
                if (_data.CustomerDisplayFramingId == null)
                {
                    _data.CustomerDisplayFramingId = string.Empty;
                }
```

В **каждый** фейк `ISettingsService` дописать рядом с `CustomerDisplayCodePageId`:

```csharp
        public string CustomerDisplayProtocolId { get; set; } = string.Empty;
        public string CustomerDisplayFramingId { get; set; } = string.Empty;
        public bool CustomerDisplayDtrRts { get; set; }
```

Полный список — 21 класс в 19 файлах, все под `tests/VvCash.Tests/`:

| Файл | Класс |
|---|---|
| `AuthServiceTest.cs` | `FakeSettings` |
| `CashOperationServiceTest.cs` | `FakeSettings` |
| `CompositePrinterServiceTest.cs` | `FakeSettings` |
| `CounterpartyServiceTest.cs` | `FakeSettings` |
| `CustomerDisplayTest.cs` | `FakeSettings` |
| `CustomerRegistrationViewModelTest.cs` | `FakeSettingsService` |
| `ExchangeViewModelTest.cs` | `FakeSettings` |
| `ExpenseDocumentServiceTest.cs` | `FakeSettings` |
| `PaymentCategoryServiceTest.cs` | `FakeSettings` |
| `PosViewModelSellerGateTest.cs` | `FakeSettingsService` |
| `PrinterRoutingTest.cs` | `FakeSettings` |
| `QueueServerHostTest.cs` | `FakeSettings` |
| `QuoteServiceTest.cs` | `FakeSettings` |
| `ReturnServiceTest.cs` | `FakeSettings` |
| `ReturnsViewModelTest.cs` | `FakeSettings` |
| `SellerRosterServiceTest.cs` | `FakeSettings` **и** `ThrowingBackendUrlSettings` |
| `SettingsViewModelTest.cs` | `FakeSettings` |
| `ShiftServiceTest.cs` | `FakeSettings` |
| `SyncServiceTest.cs` | `FakeSettings` |

Проверить, что ни один не пропущен:

```bash
grep -c ": ISettingsService" tests/VvCash.Tests/*.cs | grep -v ":0"
grep -c "CustomerDisplayProtocolId" tests/VvCash.Tests/*.cs | grep -v ":0"
```

Оба списка обязаны совпасть по файлам, а `SellerRosterServiceTest.cs` — дать `2` во
втором.

В `SettingsViewModel` — поля и загрузка (полностью в Task 12; здесь только то, что нужно тестам этой задачи):

```csharp
    [ObservableProperty]
    private IDisplayProtocol? _selectedDisplayProtocol = DisplayProtocols.Default;

    [ObservableProperty]
    private SerialFraming? _selectedDisplayFraming = SerialFramings.Default;

    [ObservableProperty]
    private bool _customerDisplayDtrRts;

    public IReadOnlyList<IDisplayProtocol> AvailableDisplayProtocols { get; } = DisplayProtocols.All;
    public IReadOnlyList<SerialFraming> AvailableDisplayFramings { get; } = SerialFramings.All;
```

В конструкторе рядом с `SelectedDisplayCodePage`:

```csharp
        SelectedDisplayProtocol = DisplayProtocols.Resolve(_settingsService.CustomerDisplayProtocolId);
        SelectedDisplayFraming = SerialFramings.Resolve(_settingsService.CustomerDisplayFramingId);
        CustomerDisplayDtrRts = _settingsService.CustomerDisplayDtrRts;
```

В `Save` рядом с `CustomerDisplayCodePageId`:

```csharp
        if (SelectedDisplayProtocol != null)
            _settingsService.CustomerDisplayProtocolId = SelectedDisplayProtocol.Id;
        if (SelectedDisplayFraming != null)
            _settingsService.CustomerDisplayFramingId = SelectedDisplayFraming.Id;
        _settingsService.CustomerDisplayDtrRts = CustomerDisplayDtrRts;
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/ISettingsService.cs src/VvCash/Services/SettingsService.cs src/VvCash/ViewModels/SettingsViewModel.cs tests/VvCash.Tests/
git commit -m "feat(display): store the protocol, framing and DTR/RTS settings"
```

---

## Task 9: Настройки доезжают до железа

**Files:**
- Modify: `src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs`
- Modify: `tests/VvCash.Tests/CustomerDisplayTest.cs`

- [ ] **Step 1: Написать падающий тест**

Дописать в `CustomerDisplayTest.cs`:

```csharp
    [Fact]
    public async Task ConfiguredDisplay_CarriesTheProtocolFramingAndDtrToTheHardware()
    {
        // Все три - не дефолты, ровно по той же причине, что и в тесте порта, скорости
        // и кодовой страницы рядом: подмена Rebuild на конструкцию, забывшую один из
        // параметров, обязана провалить хотя бы одну проверку, а не уцелеть на
        // случайном совпадении со значением по умолчанию.
        var settings = new FakeSettings
        {
            CustomerDisplayPort = "COM-does-not-exist",
            CustomerDisplayProtocolId = "CD5220",
            CustomerDisplayFramingId = "7E1",
            CustomerDisplayDtrRts = true,
        };

        var display = new ConfiguredCustomerDisplayService(settings);
        Assert.False(await display.ShowTotalAsync(10m));

        var vfd = Assert.IsType<VfdDisplayService>(display.Inner);
        Assert.Same(DisplayProtocols.Cd5220, vfd.Protocol);
        Assert.Same(SerialFramings.SevenE1, vfd.Framing);
        Assert.True(vfd.DtrRts);
    }
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~CarriesTheProtocolFraming"`
Expected: FAIL — `vfd.Protocol` остаётся `EscPos`.

- [ ] **Step 3: Реализовать**

В `ConfiguredCustomerDisplayService.Rebuild` заменить конструирование:

```csharp
    private void Rebuild()
    {
        var port = _settingsService.CustomerDisplayPort;

        _inner = string.IsNullOrWhiteSpace(port)
            ? new NullCustomerDisplayService()
            : new VfdDisplayService(
                port,
                _settingsService.CustomerDisplayBaudRate,
                EscPosCodePages.Resolve(_settingsService.CustomerDisplayCodePageId),
                DisplayProtocols.Resolve(_settingsService.CustomerDisplayProtocolId),
                SerialFramings.Resolve(_settingsService.CustomerDisplayFramingId),
                _settingsService.CustomerDisplayDtrRts);
    }
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayTest"`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs tests/VvCash.Tests/CustomerDisplayTest.cs
git commit -m "feat(display): carry the new settings through to the hardware"
```

---

## Task 10: План автоподбора

**Files:**
- Create: `src/VvCash/Services/Hardware/DisplayProbePlan.cs`
- Create: `tests/VvCash.Tests/DisplayProbePlanTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/DisplayProbePlanTest.cs`:

```csharp
using System.Linq;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class DisplayProbePlanTest
{
    [Fact]
    public void Plan_IsEveryProtocolAtEveryBaudRate()
    {
        var plan = DisplayProbePlan.Build();

        Assert.Equal(DisplayProtocols.All.Count * DisplayProbePlan.BaudRates.Count, plan.Count);
        Assert.Equal(28, plan.Count);
    }

    [Fact]
    public void Plan_NumbersRunFromOneWithoutGaps()
    {
        // Номер - это то, что кассир прочитал на табло и записал на бумажке. Дырка в
        // нумерации означала бы номер, который ввести можно, а применить нельзя.
        var plan = DisplayProbePlan.Build();

        Assert.Equal(Enumerable.Range(1, plan.Count), plan.Select(p => p.Number));
    }

    [Fact]
    public void Plan_OrderIsFixed()
    {
        // Порядок закреплён нарочно. Номер, увиденный на табло, обязан значить одно и
        // то же между запусками и между версиями кассы - иначе записанное кассиром
        // число превращается в мусор при первом же обновлении.
        var plan = DisplayProbePlan.Build();

        Assert.Same(DisplayProtocols.EscPos, plan[0].Protocol);
        Assert.Equal(600, plan[0].BaudRate);

        Assert.Same(DisplayProtocols.EscPos, plan[6].Protocol);
        Assert.Equal(38400, plan[6].BaudRate);

        Assert.Same(DisplayProtocols.Cd5220, plan[7].Protocol);
        Assert.Equal(600, plan[7].BaudRate);

        Assert.Same(DisplayProtocols.Raw, plan[27].Protocol);
        Assert.Equal(38400, plan[27].BaudRate);
    }

    [Fact]
    public void BaudRates_ReachBelow2400()
    {
        // Низ включён по следу живого разбора: табло гасло на всём выше 2400, и
        // перебор, начинающийся с 9600, такое не нашёл бы вовсе.
        Assert.Contains(600, DisplayProbePlan.BaudRates);
        Assert.Contains(1200, DisplayProbePlan.BaudRates);
    }

    [Fact]
    public void Find_UnknownNumber_IsNull()
    {
        Assert.Null(DisplayProbePlan.Find(0));
        Assert.Null(DisplayProbePlan.Find(29));
    }

    [Fact]
    public void Find_KnownNumber_ReturnsThatCombination()
    {
        var probe = DisplayProbePlan.Find(8);

        Assert.NotNull(probe);
        Assert.Same(DisplayProtocols.Cd5220, probe!.Protocol);
        Assert.Equal(600, probe.BaudRate);
    }
}
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProbePlanTest"`
Expected: FAIL — `DisplayProbePlan` не существует.

- [ ] **Step 3: Реализовать**

`src/VvCash/Services/Hardware/DisplayProbePlan.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace VvCash.Services.Hardware;

/// <summary>Одна комбинация автоподбора и её номер — то самое число, которое кассир
/// читает на табло.</summary>
public sealed record DisplayProbe(int Number, IDisplayProtocol Protocol, int BaudRate);

/// <summary>Что перебирает автоподбор и в каком порядке.
///
/// Чистая функция, вынесенная отдельно от экрана настроек по той же причине, что
/// CustomerDisplayPlacementSelector: решение, зависящее только от каталогов, должно
/// проверяться без Avalonia и без COM-порта.
///
/// Формат кадра и DTR/RTS сюда не входят намеренно. Они редкие, а в кресте с ними
/// перебор вырос бы с 28 шагов до 112 — почти три минуты, столько кассир за табло не
/// отследит. Не нашлось — ставятся руками, и перебор гоняется ещё раз.</summary>
public static class DisplayProbePlan
{
    /// <summary>Низ списка включён по следу живого разбора: встречалось табло, которое
    /// гасло на всём выше 2400. Перебор, начинающийся с 9600, такое не находит.</summary>
    public static IReadOnlyList<int> BaudRates { get; } =
        Array.AsReadOnly(new[] { 600, 1200, 2400, 4800, 9600, 19200, 38400 });

    private static readonly IReadOnlyList<DisplayProbe> Plan = BuildPlan();

    /// <summary>Протокол снаружи, скорость внутри: соседние номера отличаются только
    /// скоростью, и кассиру, который видит на табло два читаемых числа подряд, сразу
    /// понятно, что дело в ней, а не в диалекте.</summary>
    private static IReadOnlyList<DisplayProbe> BuildPlan()
    {
        var probes = new List<DisplayProbe>();
        var number = 1;

        foreach (var protocol in DisplayProtocols.All)
        {
            foreach (var baud in BaudRates)
            {
                probes.Add(new DisplayProbe(number, protocol, baud));
                number++;
            }
        }

        return probes.AsReadOnly();
    }

    public static IReadOnlyList<DisplayProbe> Build() => Plan;

    /// <summary>Комбинация по номеру, или null, если такого номера нет. Экран
    /// настроек отличает «кассир ошибся при вводе» от «номер есть» только по этому
    /// null.</summary>
    public static DisplayProbe? Find(int number)
    {
        foreach (var probe in Plan)
        {
            if (probe.Number == number) return probe;
        }

        return null;
    }
}
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~DisplayProbePlanTest"`
Expected: PASS, 6 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/DisplayProbePlan.cs tests/VvCash.Tests/DisplayProbePlanTest.cs
git commit -m "feat(display): add the autodetect probe plan"
```

---

## Task 11: Ключи локализации

Делается до экрана настроек, чтобы следующим задачам было на что ссылаться.

**Files:**
- Modify: `src/VvCash/Assets/i18n/{ru,en,kk,tg,uz}.json`
- Modify: `tests/VvCash.Tests/I18nLocaleTest.cs`

- [ ] **Step 1: Написать падающий тест**

В `tests/VvCash.Tests/I18nLocaleTest.cs` заменить массив `keys` в `DisplayCheckKeys_ExistInEveryLocale`:

```csharp
        string[] keys =
        {
            "DisplayCheckOk", "DisplayCheckFailed", "DisplayCheckNoPort",
            "DisplayProtocol", "DisplayFraming", "DisplayDtrRts",
            "ProbeDisplay", "StopProbe", "DisplayProbeProgress",
            "DisplayProbeNumber", "ApplyProbeNumber", "DisplayProbeBadNumber",
            "DisplayProbeApplied", "DisplayProbeDone",
        };
```

и дописать:

```csharp
    [Fact]
    public void DisplayProbeProgress_CarriesBothPlaceholders()
    {
        // Строка идёт через string.Format с номером шага и их общим числом. Перевод,
        // потерявший {1}, покажет кассиру «Подбор: 12 из» - формат не упадёт, а
        // строка станет бессмысленной, и поймать это может только проверка текста.
        foreach (var locale in Locales)
        {
            var value = Load(locale)["DisplayProbeProgress"];
            Assert.Contains("{0}", value);
            Assert.Contains("{1}", value);
        }
    }

    [Fact]
    public void DisplayProbeApplied_CarriesItsPlaceholder()
    {
        foreach (var locale in Locales)
        {
            Assert.Contains("{0}", Load(locale)["DisplayProbeApplied"]);
        }
    }
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~I18nLocaleTest"`
Expected: FAIL — «нет ключа DisplayProtocol».

- [ ] **Step 3: Реализовать**

Вставить в каждый словарь сразу после строки `"DisplayCheckNoPort"`. Файлы записаны в UTF-8 **с BOM** и с **CRLF** — редактировать так, чтобы и то и другое сохранилось, иначе diff покажет файл целиком.

`ru.json`:
```json
  "DisplayProtocol": "Протокол",
  "DisplayFraming": "Формат кадра",
  "DisplayDtrRts": "DTR/RTS",
  "ProbeDisplay": "Подобрать",
  "StopProbe": "Стоп",
  "DisplayProbeProgress": "Подбор: {0} из {1}",
  "DisplayProbeNumber": "Номер с табло",
  "ApplyProbeNumber": "Применить",
  "DisplayProbeBadNumber": "Такого номера в переборе нет",
  "DisplayProbeApplied": "Поставлены протокол и скорость из комбинации {0}",
  "DisplayProbeDone": "Перебор закончен",
```

`en.json`:
```json
  "DisplayProtocol": "Protocol",
  "DisplayFraming": "Frame format",
  "DisplayDtrRts": "DTR/RTS",
  "ProbeDisplay": "Autodetect",
  "StopProbe": "Stop",
  "DisplayProbeProgress": "Autodetect: {0} of {1}",
  "DisplayProbeNumber": "Number on the display",
  "ApplyProbeNumber": "Apply",
  "DisplayProbeBadNumber": "No such number in the sweep",
  "DisplayProbeApplied": "Protocol and baud rate set from combination {0}",
  "DisplayProbeDone": "Autodetect finished",
```

`kk.json`:
```json
  "DisplayProtocol": "Хаттама",
  "DisplayFraming": "Кадр пішімі",
  "DisplayDtrRts": "DTR/RTS",
  "ProbeDisplay": "Автотаңдау",
  "StopProbe": "Тоқтату",
  "DisplayProbeProgress": "Автотаңдау: {1} ішінен {0}",
  "DisplayProbeNumber": "Таблодағы нөмір",
  "ApplyProbeNumber": "Қолдану",
  "DisplayProbeBadNumber": "Мұндай нөмір тізімде жоқ",
  "DisplayProbeApplied": "{0} комбинациясының хаттамасы мен жылдамдығы қойылды",
  "DisplayProbeDone": "Автотаңдау аяқталды",
```

`tg.json`:
```json
  "DisplayProtocol": "Протокол",
  "DisplayFraming": "Формати кадр",
  "DisplayDtrRts": "DTR/RTS",
  "ProbeDisplay": "Худинтихоб",
  "StopProbe": "Истодан",
  "DisplayProbeProgress": "Худинтихоб: {0} аз {1}",
  "DisplayProbeNumber": "Рақам дар табло",
  "ApplyProbeNumber": "Татбиқ кардан",
  "DisplayProbeBadNumber": "Чунин рақам дар рӯйхат нест",
  "DisplayProbeApplied": "Протокол ва суръати комбинатсияи {0} гузошта шуд",
  "DisplayProbeDone": "Худинтихоб анҷом ёфт",
```

`uz.json`:
```json
  "DisplayProtocol": "Protokol",
  "DisplayFraming": "Kadr formati",
  "DisplayDtrRts": "DTR/RTS",
  "ProbeDisplay": "Avtotanlash",
  "StopProbe": "Toxtatish",
  "DisplayProbeProgress": "Avtotanlash: {1} dan {0}",
  "DisplayProbeNumber": "Tablodagi raqam",
  "ApplyProbeNumber": "Qollash",
  "DisplayProbeBadNumber": "Bunday raqam royxatda yoq",
  "DisplayProbeApplied": "{0} kombinatsiyasining protokoli va tezligi qoyildi",
  "DisplayProbeDone": "Avtotanlash tugadi",
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~I18nLocaleTest"`
Expected: PASS, 4 теста.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Assets/i18n tests/VvCash.Tests/I18nLocaleTest.cs
git commit -m "feat(display): add locale strings for the protocol settings and autodetect"
```

---

## Task 12: Проверка дисплея идёт через выбранный протокол

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`
- Modify: `tests/VvCash.Tests/SettingsViewModelTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
    [Fact]
    public async Task CheckDisplay_BuildsFromTheProtocolOnScreen_NotTheSavedOne()
    {
        // Кнопка обязана проверять то, что кассир только что выбрал, а не то, что уже
        // сохранено - иначе «проверка прошла» перестаёт значить что-либо про
        // настройку, которую сейчас подбирают. Тот же приём и та же причина, что у
        // TestPrint_BuildsFromTheCodePageOnScreen выше.
        var vm = BuildWith(new FakeSettings { CustomerDisplayPort = "COM-does-not-exist" });

        vm.SelectedDisplayProtocol = DisplayProtocols.Numeric;
        vm.SelectedDisplayFraming = SerialFramings.SevenE1;
        vm.CustomerDisplayDtrRts = true;

        await vm.CheckDisplayCommand.ExecuteAsync(null);

        Assert.Same(DisplayProtocols.Numeric, vm.LastCheckDisplayService?.Protocol);
        Assert.Same(SerialFramings.SevenE1, vm.LastCheckDisplayService?.Framing);
        Assert.True(vm.LastCheckDisplayService?.DtrRts);
    }
```

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~CheckDisplay_BuildsFromTheProtocol"`
Expected: FAIL — `LastCheckDisplayService` не существует.

- [ ] **Step 3: Реализовать**

В `SettingsViewModel` рядом с `LastTestPrintService` добавить seam:

```csharp
    /// <summary>Что построила последняя проверка дисплея. Seam ровно как
    /// LastTestPrintService рядом: только для чтения, только для теста, существует
    /// затем, чтобы проверку «кнопка строит из полей экрана, а не из сохранённого»
    /// вообще можно было написать.</summary>
    internal VfdDisplayService? LastCheckDisplayService { get; private set; }
```

Заменить конструирование в `CheckDisplay`:

```csharp
        var display = new VfdDisplayService(
            CustomerDisplayPort,
            // Тот же откат, что у Save чуть ниже по файлу, а не свои жёсткие
            // 9600: иначе нечитаемое поле проверяется на одной скорости и
            // сохраняется на другой — «проверка прошла» перестаёт значить
            // что-либо про то, на чём касса в итоге заработает.
            int.TryParse(CustomerDisplayBaudRateText, out var baud) && baud > 0 ? baud : _settingsService.CustomerDisplayBaudRate,
            SelectedDisplayCodePage ?? EscPosCodePages.Default,
            SelectedDisplayProtocol ?? DisplayProtocols.Default,
            SelectedDisplayFraming ?? SerialFramings.Default,
            CustomerDisplayDtrRts);

        LastCheckDisplayService = display;
```

И заменить проверочную строку:

```csharp
        // Вторая строка несёт цифры намеренно. На текстовом табло это по-прежнему
        // читаемая проверка, а на сегментном NumericDisplayProtocol достанет из неё
        // 8888.88 — то есть зажжёт все сегменты разом, классическую самопроверку
        // панели, которую с покоем не спутать. Убери отсюда цифры, и кнопка перестанет
        // что-либо показывать на цифровых табло.
        var send = display.ShowLineAsync("VV CASH", "TEST 8888.88");
```

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs tests/VvCash.Tests/SettingsViewModelTest.cs
git commit -m "feat(display): run the check button through the selected protocol"
```

---

## Task 13: Перебор, остановка и применение номера

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`
- Modify: `tests/VvCash.Tests/SettingsViewModelTest.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
    [Fact]
    public async Task Probe_WalksTheWholePlanAndClearsItsFlagAtTheEnd()
    {
        // Пауза подменяется, иначе тест ждал бы 42 секунды. Тот же приём, что у
        // localNetworkAddress в конструкторе рядом.
        var vm = BuildWith(
            new FakeSettings { CustomerDisplayPort = "COM-does-not-exist" },
            probeDelay: (_, _) => Task.CompletedTask);

        await vm.ProbeDisplayCommand.ExecuteAsync(null);

        Assert.Equal(DisplayProbePlan.Build().Count, vm.ProbeStepsRun);
        Assert.False(vm.IsProbing);
    }

    [Fact]
    public async Task Probe_WithNoPort_RefusesInsteadOfWalkingTheWholePlanIntoNothing()
    {
        var vm = BuildWith(new FakeSettings(), probeDelay: (_, _) => Task.CompletedTask);

        await vm.ProbeDisplayCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.ProbeStepsRun);
        Assert.Equal(I18nService.Instance["DisplayCheckNoPort"], vm.ErrorMessage);
    }

    [Fact]
    public async Task Probe_Stopped_LeavesTheRestOfThePlanUnsent()
    {
        // Стоп обязан прекращать отправки, а не только гасить надпись: кассир жмёт его
        // именно тогда, когда увидел своё число и не хочет ждать оставшуюся минуту.
        SettingsViewModel? vm = null;
        vm = BuildWith(
            new FakeSettings { CustomerDisplayPort = "COM-does-not-exist" },
            probeDelay: (_, _) => { vm!.StopProbeCommand.Execute(null); return Task.CompletedTask; });

        await vm.ProbeDisplayCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.ProbeStepsRun);
        Assert.False(vm.IsProbing);
    }

    [Fact]
    public void ApplyProbeNumber_SetsTheProtocolAndBaudRateOfThatCombination()
    {
        var vm = Build(out _);
        vm.ProbeNumberText = "8";

        vm.ApplyProbeNumberCommand.Execute(null);

        Assert.Same(DisplayProtocols.Cd5220, vm.SelectedDisplayProtocol);
        Assert.Equal("600", vm.CustomerDisplayBaudRateText);
        Assert.False(vm.HasError);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("29")]
    [InlineData("не число")]
    [InlineData("")]
    public void ApplyProbeNumber_OutsideThePlan_ReportsItAndChangesNothing(string input)
    {
        var vm = Build(out _);
        var before = vm.SelectedDisplayProtocol;
        vm.ProbeNumberText = input;

        vm.ApplyProbeNumberCommand.Execute(null);

        Assert.Same(before, vm.SelectedDisplayProtocol);
        Assert.Equal(I18nService.Instance["DisplayProbeBadNumber"], vm.ErrorMessage);
    }
```

Расширить **существующий** `BuildWith` (`SettingsViewModelTest.cs:125`) — не заводить
вторую перегрузку, иначе вызовы станут неоднозначными:

```csharp
    private static SettingsViewModel BuildWith(
        FakeSettings settings,
        Func<TimeSpan, CancellationToken, Task>? probeDelay = null)
        => new SettingsViewModel(
            new MainViewModel(),
            settings,
            new FakeStorage(),
            new FakeFeatures(),
            new FakePaymentCategories(),
            probeDelay: probeDelay);
```

Дописать `using System.Threading;`.

- [ ] **Step 2: Прогнать и убедиться, что падает**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~Probe"`
Expected: FAIL — `ProbeDisplayCommand` не существует.

- [ ] **Step 3: Реализовать**

В конструкторе `SettingsViewModel` (`SettingsViewModel.cs:330`) добавить параметр
**после** `localNetworkAddress`, чтобы существующие именованные вызовы не поехали:

```csharp
        Func<string>? localNetworkAddress = null,
        // Подменяется в тесте: один прогон перебора иначе стоил бы 42 секунды.
        // Тот же шов и та же причина, что у localNetworkAddress строкой выше.
        Func<TimeSpan, CancellationToken, Task>? probeDelay = null)
```

и в тело:

```csharp
        // Подменяется в тесте, иначе один прогон перебора стоил бы 42 секунды. Тот же
        // приём, что у localNetworkAddress выше.
        _probeDelay = probeDelay ?? ((delay, token) => Task.Delay(delay, token));
```

Поля и состояние:

```csharp
    private readonly Func<TimeSpan, CancellationToken, Task> _probeDelay;
    private CancellationTokenSource? _probeCts;

    /// <summary>Сколько кадров успел отправить последний перебор. Seam как
    /// LastTestPrintService: только для чтения, только для теста. Без него проверить,
    /// что «Стоп» действительно прекращает отправки, а не просто гасит надпись,
    /// нечем.</summary>
    internal int ProbeStepsRun { get; private set; }

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _probeStatus = string.Empty;

    [ObservableProperty]
    private string _probeNumberText = string.Empty;

    /// <summary>Полторы секунды на шаг. Меньше — кассир не успевает прочитать число на
    /// табло; больше — перебор из 28 шагов перестаёт помещаться в терпение.</summary>
    private static readonly TimeSpan ProbeStep = TimeSpan.FromSeconds(1.5);
```

Команды:

```csharp
    /// <summary>Гонит план автоподбора, отправляя на табло номер каждой комбинации.
    ///
    /// Определить успех сама касса не может и не притворяется: запись в порт удаётся и
    /// на неверной скорости — драйвер отдаёт байты, а ответа от ESC/POS-табло не
    /// существует. Судья здесь кассир, глядящий на табло, и потому пробник — номер, а
    /// не код возврата.</summary>
    [RelayCommand]
    private async Task ProbeDisplay()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        ProbeStepsRun = 0;

        // Та же отсечка, что у CheckDisplay: без порта перебор прогнал бы 28 шагов в
        // пустоту и отчитался бы об успешном завершении.
        if (string.IsNullOrWhiteSpace(CustomerDisplayPort))
        {
            ErrorMessage = I18nService.Instance["DisplayCheckNoPort"];
            return;
        }

        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        IsProbing = true;
        try
        {
            var plan = DisplayProbePlan.Build();
            var codePage = SelectedDisplayCodePage ?? EscPosCodePages.Default;
            var framing = SelectedDisplayFraming ?? SerialFramings.Default;

            foreach (var probe in plan)
            {
                if (token.IsCancellationRequested) break;

                ProbeStatus = string.Format(
                    I18nService.Instance["DisplayProbeProgress"], probe.Number, plan.Count);

                var display = new VfdDisplayService(
                    CustomerDisplayPort, probe.BaudRate, codePage,
                    probe.Protocol, framing, CustomerDisplayDtrRts);

                // Результат не ждётся: на мёртвой комбинации он всё равно false, а
                // ждать его значило бы добавить время открытия порта к паузе, которую
                // кассир и так отсчитывает глазами.
                _ = display.ShowProbeAsync(probe.Number);
                ProbeStepsRun++;

                try
                {
                    await _probeDelay(ProbeStep, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            ProbeStatus = I18nService.Instance["DisplayProbeDone"];
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand]
    private void StopProbe() => _probeCts?.Cancel();

    /// <summary>Ставит комбинацию, номер которой кассир прочитал на табло.</summary>
    [RelayCommand]
    private void ApplyProbeNumber()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        var probe = int.TryParse(ProbeNumberText, out var number)
            ? DisplayProbePlan.Find(number)
            : null;

        if (probe == null)
        {
            ErrorMessage = I18nService.Instance["DisplayProbeBadNumber"];
            return;
        }

        SelectedDisplayProtocol = probe.Protocol;
        CustomerDisplayBaudRateText = probe.BaudRate.ToString();
        StatusMessage = string.Format(
            I18nService.Instance["DisplayProbeApplied"], probe.Number);
    }
```

Дописать в начало файла `using System.Threading;` и `using VvCash.Services.Hardware;`, если их ещё нет.

- [ ] **Step 4: Прогнать — должно пройти**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs tests/VvCash.Tests/SettingsViewModelTest.cs
git commit -m "feat(display): sweep protocol and baud rate from the settings screen"
```

---

## Task 14: Экран настроек

Привязки в этом проекте рефлективные (`AvaloniaUseCompiledBindingsByDefault` выключен), поэтому опечатка в пути компилируется и молча ничего не показывает. Проверять руками на запущенном приложении.

**Files:**
- Modify: `src/VvCash/Views/SettingsView.axaml`

- [ ] **Step 1: Заменить блок дисплея покупателя**

Найти `<Grid ColumnDefinitions="*, *, *, Auto" RowDefinitions="Auto, Auto">` внутри блока дисплея и заменить весь `<Grid>…</Grid>` на:

```xml
                            <Grid ColumnDefinitions="*, *, *, Auto" RowDefinitions="Auto, Auto, Auto, Auto, Auto">
                                <TextBlock Grid.Row="0" Grid.Column="0" Text="{Binding [DisplayPort], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
                                <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding [DisplayBaudRate], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
                                <TextBlock Grid.Row="0" Grid.Column="2" Text="{Binding [CodePage], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>

                                <ComboBox Grid.Row="1" Grid.Column="0" ItemsSource="{Binding AvailableDisplayPorts}" SelectedItem="{Binding CustomerDisplayPort, Mode=TwoWay}" Classes="PrinterCombo" Margin="0,0,8,0"/>
                                <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding CustomerDisplayBaudRateText, Mode=TwoWay}" Classes="PrinterInput" Margin="0,0,8,0"/>
                                <ComboBox Grid.Row="1" Grid.Column="2" ItemsSource="{Binding AvailableCodePages}" SelectedItem="{Binding SelectedDisplayCodePage, Mode=TwoWay}"
                                          DisplayMemberBinding="{Binding DisplayName}" Classes="PrinterCombo" Margin="0,0,8,0"/>
                                <Button Grid.Row="1" Grid.Column="3" VerticalAlignment="Center" Command="{Binding CheckDisplayCommand}">
                                    <StackPanel Orientation="Horizontal" Spacing="6">
                                        <material:MaterialIcon Kind="CheckCircle" Width="18" Height="18"/>
                                        <TextBlock Text="{Binding [CheckDisplay], Source={x:Static services:I18nService.Instance}}" VerticalAlignment="Center"/>
                                    </StackPanel>
                                </Button>

                                <TextBlock Grid.Row="2" Grid.Column="0" Text="{Binding [DisplayProtocol], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,12,8,4"/>
                                <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding [DisplayFraming], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,12,8,4"/>

                                <ComboBox Grid.Row="3" Grid.Column="0" ItemsSource="{Binding AvailableDisplayProtocols}" SelectedItem="{Binding SelectedDisplayProtocol, Mode=TwoWay}"
                                          DisplayMemberBinding="{Binding DisplayName}" Classes="PrinterCombo" Margin="0,0,8,0"/>
                                <ComboBox Grid.Row="3" Grid.Column="1" ItemsSource="{Binding AvailableDisplayFramings}" SelectedItem="{Binding SelectedDisplayFraming, Mode=TwoWay}"
                                          DisplayMemberBinding="{Binding DisplayName}" Classes="PrinterCombo" Margin="0,0,8,0"/>
                                <CheckBox Grid.Row="3" Grid.Column="2" IsChecked="{Binding CustomerDisplayDtrRts, Mode=TwoWay}"
                                          Content="{Binding [DisplayDtrRts], Source={x:Static services:I18nService.Instance}}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                                <Button Grid.Row="3" Grid.Column="3" VerticalAlignment="Center" Command="{Binding ProbeDisplayCommand}" IsEnabled="{Binding !IsProbing}">
                                    <StackPanel Orientation="Horizontal" Spacing="6">
                                        <material:MaterialIcon Kind="Magnify" Width="18" Height="18"/>
                                        <TextBlock Text="{Binding [ProbeDisplay], Source={x:Static services:I18nService.Instance}}" VerticalAlignment="Center"/>
                                    </StackPanel>
                                </Button>

                                <!-- Панель перебора. Видна только пока он идёт или пока
                                     есть что применить: в покое это четыре лишних
                                     контрола на и без того плотном экране. -->
                                <StackPanel Grid.Row="4" Grid.Column="0" Grid.ColumnSpan="4" Orientation="Horizontal" Spacing="8" Margin="0,12,0,0"
                                            IsVisible="{Binding ProbeStatus, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
                                    <TextBlock Text="{Binding ProbeStatus}" VerticalAlignment="Center" FontSize="12" Foreground="{StaticResource Slate700Brush}"/>
                                    <Button Command="{Binding StopProbeCommand}" IsVisible="{Binding IsProbing}"
                                            Content="{Binding [StopProbe], Source={x:Static services:I18nService.Instance}}"/>
                                    <TextBlock Text="{Binding [DisplayProbeNumber], Source={x:Static services:I18nService.Instance}}" VerticalAlignment="Center" FontSize="12" Foreground="{StaticResource Slate500Brush}"/>
                                    <TextBox Text="{Binding ProbeNumberText, Mode=TwoWay}" Width="80" Classes="PrinterInput"/>
                                    <Button Command="{Binding ApplyProbeNumberCommand}"
                                            Content="{Binding [ApplyProbeNumber], Source={x:Static services:I18nService.Instance}}"/>
                                </StackPanel>
                            </Grid>
```

- [ ] **Step 2: Собрать и прогнать весь набор**

Run: `& ./run-tests.ps1`
Expected: PASS, все тесты. XAML компилируется в рамках сборки `VvCash`.

- [ ] **Step 3: Проверить руками**

Запустить приложение, открыть Настройки. Убедиться: обе выпадашки заполнены, галочка DTR/RTS переключается, «Подобрать» без выбранного порта даёт «Порт дисплея не выбран», с портом — показывает «Подбор: N из 28» и кнопку «Стоп».

Привязки рефлективные: опечатка в пути не ломает сборку, а молча даёт пустой контрол. Пустая выпадашка здесь означает именно опечатку, а не отсутствие данных.

- [ ] **Step 4: Коммит**

```bash
git add src/VvCash/Views/SettingsView.axaml
git commit -m "feat(display): add protocol, framing and autodetect to the settings screen"
```

---

## Task 15: Проверка целиком

- [ ] **Step 1: Весь набор тестов**

Run: `& ./run-tests.ps1`
Expected: PASS, падений нет.

- [ ] **Step 2: Убедиться, что настроенная касса не заметила обновления**

Открыть `%LOCALAPPDATA%\VvCash\settings.json`, убедиться, что трёх новых ключей там нет, запустить приложение, открыть Настройки.

Ожидается: протокол `ESC/POS (Epson)`, формат `8N1`, DTR/RTS снят. Это и есть проверка того, что `Resolve` читает отсутствующий ключ как нынешнее поведение — на кассах в точках после обновления не должно измениться ничего.

- [ ] **Step 3: Коммит, если что-то поправилось**

```bash
git add -A
git commit -m "fix(display): address findings from the full verification pass"
```
