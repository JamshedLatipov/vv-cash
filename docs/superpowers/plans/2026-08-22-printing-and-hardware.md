# Печать и железо (батч B) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Заставить кассу действительно печатать — USB-путь перестаёт врать, байты уходят в кодовой странице принтера, дисплей покупателя оживает, — и дать точке возможность проверить это без разработчика.

**Architecture:** Каталог кодовых страниц — чистая статика по образцу `PhoneFormats`. Интероп со спулером живёт в отдельном файле и бросает исключения, которые существующие `catch` превращают в `PrinterStatus.Error`. Дисплей регистрируется через обёртку, подписанную на `SettingsChanged`, — тот же приём, что у `CompositePrinterService`. Пробная печать и проверка дисплея — команды на `SettingsViewModel`, по образцу уже работающего `RemovePrinterCommand`.

**Tech Stack:** .NET 10 (`net10.0`, не `-windows`), Avalonia 11.2.3, CommunityToolkit.Mvvm, xUnit 2.9.2, `System.Text.Encoding.CodePages`, P/Invoke в `winspool.drv`.

**Спека:** [2026-08-22-printing-and-hardware-design.md](../specs/2026-08-22-printing-and-hardware-design.md)
**Ветка:** `fix/printing-and-hardware` (отведена от `fix/session-and-data-safety`, батч A)

---

## Как запускать тесты

На этой машине нет `pwsh`, и сборка при запущенном приложении падает на файловой блокировке. Поэтому **всегда** через раннер, он собирает в `build/verify-tests`:

```bash
& ./run-tests.ps1
```

Один тест:

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosCodePageTest"
```

Полный прогон изредка роняет случайный тест на гонке в Avalonia Dispatcher. Прежде чем винить свою правку — прочитайте стек: если в нём `Dispatcher`, перезапустите.

---

## Структура файлов

**Создаются:**

| Файл | Ответственность |
|---|---|
| `src/VvCash/Models/EscPosCodePage.cs` | запись каталога + сам каталог `EscPosCodePages` + регистрация провайдера кодировок |
| `src/VvCash/Models/QuantityFormat.cs` | единственная реализация «количество без хвостовых нулей» |
| `src/VvCash/Services/Hardware/WindowsRawPrinter.cs` | P/Invoke в спулер, ничего про ESC/POS |
| `src/VvCash/Services/Hardware/NullCustomerDisplayService.cs` | состояние «VFD не настроен» (переезд из `MockCustomerDisplayService.cs`) |
| `src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs` | пересобирает дисплей по `SettingsChanged` |
| `tests/VvCash.Tests/EscPosCodePageTest.cs` | каталог, кодировки, фолбэк |
| `tests/VvCash.Tests/QuantityFormatTest.cs` | хелпер количества |
| `tests/VvCash.Tests/CompositePrinterServiceTest.cs` | гонка при пересборке списка |
| `tests/VvCash.Tests/CustomerDisplayTest.cs` | `ConfiguredCustomerDisplayService`, `NullCustomerDisplayService` |

**Изменяются:** `EscPosPrinterService.cs`, `PrinterDiscoveryService.cs`, `CompositePrinterService.cs`, `VfdDisplayService.cs`, `ICustomerDisplayService.cs`, `PrinterConfig.cs`, `ReturnReceiptLine.cs`, `CartItem.cs`, `ISettingsService.cs`, `SettingsService.cs`, `SettingsViewModel.cs`, `SettingsView.axaml`, `App.axaml.cs`, `ExchangeViewModel.cs`, пять `Assets/i18n/*.json`, три `EscPos*Test.cs` и 16 фейков `ISettingsService`.

**Удаляются:** `src/VvCash/Services/MockProductService.cs`, `src/VvCash/Services/Hardware/MockPrinterService.cs`, `src/VvCash/Services/Hardware/MockCustomerDisplayService.cs`.

---

## Отклонения от спеки (осознанные, каждое — с причиной)

1. **Провайдер кодировок регистрируется в статическом конструкторе `EscPosCodePage`, а не в `Program.Main`.** В тестовом процессе `Main` не выполняется вообще, а строка «Регистрация провайдера кодировок» стоит в таблице покрытия спеки. На типе записи, а не на каталоге: `GetEncoding` зовёт он, конструктор у него публичный, и явный статический конструктор снимает `beforefieldinit` — то есть CLR выполнит регистрацию до первого экземпляра, включая те, что создают инициализаторы полей каталога. Task 1 правит соответствующий абзац спеки.
2. **Успех кнопок проверки показывается отдельным нейтральным баннером `StatusMessage`, а не `ErrorMessage`.** Спека говорит «через уже существующий баннер», но он красный, с иконкой `AlertCircleOutline`. «Пробный чек отправлен» в красной рамке читается как отказ. Отказы идут в `ErrorMessage` ровно как написано в спеке; успех — в соседний баннер.
3. **Платформенная проверка — `OperatingSystem.IsWindows()`, а не `RuntimeInformation.IsOSPlatform`.** CA1416 распознаёт первую как platform guard гарантированно; вторая работает, но зависит от версии анализатора. Смысл тот же.

---

## Открытое решение (не блокирует, решать до Task 5)

`м²` из `Product.UnitShortName` **не входит ни в CP866, ни в CP1251** — надстрочной двойки нет ни в одной однобайтовой таблице ESC/POS. По политике фолбэка из спеки строка единицы напечатается как `12.72 м?`.

План реализует честный вариант: тест утверждает `м?`, поведение задокументировано. Если это неприемлемо — товар в квадратных метрах в этих магазинах обычный, — то альтернатива в одну строку внутри `WriteLine` (Task 5): заменять `²`→`2` и `³`→`3` перед кодированием. Это новая политика, которой в спеке нет, поэтому план её не вводит молча.

---

## Task 1: Каталог кодовых страниц

**Files:**
- Create: `src/VvCash/Models/EscPosCodePage.cs`
- Create: `tests/VvCash.Tests/EscPosCodePageTest.cs`
- Modify: `docs/superpowers/specs/2026-08-22-printing-and-hardware-design.md` (абзац про `Program.Main`)

- [ ] **Step 1: Пакет не нужен — проверить и не добавлять**

Первая редакция плана велела добавить `PackageReference` на
`System.Text.Encoding.CodePages`, ссылаясь на соседний `System.IO.Ports`. Аналогия
ложная: `System.IO.Ports` действительно вне фреймворка, а `System.Text.Encoding.CodePages`
на `net10.0` уже лежит в shared framework и в ref-паке. Пакет не даёт ни строчки в
`deps.json`, не копируется в вывод, и единственное его следствие — постоянный `NU1510`
на каждой сборке обоих проектов. Этот репозиторий держит лог сборки чистым сознательно
(см. комментарий про advisory в самом csproj).

`src/VvCash/VvCash.csproj` не трогается. Провайдер кодировок регистрируется кодом
(Step 4) — этого достаточно, что и подтверждает тест
`Catalog_MakesSingleByteEncodingsAvailable`.

- [ ] **Step 2: Написать падающий тест**

Создать `tests/VvCash.Tests/EscPosCodePageTest.cs`:

```csharp
using System;
using System.Linq;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Каталог кодовых страниц. Всё чистое — ни принтера, ни настроек.</summary>
public class EscPosCodePageTest
{
    [Theory]
    [InlineData("CP866", 866, 17)]
    [InlineData("CP1251", 1251, 46)]
    [InlineData("PC437", 437, 0)]
    public void Resolve_ReturnsTheCatalogEntry(string id, int codePage, byte selector)
    {
        var entry = EscPosCodePages.Resolve(id);

        Assert.Equal(codePage, entry.CodePage);
        Assert.Equal(selector, entry.EscTSelector);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CP-does-not-exist")]
    public void Resolve_OnEmptyOrUnknown_IsTheDefault(string? id)
    {
        // Правило «пусто или незнакомо — значит CP866» должно быть одно и
        // проверяться без файловой системы, ровно как у PhoneFormats.
        Assert.Same(EscPosCodePages.Default, EscPosCodePages.Resolve(id));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Same(EscPosCodePages.Cp1251, EscPosCodePages.Resolve("cp1251"));
    }

    [Fact]
    public void Catalog_MakesSingleByteEncodingsAvailable()
    {
        // .NET Core не несёт однобайтовых кодировок: без RegisterProvider это
        // NotSupportedException. Program.Main в тестовом процессе не выполняется,
        // поэтому регистрация обязана жить там, куда ходят за кодировкой.
        Assert.Equal(866, EscPosCodePages.Cp866.Encoding.CodePage);
        Assert.Equal(1251, EscPosCodePages.Cp1251.Encoding.CodePage);
    }

    [Fact]
    public void Encoding_RoundTripsRussian()
    {
        var bytes = EscPosCodePages.Cp866.Encoding.GetBytes("Плитка");

        Assert.Equal(6, bytes.Length); // однобайтовая: буква = байт
        Assert.Equal("Плитка", EscPosCodePages.Cp866.Encoding.GetString(bytes));
    }

    [Fact]
    public void Encoding_ReplacesAnUncoveredLetterWithQuestionMark()
    {
        // Таджикской ӯ нет ни в CP866, ни в CP1251, и однобайтовой таблицы под
        // таджикский у ESC/POS нет вообще. Замена названа явно, чтобы её было
        // на что предъявить в пробной печати, а не обнаруживать на товарах.
        var bytes = EscPosCodePages.Cp866.Encoding.GetBytes("ӯ");

        Assert.Equal(new byte[] { (byte)'?' }, bytes);
    }

    [Fact]
    public void All_ContainsEveryDeclaredEntry()
    {
        Assert.Equal(3, EscPosCodePages.All.Count);
        Assert.Contains(EscPosCodePages.Cp866, EscPosCodePages.All);
        Assert.Contains(EscPosCodePages.Cp1251, EscPosCodePages.All);
        Assert.Contains(EscPosCodePages.Pc437, EscPosCodePages.All);
        Assert.All(EscPosCodePages.All, e => Assert.False(string.IsNullOrWhiteSpace(e.DisplayName)));
    }

    [Fact]
    public void EveryEntryResolvesBackFromItsOwnId()
    {
        // Id — ключ хранения: запись, до которой Resolve не доходит, недостижима
        // из настроек, хотя в каталоге лежит. Без этого теста четвёртая запись с
        // опечаткой или дублем в Id прошла бы все остальные проверки.
        Assert.All(EscPosCodePages.All, e => Assert.Same(e, EscPosCodePages.Resolve(e.Id)));
        Assert.Equal(EscPosCodePages.All.Count,
            EscPosCodePages.All.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
```

- [ ] **Step 3: Прогнать — должен упасть**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosCodePageTest"
```

Ожидание: ошибка сборки, `CS0103: имя 'EscPosCodePages' не существует в текущем контексте`. (Не `CS0246`: в тесте тип встречается только в позиции доступа к члену, `EscPosCodePages.Resolve(...)`, а не в позиции объявления — компилятор до разрешения типа не доходит.)

- [ ] **Step 4: Написать каталог**

Создать `src/VvCash/Models/EscPosCodePage.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace VvCash.Models;

/// <summary>Одна кодовая страница термопринтера: чем кодировать байты и каким
/// номером сказать принтеру, как их читать.
///
/// Обе половины нужны и по разным причинам. <see cref="CodePage"/> определяет,
/// какие байты уходят; <see cref="EscTSelector"/> — как принтер их истолкует.
/// Разойдутся — получится другой мусор вместо нынешнего.</summary>
public sealed class EscPosCodePage
{
    private Encoding? _encoding;

    // Регистрация живёт на типе, который зовёт GetEncoding, а не на каталоге.
    // Явный статический конструктор снимает beforefieldinit, поэтому CLR выполнит
    // его до появления первого экземпляра — включая инициализаторы полей самого
    // каталога, которые эти экземпляры и создают. На каталоге она была бы верна
    // лишь пока Encoding ленивое, и не спасала бы запись, построенную мимо
    // каталога: конструктор EscPosCodePage публичный.
    static EscPosCodePage()
    {
        // .NET Core не несёт однобайтовых кодировок: без этой строки первое же
        // Encoding.GetEncoding(866) бросает NotSupportedException.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EscPosCodePage(string id, string displayName, int codePage, byte escTSelector)
    {
        Id = id;
        DisplayName = displayName;
        CodePage = codePage;
        EscTSelector = escTSelector;
    }

    /// <summary>То, что ложится в настройки. Хранится он, а не DisplayName:
    /// правка подписи в интерфейсе не должна ломать настроенную кассу.</summary>
    public string Id { get; }

    /// <summary>Не переводится и живёт в коде — как DisplayName у PhoneFormat.
    /// Номер таблицы опознаётся независимо от письменности.</summary>
    public string DisplayName { get; }

    public int CodePage { get; }

    /// <summary>n в команде ESC t n.</summary>
    public byte EscTSelector { get; }

    /// <summary>Замена непокрытой буквы на «?» названа здесь явно, а не оставлена
    /// на умолчание. Таджикских ӯ ғ қ ҳ ҷ и казахских ә ң ө ұ ү і нет ни в одной
    /// однобайтовой таблице ESC/POS, то есть подстановка будет случаться на живых
    /// названиях товаров. Падать нельзя — чек обязан выйти; прятать нечестно —
    /// поэтому она предъявляется в пробной печати.</summary>
    public Encoding Encoding => _encoding ??= Encoding.GetEncoding(
        CodePage,
        new EncoderReplacementFallback("?"),
        new DecoderReplacementFallback("?"));
}

/// <summary>Каталог. Не редактируется из интерфейса сознательно: кассир не должен
/// иметь возможности задать таблицу, которой у принтера нет. Новая запись — правка
/// этого файла и релиз, ровно как с PhoneFormats.
///
/// Значения EscTSelector — из нумерации таблиц Epson, которой следует большинство
/// клонов. У CP866 селектор 17 поддержан почти повсеместно; у CP1251 в природе
/// встречаются 6, 7 и 46, и угадать нужный из репозитория нельзя — это вторая
/// причина, по которой выбор вынесен в настройку с пробной печатью.</summary>
public static class EscPosCodePages
{
    public static readonly EscPosCodePage Cp866 =
        new("CP866", "CP866 — кириллица (DOS)", 866, 17);

    public static readonly EscPosCodePage Cp1251 =
        new("CP1251", "CP1251 — кириллица (Windows)", 1251, 46);

    public static readonly EscPosCodePage Pc437 =
        new("PC437", "PC437 — латиница (таблица по умолчанию)", 437, 0);

    public static IReadOnlyList<EscPosCodePage> All { get; } =
        Array.AsReadOnly(new[] { Cp866, Cp1251, Pc437 });

    /// <summary>Чем становится принтер, у которого настройку не трогали. CP866 —
    /// её понимает большинство ESC/POS-клонов на этом рынке.</summary>
    public static EscPosCodePage Default => Cp866;

    /// <summary>Единственное место, где Id превращается в запись. Функцией, а не
    /// веткой по месту: правило «пусто или незнакомо — значит CP866» должно быть
    /// одно и проверяться тестом.</summary>
    public static EscPosCodePage Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var page in All)
            {
                if (string.Equals(page.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return page;
                }
            }
        }

        return Default;
    }
}
```

- [ ] **Step 5: Прогнать — должен пройти**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosCodePageTest"
```

Ожидание: `Passed! - Failed: 0`, **13** тестов (6 `[Fact]` + 7 `[InlineData]` в двух `[Theory]`).

- [ ] **Step 6: Поправить спеку под фактическое место регистрации**

В `docs/superpowers/specs/2026-08-22-printing-and-hardware-design.md` заменить абзац, начинающийся со слов «Место — первая строка `Program.Main`», на:

```markdown
Место — статический конструктор `EscPosCodePage`, а не `Program.Main`, как
предполагалось при проектировании. `Main` не выполняется ни в тестовом процессе,
ни в превьюере Avalonia, а строка «регистрация провайдера кодировок» стоит в
таблице покрытия ниже. Регистрация висит на типе записи, а не на каталоге:
`GetEncoding` зовёт именно он, и явный статический конструктор снимает с типа
`beforefieldinit`, поэтому CLR выполняет его до появления первого экземпляра —
включая те экземпляры, которые создают инициализаторы полей каталога. `Program.cs`
трогать не нужно вовсе.
```

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Models/EscPosCodePage.cs tests/VvCash.Tests/EscPosCodePageTest.cs src/VvCash/VvCash.csproj docs/superpowers/specs/2026-08-22-printing-and-hardware-design.md
git commit -m "feat(printing): add the ESC/POS code page catalog"
```

---

## Task 2: USB перестаёт врать

**Files:**
- Create: `src/VvCash/Services/Hardware/WindowsRawPrinter.cs`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs` (`SendViaUsb`, строки 208-214)

Юнит-тестов нет и не будет: спека называет интероп с winspool принципиально непокрываемым. Проверяется сборкой и ручным шагом приёмки на точке.

- [ ] **Step 1: Написать интероп**

Создать `src/VvCash/Services/Hardware/WindowsRawPrinter.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VvCash.Services.Hardware;

/// <summary>Сырой поток байт в очередь спулера Windows.
///
/// Отдельно от EscPosPrinterService намеренно: тот про ESC/POS, а не про
/// маршалинг. Имя очереди — то же, что перечисляет PrinterDiscoveryService,
/// то есть ровно то, что кассир выбрал в настройках.
///
/// Каждый вызов winspool возвращает bool. Игнорировать их — воспроизвести на
/// новом уровне ту самую ложь, ради которой файл написан.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    // Возвращает DWORD — идентификатор задания, не BOOL. Ненулевой при успехе,
    // ноль при отказе, поэтому маршалинг в bool корректен; так же объявлено в
    // образце RawPrinterHelper у Microsoft.
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr handle, int level, ref DocInfo1 info);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);

    /// <summary>Бросает при любом отказе спулера. Вызывающий (EscPosPrinterService)
    /// ловит и выставляет PrinterStatus.Error.</summary>
    public static void Send(string printerName, byte[] data)
    {
        // Пустое задание встало бы в очередь и отчиталось успехом: WritePrinter
        // пишет ноль байт без ошибки, и проверка короткой записи даёт 0 != 0.
        // Сегодня недостижимо — любой построитель шлёт минимум ESC @, — но
        // опираться на это молча не стоит.
        if (data.Length == 0) throw new ArgumentException("Nothing to print.", nameof(data));

        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw Failure($"OpenPrinter('{printerName}')");
        }

        var buffer = IntPtr.Zero;
        var docStarted = false;
        var pageStarted = false;
        try
        {
            // RAW — без него спулер отдал бы байты драйверу как документ на
            // отрисовку, и ESC/POS до принтера не доехал бы.
            var info = new DocInfo1 { DocName = "VvCash receipt", OutputFile = null, DataType = "RAW" };
            if (!StartDocPrinter(handle, 1, ref info)) throw Failure("StartDocPrinter");
            docStarted = true;

            if (!StartPagePrinter(handle)) throw Failure("StartPagePrinter");
            pageStarted = true;

            buffer = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);

            if (!WritePrinter(handle, buffer, data.Length, out var written))
            {
                throw Failure("WritePrinter");
            }

            // Короткая запись не считается отказом на уровне API, но чек при ней
            // выходит обрезанным — а обрезанный чек это тот же молчаливый успех.
            // Обрезанное задание при этом всё равно зафиксируется в очереди: из
            // принтера выйдет половина чека, а кассир прочитает отказ. Это
            // сознательный размен — потерять половину чека лучше, чем считать
            // напечатанным то, что напечаталось не полностью.
            if (written != data.Length)
            {
                throw new InvalidOperationException(
                    $"WritePrinter accepted {written} of {data.Length} bytes.");
            }

            // Флаг сбрасывается ПЕРЕД своим вызовом, а не после: иначе отказавший
            // End* бросал бы с ещё взведённым флагом, и finally повторял бы ровно
            // тот вызов, который только что провалился.
            pageStarted = false;
            if (!EndPagePrinter(handle)) throw Failure("EndPagePrinter");

            docStarted = false;
            if (!EndDocPrinter(handle)) throw Failure("EndDocPrinter");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            // Взведённым флаг доходит сюда только если до его вызова дело не
            // дошло вовсе. Повтор уже отказавшего вызова — не уборка, а шум.
            // Отказы здесь игнорируются сознательно: бросок из finally затёр бы
            // исходное исключение, то есть настоящую причину. На успешном пути
            // оба End* уже вызваны выше и проверены — иначе незафиксированное
            // задание молча возвращало бы успех, ровно тот баг, ради которого
            // написан этот файл.
            if (pageStarted) EndPagePrinter(handle);
            if (docStarted) EndDocPrinter(handle);
            // ClosePrinter игнорируется всегда: его отказ уже ничего не отменяет.
            ClosePrinter(handle);
        }
    }

    private static InvalidOperationException Failure(string call)
    {
        // Снимается до создания Win32Exception: её конструктор сам может
        // затереть последнюю ошибку потока. Текст, а не голый номер, потому что
        // кнопка пробной печати (Task 12) кладёт эту строку на экран кассиру.
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException(
            $"{call} failed: {new Win32Exception(code).Message} (Win32 {code}).");
    }
}
```

- [ ] **Step 2: Заменить заглушку**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs` заменить метод `SendViaUsb` целиком:

```csharp
    private Task SendViaUsb(byte[] data)
    {
        // Проект таргетит net10.0, не net10.0-windows, поэтому платформенная
        // проверка обязательна. OperatingSystem.IsWindows(), а не
        // RuntimeInformation: CA1416 распознаёт её как platform guard
        // гарантированно.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USB printing goes through the Windows spooler and is unavailable on this OS.");
        }

        return SendViaSpoolerAsync(_connectionString, data);
    }

    /// <summary>Отдельным методом с атрибутом, а не лямбдой на месте: guard из
    /// SendViaUsb не протекает в тело лямбды, и CA1416 сработал бы на ней.
    ///
    /// Task.Run, а не синхронный вызов: OpenPrinter и StartDocPrinter — это RPC
    /// в spoolsv.exe, и на зависшем спулере они блокируются на секунды. Без него
    /// весь цикл проходил бы на UI-потоке — SendViaUsb никогда не уступает поток,
    /// а CompositePrinterService строит список задач энергичным Select, то есть
    /// до Task.WhenAll дело дошло бы уже после печати. Касса замерзала бы ровно в
    /// момент закрытия продажи. Соседние COM и LAN поток уступают честно.</summary>
    [SupportedOSPlatform("windows")]
    private static Task SendViaSpoolerAsync(string queueName, byte[] data)
        => Task.Run(() => WindowsRawPrinter.Send(queueName, data));
```

`using System;` в файле уже есть (строка 1). Добавить нужно `using System.Runtime.Versioning;`.

**Не заменять ручной `AllocCoTaskMem`/`Copy`/`FreeCoTaskMem` на маршалинг `byte[]`.** Вариант рассматривался и отклонён: семантика та же, форма чище, но два независимых ревью уже проверили нынешнюю на отсутствие утечек и парность аллокаторов, а это самый рискованный непокрываемый тестами код батча. Менять работающий интероп ради изящества — ровно то место, где новый баг заводится без теста, который его поймает.

- [ ] **Step 3: Сборка должна быть зелёной и без CA1416**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Ожидание: `Build succeeded`, `0 Error(s)` и **ровно два предупреждения, оба унаследованных**: `CS8601` в `PosViewModel.cs:2266` и `CS0067` в `MockPrinterService.cs:11`. Оба есть и на чистом HEAD до этой задачи — сверить можно через `git stash` и пересборку. Второе исчезнет само в Task 9 вместе с удаляемым файлом; первое вне скоупа батча. Появление третьего означает, что его добавила эта правка. Появление `CA1416` означает, что guard не распознан — тогда пометить `SendViaUsb` атрибутом `[SupportedOSPlatform("windows")]` нельзя (метод вызывается кроссплатформенно); вместо этого вынести тело в отдельный `[SupportedOSPlatform("windows")] private static void SendViaSpooler(string, byte[])` и звать его под тем же guard'ом.

- [ ] **Step 4: Прогнать весь набор — ничего не должно сломаться**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/WindowsRawPrinter.cs src/VvCash/Services/Hardware/EscPosPrinterService.cs
git commit -m "fix(printing): send USB receipts through the spooler instead of the console"
```

---

## Task 3: Имя принтера доезжает неискажённым

**Files:**
- Modify: `src/VvCash/Services/Hardware/PrinterDiscoveryService.cs:32-39`

- [ ] **Step 1: Выставить кодировку в обе стороны**

В `src/VvCash/Services/Hardware/PrinterDiscoveryService.cs` заменить блок `processInfo` внутри ветки Windows:

```csharp
                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // powershell.exe пишет вывод в кодировке консоли (здесь
                    // cp866), а .NET по умолчанию читает его в кодировке хоста
                    // (в GUI-процессе cp1251) — оба конца надо задать явно, и
                    // одного StandardOutputEncoding мало: он лишь меняет то, чем
                    // декодируют по-прежнему не-UTF-8 байты. [Console]::OutputEncoding
                    // внутри команды переключает сам PowerShell. Пока USB был
                    // заглушкой, покорёженное кириллическое имя никого не
                    // задевало — теперь на его точности держится OpenPrinter.
                    Arguments = "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
                              + "Get-WmiObject -Query 'SELECT Name FROM Win32_Printer' | Select-Object -ExpandProperty Name\"",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
```

- [ ] **Step 2: Добавить using**

В том же файле, к списку `using` вверху (после `using System.Runtime.InteropServices;`, строка 6):

```csharp
using System.Text;
```

- [ ] **Step 3: Сборка**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Ожидание: `Build succeeded`, `0 Error(s)`.

- [ ] **Step 4: Коммит**

```bash
git add src/VvCash/Services/Hardware/PrinterDiscoveryService.cs
git commit -m "fix(printing): read spooler printer names as UTF-8"
```

---

## Task 4: Все пять catch сохраняют причину, и статус возвращается в Ready

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs` (четыре `catch` на строках 140, 262, 275, 338; и пять успешных веток)

Из пяти `catch` текст исключения сегодня сохраняет только `PrintReceiptAsync`.

**Честно о том, чего эта задача НЕ даёт.** `OutputType` проекта — `WinExe`, а логирования в проекте нет никакого: 15 файлов пишут в `Console.WriteLine`, и всё. То есть в бою у приложения нет консоли, и ни одна из этих строк никуда не приезжает. Кассиру причина попадает не отсюда, а из Task 12: кнопка пробной печати зовёт `PrintTestReceiptAsync` напрямую, минуя эти `catch`, и кладёт `ex.Message` в баннер на экране.

Задача всё равно нужна, но по более скромной причине: четыре голых `catch` теряют исключение целиком, поэтому оно недоступно даже при запуске из консоли или под отладчиком — а это единственный способ разобраться в отказе спулера на месте. Приведение к одному виду стоит две строки на метод.

**Вне скоупа, записано в батч D:** отсутствие долговременного лога — настоящая дыра этого приложения, и она шире батча. См. раздел «Вне скоупа» спеки.

- [ ] **Step 1: `PrintPreReceiptAsync`**

Заменить (около строки 140):

```csharp
        catch
        {
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    private static void Write(MemoryStream ms, byte[] data) => ms.Write(data, 0, data.Length);
```

на:

```csharp
        catch (Exception ex)
        {
            Console.WriteLine($"Pre-receipt print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    private static void Write(MemoryStream ms, byte[] data) => ms.Write(data, 0, data.Length);
```

- [ ] **Step 2: `PrintReturnReceiptAsync`**

Заменить (около строки 262):

```csharp
            await SendAsync(BuildReturnReceipt(lines, totalRefund, documentNumber, warehouseName, sellerName, saleDate));
            return true;
        }
        catch
        {
```

на:

```csharp
            await SendAsync(BuildReturnReceipt(lines, totalRefund, documentNumber, warehouseName, sellerName, saleDate));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Return receipt print error: {ex.Message}");
```

- [ ] **Step 3: `OpenCashDrawerAsync`**

Заменить (около строки 275):

```csharp
            await SendAsync(CmdDrawerKick);
            return true;
        }
        catch
        {
```

на:

```csharp
            await SendAsync(CmdDrawerKick);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cash drawer error: {ex.Message}");
```

- [ ] **Step 4: `PrintExchangeReceiptAsync`**

Заменить (около строки 338):

```csharp
            await SendAsync(BuildExchangeReceipt(returned, issued, difference, documentNumber, warehouseName, sellerName, saleDate));
            return true;
        }
        catch
        {
```

на:

```csharp
            await SendAsync(BuildExchangeReceipt(returned, issued, difference, documentNumber, warehouseName, sellerName, saleDate));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exchange receipt print error: {ex.Message}");
```

- [ ] **Step 5: Статус возвращается в `Ready` после удачной печати**

`SetStatus` во всём файле вызывается только с `Error` — обратного перехода нет ни одного. До Task 2 это было недостижимо для USB-касс: путь был заглушкой и не бросал никогда. Теперь первый же сбой печати навсегда красит индикатор готовности принтера (`IsPrinterReady`, зелёная точка в [PosView.axaml:634](src/VvCash/Views/PosView.axaml:634)) до перезапуска кассы или смены настроек — даже когда печать давно восстановилась.

Для батча, чья тема — «интерфейс не должен врать про принтер», это то же враньё, только в другую сторону. Task 2 сделал ветку достижимой, значит чинит её этот батч.

В каждом из пяти методов (`PrintReceiptAsync`, `PrintPreReceiptAsync`, `PrintReturnReceiptAsync`, `PrintExchangeReceiptAsync`, `OpenCashDrawerAsync`) успешная ветка получает `SetStatus` перед `return true`. Например:

```csharp
            await SendAsync(BuildSaleReceipt(items, subtotal, discount, total, discountName,
                documentNumber, warehouseName, sellerName, saleDate));
            // Обратный переход: без него первый же отказ красит индикатор
            // навсегда, потому что SetStatus нигде не вызывался с Ready.
            SetStatus(PrinterStatus.Ready);
            return true;
```

`SetStatus` уже фильтрует повторы на уровне `CompositePrinterService.SetStatus`, а здесь событие поднимается на каждый вызов — это существующее поведение, менять его не нужно.

- [ ] **Step 6: Проверить, что голых catch не осталось**

```bash
grep -n "catch$" src/VvCash/Services/Hardware/EscPosPrinterService.cs
```

Ожидание: пусто (grep вернёт код 1 и ничего не напечатает).

- [ ] **Step 7: Сборка и тесты**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`.

- [ ] **Step 8: Коммит**

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs
git commit -m "fix(printing): keep the reason a receipt failed, and clear the error once it prints"
```

---

## Task 5: Кодовая страница доезжает до байтов

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs` (конструктор, `WriteLine`, четыре места с `CmdInit`, три билдера)
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs:45`
- Modify: `tests/VvCash.Tests/EscPosUnitTest.cs`, `EscPosReturnTest.cs`, `EscPosExchangeTest.cs`

- [ ] **Step 1: Написать падающие тесты на команду и кодировку**

Дописать в конец класса в `tests/VvCash.Tests/EscPosUnitTest.cs`:

```csharp
    // -------------------------------------------------------------------------------
    // Кодовая страница. Сегодня чек уходит в UTF-8 без ESC t n вообще, то есть
    // кириллица печатается мусором. Тесты ниже проверяют обе половины: какими
    // байтами кодируем и каким номером объявляем таблицу принтеру.
    // -------------------------------------------------------------------------------

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }

    [Fact]
    public void SaleReceipt_SelectsTheCodePageRightAfterInit()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866, new[] { line }, subtotal: 10m, discount: 0m, total: 10m);

        // ESC @ первым, ESC t n сразу за ним: таблица должна быть выбрана до
        // первой буквы, иначе шапка уходит в дефолтную.
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 17 }, bytes[..5]);
    }

    [Fact]
    public void ReturnReceipt_SelectsTheCodePage()
    {
        var bytes = EscPosPrinterService.BuildReturnReceipt(
            EscPosCodePages.Cp866, new[] { new ReturnReceiptLine("Товар", 1, 10m) },
            totalRefund: 10m, documentNumber: "RT-1");

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 17 }, bytes[..5]);
    }

    [Fact]
    public void ExchangeReceipt_SelectsTheCodePage()
    {
        var bytes = EscPosPrinterService.BuildExchangeReceipt(
            EscPosCodePages.Cp866,
            new[] { new ReturnReceiptLine("Товар", 1, 10m) },
            new[] { new ReturnReceiptLine("Другой", 1, 12m) },
            difference: 2m, documentNumber: "EX-1");

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 17 }, bytes[..5]);
    }

    [Fact]
    public void TestReceipt_SelectsTheCodePage_AndNamesIt()
    {
        // Пробный чек печатает выбранную таблицу и её селектор, чтобы точка могла
        // сказать, что именно пробовала, не глядя в настройки.
        var bytes = EscPosPrinterService.BuildTestReceipt(EscPosCodePages.Cp1251);

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 46 }, bytes[..5]);

        var text = EscPosCodePages.Cp1251.Encoding.GetString(bytes);
        Assert.Contains("CP1251", text);
        Assert.Contains("ESC t 46", text);
    }

    [Fact]
    public void TestReceipt_CarriesRussianTajikKazakhLatinAndDigits()
    {
        // Без второй строки русский образец печатается безупречно, кассир
        // отвечает «кириллица видна», и граница обнаруживается позже — на
        // названиях товаров в бою.
        var text = EscPosCodePages.Cp866.Encoding.GetString(
            EscPosPrinterService.BuildTestReceipt(EscPosCodePages.Cp866));

        Assert.Contains("Ёжик", text);
        Assert.Contains("The quick brown fox", text);
        Assert.Contains("0123456789", text);
        // Ни одной из десяти таджикских и казахских букв в CP866 нет —
        // строка целиком вырождается в вопросительные знаки, и предъявляется
        // это ровно там, где на неё смотрят. Утверждение точное, а не
        // Contains("?"): последнее прошло бы и на одной случайной замене,
        // то есть не отличило бы эту границу от опечатки в другом месте.
        Assert.Contains("TJ/KK: ? ? ? ? ? ? ? ? ? ?", text);
    }

    [Fact]
    public void Receipt_IsEncodedInTheChosenCodePage_NotUtf8()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866, new[] { line }, subtotal: 10m, discount: 0m, total: 10m);

        Assert.True(Contains(bytes, EscPosCodePages.Cp866.Encoding.GetBytes("Товар")));
        Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("Товар")));
    }
```

Новых `using` не нужно: `using System.Text;` (строка 2) и `using VvCash.Models;` (строка 3) в этом файле уже есть, а `EscPosCodePages` живёт в `VvCash.Models`.

- [ ] **Step 2: Прогнать — должно упасть на сборке**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~EscPosUnitTest"
```

Ожидание: `CS1501: No overload for method 'BuildSaleReceipt' takes 5 arguments` и `CS0117: 'EscPosPrinterService' does not contain a definition for 'BuildTestReceipt'`.

- [ ] **Step 3: Провести кодовую страницу через `EscPosPrinterService`**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs`:

(a) добавить `using VvCash.Models;` — уже есть на строке 9, ничего не делать.

(b) поле и конструктор:

```csharp
    private readonly PrinterConnectionType _connectionType;
    private readonly string _connectionString;
    private readonly EscPosCodePage _codePage;
    private PrinterStatus _status = PrinterStatus.Ready;
```

```csharp
    public EscPosPrinterService(PrinterConnectionType connectionType, string connectionString,
        EscPosCodePage codePage)
    {
        _connectionType = connectionType;
        _connectionString = connectionString;
        _codePage = codePage;
    }
```

(c) заменить `WriteLine` и добавить `WriteInit` (на месте нынешних строк 148-152):

```csharp
    /// <summary>ESC @ и следом ESC t n. Одним методом, а не двумя командами по
    /// месту: CmdInit пишется в четырёх местах — три билдера и PrintPreReceiptAsync,
    /// который собирает буфер мимо них, — и дописывать выбор таблицы руками рядом с
    /// каждым значит однажды пропустить четвёртый. Пречек за смену печатается чаще
    /// всех прочих чеков вместе взятых, и его молчаливый откат на дефолтную таблицу
    /// выглядел бы как «иногда печатает мусор».</summary>
    private static void WriteInit(MemoryStream ms, EscPosCodePage codePage)
    {
        Write(ms, CmdInit);
        ms.WriteByte(0x1B);
        ms.WriteByte(0x74);
        ms.WriteByte(codePage.EscTSelector);
    }

    private static void WriteLine(MemoryStream ms, string text, EscPosCodePage codePage)
    {
        var bytes = codePage.Encoding.GetBytes(text + "\n");
        ms.Write(bytes, 0, bytes.Length);
    }
```

(d) в каждом из трёх билдеров: первым параметром `EscPosCodePage codePage`, `Write(ms, CmdInit)` → `WriteInit(ms, codePage)`, каждый `WriteLine(ms, X)` → `WriteLine(ms, X, codePage)`.

Сигнатуры после правки:

```csharp
    public static byte[] BuildSaleReceipt(
        EscPosCodePage codePage,
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null,
        string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null)
```

```csharp
    public static byte[] BuildReturnReceipt(
        EscPosCodePage codePage,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> lines,
        decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
```

```csharp
    public static byte[] BuildExchangeReceipt(
        EscPosCodePage codePage,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        System.Collections.Generic.IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
```

Параметр идёт **первым**, потому что у всех остальных есть значения по умолчанию.

(e) `PrintPreReceiptAsync` — четвёртое место: `Write(ms, CmdInit)` → `WriteInit(ms, _codePage)`, и все `WriteLine(ms, X)` → `WriteLine(ms, X, _codePage)`.

(f) в четырёх `Print*Async` вызовы билдеров получают `_codePage` первым аргументом. Например:

```csharp
            await SendAsync(BuildSaleReceipt(_codePage, items, subtotal, discount, total, discountName,
                documentNumber, warehouseName, sellerName, saleDate));
```

- [ ] **Step 4: Добавить пробный чек**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs`, сразу после `BuildSaleReceipt`:

```csharp
    /// <summary>Образец, по которому на точке решают, угадана ли таблица.
    ///
    /// Не «Hello world»: проверять надо ровно то, что ломалось. Русская строка —
    /// собственно проверка; строка таджикских и казахских букв напечатается
    /// вопросительными знаками при ЛЮБОЙ записи каталога, и это ожидаемо —
    /// однобайтовой таблицы под них у ESC/POS нет. Она стоит здесь, чтобы это
    /// увидели на бумаге, а не на названиях товаров через неделю. Латиница и
    /// цифры отделяют «таблица не та» от «принтер вообще не тот».</summary>
    public static byte[] BuildTestReceipt(EscPosCodePage codePage)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdBoldOn);
        WriteLine(ms, "TEST / ПРОБНАЯ ПЕЧАТЬ", codePage);
        Write(ms, CmdBoldOff);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdAlignLeft);
        WriteLine(ms, "RU: Ёжик съел 12 шт.", codePage);
        WriteLine(ms, "TJ/KK: ӯ ғ қ ҳ ҷ ә ң ө ұ ү", codePage);
        WriteLine(ms, "LAT: The quick brown fox", codePage);
        WriteLine(ms, "NUM: 0123456789", codePage);
        WriteLine(ms, "----------------------------", codePage);
        // Что именно пробовали — чтобы точка могла назвать это по телефону, не
        // залезая в настройки.
        WriteLine(ms, $"{codePage.Id}   ESC t {codePage.EscTSelector}", codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    /// <summary>Отправляет <see cref="BuildTestReceipt"/> и не глотает отказ:
    /// кнопке проверки нужен не bool, а причина.</summary>
    public Task PrintTestReceiptAsync() => SendAsync(BuildTestReceipt(_codePage));
```

- [ ] **Step 5: Починить единственного вызывающего конструктор**

В `src/VvCash/Services/Hardware/CompositePrinterService.cs`, строка 45:

```csharp
                var printer = new EscPosPrinterService(config.ConnectionType, config.ConnectionString,
                    EscPosCodePages.Default);
```

Пока `Default` — поле настройки появится в Task 6.

- [ ] **Step 6: Переписать три существующих теста на выбранную кодировку**

Сегодня они проходят **именно потому**, что мы шлём UTF-8, то есть подтверждают баг.

В `tests/VvCash.Tests/EscPosUnitTest.cs` заменить хелпер `Render` (строки 24-26):

```csharp
    private static string Render(IEnumerable<CartItem> items) =>
        EscPosCodePages.Cp866.Encoding.GetString(
            EscPosPrinterService.BuildSaleReceipt(
                EscPosCodePages.Cp866, items, subtotal: 5300m, discount: 0m, total: 5300m));
```

и вызов на строке 76:

```csharp
        var text = EscPosCodePages.Cp866.Encoding.GetString(EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Cp866,
            new[] { line }, subtotal: 10m, discount: 0m, total: 10m,
            discountName: null,
            documentNumber: "SL-42", warehouseName: "Склад 1", sellerName: "Анна", saleDate: "10.08.2026 14:05"));
```

**Тест на строке 35 придётся изменить по существу, а не механически.** Сейчас:

```csharp
        Assert.Contains("12.72 м²", text);
```

`²` (U+00B2) не входит ни в CP866, ни в CP1251 — надстрочной двойки нет ни в одной однобайтовой таблице ESC/POS. По политике фолбэка она станет `?`:

```csharp
        // Надстрочной двойки нет ни в одной однобайтовой таблице ESC/POS, поэтому
        // единица печатается как "м?". Это граница подхода, а не промах с выбором
        // таблицы, — см. «Честная граница» в спеке. Сама цифра, ради которой строка
        // существует, доезжает целой.
        Assert.Contains("12.72 м?", text);
```

В `tests/VvCash.Tests/EscPosReturnTest.cs` и `EscPosExchangeTest.cs` — то же движение: каждый `Encoding.UTF8.GetString(...)` становится `EscPosCodePages.Cp866.Encoding.GetString(...)`, и каждый вызов билдера получает `EscPosCodePages.Cp866` первым аргументом. Строки: `EscPosReturnTest.cs:22,35,47`, `EscPosExchangeTest.cs:18,34,49,66`. `using VvCash.Models;` в обоих файлах уже есть; после правки `using System.Text;` может остаться неиспользованным — это предупреждение, не ошибка, удалить его если IDE подсветит.

- [ ] **Step 7: Прогнать — всё должно пройти**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`. Если падает `Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("Товар")))` — значит какой-то `WriteLine` остался без `codePage`; найти его: `grep -n "WriteLine(ms, [^,]*)$" src/VvCash/Services/Hardware/EscPosPrinterService.cs`.

- [ ] **Step 8: Коммит**

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs src/VvCash/Services/Hardware/CompositePrinterService.cs tests/VvCash.Tests/EscPosUnitTest.cs tests/VvCash.Tests/EscPosReturnTest.cs tests/VvCash.Tests/EscPosExchangeTest.cs
git commit -m "fix(printing): encode receipts in the printer's code page"
```

---

## Task 6: Кодовая страница становится настройкой принтера

**Files:**
- Modify: `src/VvCash/Models/PrinterConfig.cs`
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs` (`PrinterConfigViewModel`, загрузка, `AddPrinter`, `Save`)
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs:45`
- Modify: `tests/VvCash.Tests/SettingsViewModelTest.cs`

`PrinterConfig` сериализуется в `settings.json` как есть (`SettingsData.Printers`), поэтому отдельной миграции не нужно: старый файл без поля даст пустую строку, а `Resolve` читает пустое как CP866.

- [ ] **Step 1: Написать падающий тест**

Дописать в `tests/VvCash.Tests/SettingsViewModelTest.cs` внутрь класса:

```csharp
    /// <summary>Для тестов, которым нужна касса с уже настроенным состоянием:
    /// Build(out …) создаёт FakeSettings сам, и заполнить их до конструктора
    /// вью-модели нечем, а часть настроек читается именно там.</summary>
    private static SettingsViewModel BuildWith(FakeSettings settings)
        => new SettingsViewModel(
            new MainViewModel(),
            settings,
            new FakeStorage(),
            new FakeFeatures(),
            new FakePaymentCategories());

    [Fact]
    public void Save_WritesTheCodePagePerPrinter()
    {
        // На принтер, а не на кассу: в магазине могут стоять две разные железки.
        var vm = Build(out var settings);
        vm.AddPrinterCommand.Execute(null);
        vm.Printers[0].SelectedCodePage = EscPosCodePages.Cp1251;
        // Save отказывается на пустом BackendUrl и выходит первой же строкой —
        // без этого тест не доходит до проверяемого кода. Так же поступают все
        // соседние тесты, вызывающие SaveCommand.
        vm.BackendUrl = "https://api.example.test/v1/";

        vm.SaveCommand.Execute(null);

        Assert.Equal("CP1251", settings.Printers[0].CodePageId);
    }

    [Theory]
    [InlineData("CP1251", false)]
    [InlineData("CP-gone", true)]
    public void Load_ResolvesTheStoredCodePage(string stored, bool expectDefault)
    {
        // Известный id обязателен: на одном лишь неизвестном тест зелёный и без
        // загрузки — инициализатор поля даёт ровно ту же ссылку, что и Resolve.
        var settings = new FakeSettings
        {
            Printers = new List<PrinterConfig>
            {
                new() { Name = "P", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", CodePageId = stored }
            }
        };

        var vm = BuildWith(settings);

        Assert.Same(
            expectDefault ? EscPosCodePages.Default : EscPosCodePages.Cp1251,
            vm.Printers[0].SelectedCodePage);
    }
```

`Build(out var settings)` — существующий хелпер этого файла (строка 93); он строит `FakeSettings` сам. `BuildWith` добавляется здесь же, рядом с ним. Убедиться, что в шапке есть `using System.Collections.Generic;` и `using VvCash.Models;`.

- [ ] **Step 2: Прогнать — должно упасть**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"
```

Ожидание: `CS0117: 'PrinterConfig' does not contain a definition for 'CodePageId'`.

**Красный на ошибке компиляции ничего не доказывает про сам тест.** До Step 3 проверяемых членов не существует вовсе, поэтому вакуумный тест выглядит точно так же, как настоящий. Зубы теста проверяются только мутацией после того, как всё зазеленело: убрать строку, ради которой тест написан, и убедиться, что он падает. Это относится ко всем оставшимся задачам плана, а не только к этой.

- [ ] **Step 3: Поле в модели**

`src/VvCash/Models/PrinterConfig.cs` целиком:

```csharp
namespace VvCash.Models;

public class PrinterConfig
{
    public string Name { get; set; } = string.Empty;
    public PrinterConnectionType ConnectionType { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>Id записи из EscPosCodePages. На принтер, а не на кассу: в магазине
    /// могут стоять две разные железки. Пусто на конфигурации, где настройку не
    /// трогали; Resolve читает пустое и незнакомое как CP866, поэтому обновление
    /// существующей кассы ничего не меняет.</summary>
    public string CodePageId { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Свойство во вью-модели строки принтера**

В `src/VvCash/ViewModels/SettingsViewModel.cs`, в `PrinterConfigViewModel`, после `_connectionType` (строка 33):

```csharp
    /// <summary>Каталог целиком: он неизменен и не зависит от сети — как
    /// AvailablePhoneFormats рядом.</summary>
    public IReadOnlyList<EscPosCodePage> AvailableCodePages { get; } = EscPosCodePages.All;

    /// <summary>Nullable по той же причине, что SelectedPhoneFormat: SelectingItemsControl
    /// приводит SelectedItem к null и пишет его обратно через TwoWay, если присвоенного
    /// значения не нашлось в ItemsSource.</summary>
    [ObservableProperty]
    private EscPosCodePage? _selectedCodePage = EscPosCodePages.Default;
```

- [ ] **Step 5: Загрузка, добавление и сохранение**

В конструкторе `SettingsViewModel`, в цикле по `_settingsService.Printers` (около строки 196), добавить в инициализатор:

```csharp
                SelectedCodePage = EscPosCodePages.Resolve(printer.CodePageId),
```

В `AddPrinter` (около строки 225), в инициализатор:

```csharp
            SelectedCodePage = EscPosCodePages.Default,
```

В `Save` (около строки 377):

```csharp
        _settingsService.Printers = Printers.Select(p => new PrinterConfig
        {
            Name = p.Name,
            ConnectionType = p.ConnectionType,
            ConnectionString = p.ConnectionString,
            IsEnabled = p.IsEnabled,
            // Здесь ?? Default, а не пропуск записи, как у SelectedPhoneFormat и
            // категорий платежа выше. Причина не в том, что каталог всегда полон —
            // PhoneFormats тоже статичен и сети не требует. Причин три другие:
            // Printers пересобирается списком целиком, поэтому «пропустить и
            // сохранить прежнее» потребовало бы сопоставлять каждую строку с её
            // прежним PrinterConfig, а у только что добавленной строки прежнего нет;
            // и цена промаха здесь несравнима — откат на CP866 это ровно то, что
            // Resolve и так отдаёт ненастроенному принтеру, тогда как пустой формат
            // телефона молча применил бы чужой код страны к настоящим номерам.
            CodePageId = (p.SelectedCodePage ?? EscPosCodePages.Default).Id
        }).ToList();
```

- [ ] **Step 6: Композит читает настройку**

В `src/VvCash/Services/Hardware/CompositePrinterService.cs`, строка 45:

```csharp
                var printer = new EscPosPrinterService(config.ConnectionType, config.ConnectionString,
                    EscPosCodePages.Resolve(config.CodePageId));
```

- [ ] **Step 7: Прогнать и убедиться, что настройка доехала до продакшена**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`.

```bash
grep -n "EscPosCodePages.Default" src/VvCash/Services/Hardware/CompositePrinterService.cs
```

Ожидание: **пусто**. Строка со Step 6 — единственная, которая доносит всю фичу до боевой кассы, а оба теста этой задачи проверяют только round-trip вью-модели: забыть Step 6 и остаться зелёным здесь можно. Прочитать `_codePage` обратно нельзя — свойства у него нет, — поэтому проверка идёт грепом.

- [ ] **Step 8: Коммит**

```bash
git add src/VvCash/Models/PrinterConfig.cs src/VvCash/ViewModels/SettingsViewModel.cs src/VvCash/Services/Hardware/CompositePrinterService.cs tests/VvCash.Tests/SettingsViewModelTest.cs
git commit -m "feat(printing): make the code page a per-printer setting"
```

---

## Task 7: Гонка при пересборке списка принтеров

**Files:**
- Modify: `src/VvCash/Services/Hardware/CompositePrinterService.cs`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs` (`SetStatus` — см. Step 0)
- Create: `tests/VvCash.Tests/CompositePrinterServiceTest.cs`

**Что изменилось после Task 4, и почему эта задача перестала быть желательной.**

Task 4 добавил `SetStatus(PrinterStatus.Ready)` в успешную ветку всех пяти методов печати. Три следствия, которые меняют постановку:

1. **Окно гонки стало посещаться на счастливом пути.** Раньше в `UpdateOverallStatus` заходили только при сбое печати, то есть попасть в гонку можно было лишь сочетанием «печать упала одновременно со сменой настроек». Теперь туда заходят при каждой **успешной** печати, и достаточно «печать прошла одновременно со сменой настроек». Частота выросла на порядки.
2. **Диагностируемость упала.** `SetStatus(Ready)` стоит внутри `try`, а `StatusChanged?.Invoke` синхронный. Значит исключение из `UpdateOverallStatus` — то самое, которое чинит эта задача, — больше не всплывает стеком, а ловится `catch` метода печати и превращается в `"Print failed."` на экране кассира **для чека, который физически напечатался**. Кассир печатает повторно, получается дубль. Искать этот баг придётся по симптому «касса иногда врёт про неудачную печать», а не по падению.
3. **`volatile` в коде нет вообще** — ни у `_printers` (`CompositePrinterService.cs:12`), ни у `_overallStatus`, ни у `_status` в `EscPosPrinterService`. Ранняя редакция этого плана говорила иначе; исходить надо из фактического состояния.

**Зависимость по порядку.** Step 3 ниже содержит `EscPosCodePages.Resolve(config.CodePageId)`, который не скомпилируется, пока Task 6 не добавит `PrinterConfig.CodePageId`. Эта задача обязана идти после Task 6.

- [ ] **Step 0: `SetStatus` перестаёт превращать чужое исключение в отказ печати**

Перед всем остальным — в `src/VvCash/Services/Hardware/EscPosPrinterService.cs` подписчик больше не должен уметь опрокинуть печать:

```csharp
    /// <summary>Исключение подписчика проглатывается намеренно. Вызовы стоят внутри
    /// try методов печати, а Invoke синхронный: без этого упавший обработчик
    /// поймался бы catch'ем печати, и чек, который физически вышел из принтера,
    /// отчитался бы как «Print failed» — кассир напечатал бы дубль.
    ///
    /// Обратный переход в Ready живёт в успешных ветках всех пяти методов печати:
    /// без него первый же отказ красил индикатор навсегда, потому что с Ready
    /// SetStatus не вызывался нигде.</summary>
    private void SetStatus(PrinterStatus status)
    {
        _status = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Printer status subscriber failed: {ex.Message}");
        }
    }
```

Пояснительный комментарий из `PrintReceiptAsync` при этом переезжает сюда — в точку схождения всех пяти путей, куда читатель приходит по go-to-definition. На месте он объяснял самый очевидный случай («напечатали чек — принтер жив») и был в ста восьмидесяти строках от самого неочевидного, `OpenCashDrawerAsync`, где `SetStatus(PrinterStatus.Ready)` после удара по денежному ящику выглядит немотивированно.

- [ ] **Step 0b: райдером, раз файл всё равно открыт**

Три мелочи из ревью Task 5, каждая на строку, ни одна не стоит отдельной правки этого файла — он и так правится четвёртой задачей подряд:

1. **`ESC t` перестаёт быть единственной командой без имени.** В `WriteInit` первые два байта пишутся голыми `WriteByte`, тогда как все прочие команды файла — константы `Cmd*`. Рантаймовый там только селектор:

```csharp
    private static readonly byte[] CmdSelectCodeTable = { 0x1B, 0x74 };
```

```csharp
        Write(ms, CmdInit);
        Write(ms, CmdSelectCodeTable);
        ms.WriteByte(codePage.EscTSelector);   // единственное, что действительно рантайм
```

2. **Убрать мёртвый `using System.Text;`** из `EscPosPrinterService.cs`: единственная ссылка на `Encoding` теперь `codePage.Encoding` из `VvCash.Models`.

3. **Дописать в комментарий `EscPosCodePage.Encoding`** (файл `src/VvCash/Models/EscPosCodePage.cs`), что односимвольность замены стала несущей: `PadLine` и `Truncate` считают колонки в символах, и это верно ровно потому, что однобайтовая таблица плюс односимвольный фолбэк дают «символ == байт». Расширение замены до `"??"` или переход на `ExceptionFallback` молча сломают выравнивание чека.

**Пересоздание принтеров — часть контракта, а не деталь реализации.** `EscPosPrinterService` захватывает кодовую страницу в конструкторе и больше её не перечитывает. Сегодня `InitializePrinters` пересоздаёт **все** экземпляры по `SettingsChanged`, и только поэтому смена кодовой страницы в настройках вступает в силу без перезапуска кассы. Если хардинг гонки приведёт к переиспользованию существующих экземпляров — например, чтобы не рвать принтер посреди печати, — переиспользованный принтер останется со старой страницей, и настройка тихо перестанет работать. Тесты этого не поймают: страницу неоткуда прочитать.

Что и подводит к следующему пункту.

- [ ] **Step 0c: `BuildPreReceipt` — четвёртое место `WriteInit` становится проверяемым**

`PrintPreReceiptAsync` собирает буфер инлайном, поэтому единственное место, ради которого `WriteInit` вообще существует, — то самое, про которое его собственный комментарий пишет «однажды пропустить четвёртый», — осталось без теста. Защищены оказались три места, пропустить которые и так трудно.

Извлечь четвёртым билдером в форме трёх соседей:

```csharp
    public static byte[] BuildPreReceipt(EscPosCodePage codePage, IEnumerable<CartItem> items, decimal total)
```

`PrintPreReceiptAsync` зовёт его, и тест — та же пятибайтовая проверка, что у трёх остальных:

```csharp
    [Fact]
    public void PreReceipt_SelectsTheCodePage()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 1m };

        var bytes = EscPosPrinterService.BuildPreReceipt(EscPosCodePages.Cp866, new[] { line }, total: 10m);

        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 17 }, bytes[..5]);
    }
```

Класс от этого становится чище, а не толще: четыре билдера и пять отправителей вместо трёх билдеров, пяти отправителей и одной раскладки, спрятанной внутри отправителя.

- [ ] **Step 0d: сделать применённую кодовую страницу проверяемой**

Task 6 закрылась grep-гейтом (`EscPosCodePages.Default` не должно остаться в композите) именно потому, что шва не было: `_printers` приватный, `_codePage` приватный, прочитать «какая страница реально доехала до принтера» неоткуда. Раз этот метод всё равно переписывается, шов стоит прорезать:

```csharp
    /// <summary>Какая таблица реально применена к этому принтеру. Существует ради
    /// теста: строка, которая доносит настройку до боевой кассы, иначе не
    /// покрывается ничем, и её пропажу ловил бы только grep.</summary>
    public EscPosCodePage CodePage => _codePage;
```

После этого «принтер построен с той страницей, что задана в настройках» становится обычным тестом в `CompositePrinterServiceTest`, а grep-гейт из Task 6 можно снять.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/VvCash.Tests/CompositePrinterServiceTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Пересборка состава принтеров по SettingsChanged, пока печать идёт.
/// Кнопка пробной печати делает связку «поменял настройку → сразу печатаю»
/// обычным сценарием, а не редким.</summary>
public class CompositePrinterServiceTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PrinterConfig Lan(string address) => new()
    {
        Name = address,
        ConnectionType = PrinterConnectionType.LAN,
        ConnectionString = address,
        IsEnabled = true
    };

    [Fact]
    public async Task PrintingSurvivesASettingsChangeMidFlight()
    {
        // Ни один из адресов не отвечает, поэтому печать честно провалится —
        // проверяется не результат, а то, что метод не падает на изменившейся
        // под ним коллекции. До правки это InvalidOperationException из Select
        // по списку, который в этот момент чистят.
        var settings = new FakeSettings { Printers = { Lan("127.0.0.1:9101") } };
        var composite = new CompositePrinterService(settings);

        var printing = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m);
            }
        });

        var reconfiguring = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                settings.Printers = new List<PrinterConfig> { Lan($"127.0.0.1:{9102 + (i % 3)}") };
                settings.Save();
            }
        });

        await Task.WhenAll(printing, reconfiguring);
    }

    [Fact]
    public async Task NoPrintersConfigured_ReportsFailureRatherThanThrowing()
    {
        var composite = new CompositePrinterService(new FakeSettings());

        Assert.False(await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m));
        Assert.False(await composite.OpenCashDrawerAsync());
    }
}
```

Поля `CustomerDisplay*` в фейке — задел под Task 11; на этом шаге они лишние, но безвредные. Если Task 11 ещё не сделан, удалить три строки и вернуть их там.

- [ ] **Step 2: Прогнать — должно падать нестабильно**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CompositePrinterServiceTest"
```

Ожидание: `PrintingSurvivesASettingsChangeMidFlight` падает с `InvalidOperationException: Collection was modified` (иногда с первого раза, иногда с третьего — это гонка). Прогнать трижды, чтобы увидеть.

- [ ] **Step 3: Заменить поле и пересборку**

В `src/VvCash/Services/Hardware/CompositePrinterService.cs` заменить объявление (строка 12):

```csharp
    /// <summary>volatile, а не просто ссылка: присваивание ссылки атомарно (ECMA-335),
    /// но атомарность — не то же самое, что видимость. Атомарности хватает, чтобы не
    /// увидеть полусобранный список; чтобы гарантированно увидеть новый — нет.</summary>
    private volatile IReadOnlyList<EscPosPrinterService> _printers = Array.Empty<EscPosPrinterService>();
```

и `InitializePrinters` целиком:

```csharp
    /// <summary>Собирает новый список и присваивает его одним движением, вместо того
    /// чтобы править существующий на месте. Без блокировки: методы печати await-ят
    /// сетевой и последовательный ввод-вывод, и держать на нём мьютекс — значит
    /// подвесить экран настроек на время печати.
    ///
    /// Печать, начатая до смены настроек, доводится до конца на прежнем составе.
    /// Если она упадёт ПОСЛЕ подмены, её StatusChanged уже некому услышать и общий
    /// Status останется Ready — возвращаемый bool при этом честный, расходится
    /// только индикатор. Принято сознательно: держать подписки на выброшенных
    /// принтерах до конца их последней задачи стоит заметно больше механики, чем
    /// расхождение индикатора на одну печать.</summary>
    private void InitializePrinters()
    {
        foreach (var printer in _printers)
        {
            printer.StatusChanged -= OnPrinterStatusChanged;
        }

        var rebuilt = new List<EscPosPrinterService>();
        var configs = _settingsService.Printers?.Where(p => p.IsEnabled);
        if (configs != null)
        {
            foreach (var config in configs)
            {
                var printer = new EscPosPrinterService(config.ConnectionType, config.ConnectionString,
                    EscPosCodePages.Resolve(config.CodePageId));
                printer.StatusChanged += OnPrinterStatusChanged;
                rebuilt.Add(printer);
            }
        }

        _printers = rebuilt;

        UpdateOverallStatus();
    }
```

- [ ] **Step 4: Локальная копия во всех читателях**

`UpdateOverallStatus` — сегодня читает поле четырежды подряд:

```csharp
    private void UpdateOverallStatus()
    {
        var printers = _printers;

        if (printers.Count == 0)
        {
            SetStatus(PrinterStatus.Ready);
            return;
        }

        if (printers.Any(p => p.Status == PrinterStatus.Error))
        {
            SetStatus(PrinterStatus.Error);
        }
        else if (printers.Any(p => p.Status == PrinterStatus.NoPaper))
        {
            SetStatus(PrinterStatus.NoPaper);
        }
        else if (printers.Any(p => p.Status == PrinterStatus.Offline))
        {
            SetStatus(PrinterStatus.Offline);
        }
        else
        {
            SetStatus(PrinterStatus.Ready);
        }
    }
```

И каждый из пяти методов печати. Копия берётся **до** проверки на пустоту — подмена между `Any()` и `Select()` это ровно та гонка, только в более узкое окно:

```csharp
    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null,
        string? documentNumber = null, string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        var printers = _printers;
        if (printers.Count == 0) return false;

        var tasks = printers.Select(p => p.PrintReceiptAsync(items, subtotal, discount, total, coupons, discountName,
            documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total)
    {
        var printers = _printers;
        if (printers.Count == 0) return false;

        var tasks = printers.Select(p => p.PrintPreReceiptAsync(items, total)).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }

    public async Task<bool> OpenCashDrawerAsync()
    {
        var printers = _printers;
        if (printers.Count == 0) return false;

        var tasks = printers.Select(p => p.OpenCashDrawerAsync()).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintReturnReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> lines, decimal totalRefund, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        var printers = _printers;
        if (printers.Count == 0) return false;

        var list = lines.ToList();
        var tasks = printers.Select(p => p.PrintReturnReceiptAsync(list, totalRefund, documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintExchangeReceiptAsync(
        IEnumerable<VvCash.Models.ReturnReceiptLine> returned,
        IEnumerable<VvCash.Models.ReturnReceiptLine> issued,
        decimal difference, string documentNumber,
        string? warehouseName = null, string? sellerName = null, string? saleDate = null)
    {
        var printers = _printers;
        if (printers.Count == 0) return false;

        var returnedList = returned.ToList();
        var issuedList = issued.ToList();
        var tasks = printers.Select(p => p.PrintExchangeReceiptAsync(returnedList, issuedList, difference, documentNumber, warehouseName, sellerName, saleDate)).ToList();
        await Task.WhenAll(tasks);

        return tasks.Any(t => t.Result);
    }
```

- [ ] **Step 5: Убедиться, что прямых чтений поля не осталось**

```bash
grep -n "_printers" src/VvCash/Services/Hardware/CompositePrinterService.cs
```

Ожидание: ровно четыре вхождения — объявление, отписка и присваивание в `InitializePrinters`, и `var printers = _printers` × 6. Ни одного `_printers.Any()` / `_printers.Select(`.

- [ ] **Step 6: Прогнать трижды**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CompositePrinterServiceTest"
```

Ожидание: `Failed: 0` все три раза.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Services/Hardware/CompositePrinterService.cs tests/VvCash.Tests/CompositePrinterServiceTest.cs
git commit -m "fix(printing): swap the printer list instead of editing it mid-print"
```

---

## Task 8: Дробное количество на чеке обмена

**Files:**
- Create: `src/VvCash/Models/QuantityFormat.cs`
- Create: `tests/VvCash.Tests/QuantityFormatTest.cs`
- Modify: `src/VvCash/Models/ReturnReceiptLine.cs`
- Modify: `src/VvCash/Models/CartItem.cs:74-76,97-99`
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs` (три строки с `x{l.Quantity}`)
- Modify: `src/VvCash/ViewModels/ExchangeViewModel.cs:858`

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/VvCash.Tests/QuantityFormatTest.cs`:

```csharp
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class QuantityFormatTest
{
    [Theory]
    [InlineData(2.0, "2")]
    [InlineData(1.4, "1.4")]
    [InlineData(1.400, "1.4")]
    [InlineData(0.5, "0.5")]
    [InlineData(53, "53")]
    public void Display_DropsTrailingZeroesButKeepsRealFractions(decimal value, string expected)
    {
        Assert.Equal(expected, QuantityFormat.Display(value, "0.###"));
    }

    [Fact]
    public void Display_UsesTheInvariantSeparator()
    {
        // Точка, а не запятая, на любой локали ОС: тот же чек не должен печататься
        // по-разному на соседних кассах.
        Assert.Equal("1.4", QuantityFormat.Display(1.4m, "0.###"));
    }

    [Fact]
    public void Display_HonoursTheRequestedPrecision()
    {
        Assert.Equal("12.720001", QuantityFormat.Display(12.720001m, "0.######"));
        Assert.Equal("12.72", QuantityFormat.Display(12.720001m, "0.###"));
    }
}
```

Дописать в `tests/VvCash.Tests/EscPosExchangeTest.cs` внутрь класса:

```csharp
    [Fact]
    public void ExchangeReceipt_PrintsAFractionalIssuedQuantity()
    {
        // 1.4 кг печаталось как «x1»: ReturnReceiptLine объявлял int, а выданная
        // сторона обмена приводила туда decimal.
        var bytes = EscPosPrinterService.BuildExchangeReceipt(
            EscPosCodePages.Cp866,
            System.Array.Empty<ReturnReceiptLine>(),
            new[] { new ReturnReceiptLine("Сахар", 1.4m, 70m) },
            difference: 70m, documentNumber: "EX-2");

        var text = EscPosCodePages.Cp866.Encoding.GetString(bytes);

        Assert.Contains("Сахар x1.4", text);
        Assert.DoesNotContain("Сахар x1 ", text);
    }

    [Fact]
    public void ExchangeReceipt_StillPrintsAWholeQuantityWithoutADecimalTail()
    {
        var bytes = EscPosPrinterService.BuildExchangeReceipt(
            EscPosCodePages.Cp866,
            new[] { new ReturnReceiptLine("Товар", 2m, 20m) },
            System.Array.Empty<ReturnReceiptLine>(),
            difference: -20m, documentNumber: "EX-3");

        Assert.Contains("Товар x2", EscPosCodePages.Cp866.Encoding.GetString(bytes));
    }
```

- [ ] **Step 2: Прогнать — должно упасть**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityFormatTest|FullyQualifiedName~EscPosExchangeTest"
```

Ожидание: `CS0246: The type or namespace name 'QuantityFormat' could not be found` и `CS1503: cannot convert from 'decimal' to 'int'`.

- [ ] **Step 3: Хелпер**

Создать `src/VvCash/Models/QuantityFormat.cs`:

```csharp
using System.Globalization;

namespace VvCash.Models;

/// <summary>Количество без хвостовых нулей, одинаково на экране и на чеке.
///
/// Одним местом, а не тремя: у CartItem уже было две копии этой логики, и третья
/// на чеке — прямой путь к расхождению между тем, что кассир видит в корзине, и
/// тем, что печатается покупателю.</summary>
public static class QuantityFormat
{
    /// <summary>Инвариантный разделитель намеренно: тот же чек печатался 20.00 на
    /// одной кассе и 20,00 на соседней, пока формат брался из локали ОС.</summary>
    public static string Display(decimal value, string fractionFormat)
        => value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(fractionFormat, CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Модель строки чека**

`src/VvCash/Models/ReturnReceiptLine.cs` целиком:

```csharp
namespace VvCash.Models;

/// <summary>Строка чека возврата и обмена. Quantity — decimal, потому что выданная
/// сторона обмена может быть дробной (1.4 кг). Возвращаемая сторона честно целая:
/// ReturnLineVm.ReturnQty — int, серверный ReturnLineRequest.Quantity тоже, так что
/// decimal покрывает оба случая без выдумывания дробных возвратов.</summary>
public record ReturnReceiptLine(string Name, decimal Quantity, decimal LineRefund);
```

- [ ] **Step 5: Убрать приведение в обмене**

В `src/VvCash/ViewModels/ExchangeViewModel.cs`, строка 858:

```csharp
                .Select(l => new ReturnReceiptLine(l.Product.Name, l.Quantity, IssuedLineFinalTotal(l))).ToList();
```

- [ ] **Step 6: Чек печатает через хелпер**

В `src/VvCash/Services/Hardware/EscPosPrinterService.cs` заменить три строки, где количество попадает на бумагу.

`BuildReturnReceipt` (около строки 240):

```csharp
        foreach (var l in lines)
            WriteLine(ms, PadLine($"{l.Name} x{QuantityFormat.Display(l.Quantity, "0.###")}", Money(l.LineRefund), 32), codePage);
```

`BuildExchangeReceipt`, обе секции (около строк 308 и 312):

```csharp
        WriteLine(ms, "RETURNED:", codePage);
        foreach (var l in returned)
            WriteLine(ms, PadLine($"{l.Name} x{QuantityFormat.Display(l.Quantity, "0.###")}", Money(l.LineRefund), 32), codePage);

        WriteLine(ms, "ISSUED:", codePage);
        foreach (var l in issued)
            WriteLine(ms, PadLine($"{l.Name} x{QuantityFormat.Display(l.Quantity, "0.###")}", Money(l.LineRefund), 32), codePage);
```

- [ ] **Step 7: `CartItem` перестаёт держать свою копию**

В `src/VvCash/Models/CartItem.cs` заменить два свойства (строки 74-76 и 97-99):

```csharp
    public string QuantityDisplay => QuantityFormat.Display(Quantity, "0.###");
```

```csharp
    public string QuantityInUnitDisplay => QuantityFormat.Display(QuantityInUnit, "0.######");
```

Если после этого `using System.Globalization;` в `CartItem.cs` больше нигде не нужен — удалить его; если нужен (`Money`, другие форматы) — оставить. Проверить: `grep -n "CultureInfo" src/VvCash/Models/CartItem.cs`.

- [ ] **Step 8: Прогнать всё**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`. Прежние тесты `EscPosUnitTest` на `Плитка x53` и `Товар x2` должны продолжать проходить — хелпер отбрасывает нулевую дробь.

- [ ] **Step 9: Коммит**

```bash
git add src/VvCash/Models/QuantityFormat.cs src/VvCash/Models/ReturnReceiptLine.cs src/VvCash/Models/CartItem.cs src/VvCash/Services/Hardware/EscPosPrinterService.cs src/VvCash/ViewModels/ExchangeViewModel.cs tests/VvCash.Tests/QuantityFormatTest.cs tests/VvCash.Tests/EscPosExchangeTest.cs
git commit -m "fix(receipt): print fractional exchange quantities as issued"
```

---

## Task 9: Мёртвый код и честное имя заглушки дисплея

**Files:**
- Delete: `src/VvCash/Services/MockProductService.cs`, `src/VvCash/Services/Hardware/MockPrinterService.cs`, `src/VvCash/Services/Hardware/MockCustomerDisplayService.cs`
- Create: `src/VvCash/Services/Hardware/NullCustomerDisplayService.cs`
- Modify: `src/VvCash/App.axaml.cs:393`

- [ ] **Step 1: Убедиться, что удаляемое действительно не используется**

```bash
grep -rn "MockProductService\|MockPrinterService" --include=*.cs --include=*.axaml src/ tests/
```

Ожидание: только собственные определения (`src/VvCash/Services/MockProductService.cs:8`, `src/VvCash/Services/Hardware/MockPrinterService.cs:8`). Если нашлось что-то ещё — **остановиться** и разобраться, а не удалять.

- [ ] **Step 2: Удалить два мёртвых файла**

```bash
git rm src/VvCash/Services/MockProductService.cs src/VvCash/Services/Hardware/MockPrinterService.cs
```

- [ ] **Step 3: Заменить мок дисплея на честную заглушку**

```bash
git rm src/VvCash/Services/Hardware/MockCustomerDisplayService.cs
```

Создать `src/VvCash/Services/Hardware/NullCustomerDisplayService.cs`:

```csharp
using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

/// <summary>Касса без VFD. Это нормальное состояние, а не отсутствие реализации:
/// дисплей покупателя есть далеко не на каждой точке.
///
/// Пришёл на смену MockCustomerDisplayService, который был зарегистрирован боевым
/// и сорил каждой продажей в консоль. Имя врало: «mock» обещает подмену на время
/// тестов, а это рабочее поведение ненастроенной кассы.
///
/// Возвращает true: «показывать нечего» — не отказ, и кнопка проверки дисплея на
/// такой кассе не должна показывать ошибку.</summary>
public class NullCustomerDisplayService : ICustomerDisplayService
{
    public Task<bool> ShowLineAsync(string line1, string line2) => Task.FromResult(true);
    public Task<bool> ShowItemAsync(string name, decimal price) => Task.FromResult(true);
    public Task<bool> ShowTotalAsync(decimal total) => Task.FromResult(true);
    public Task<bool> ClearAsync() => Task.FromResult(true);
}
```

Сигнатуры уже под `Task<bool>` — интерфейс меняется в Task 10, поэтому этот файл до Task 10 **не соберётся**. Так и задумано: Task 9 и Task 10 коммитятся подряд, а промежуточная сборка проверяется в конце Task 10. Если нужен зелёный коммит здесь — временно оставить `Task` без `<bool>` и вернуть в Task 10.

- [ ] **Step 4: Перерегистрировать в DI**

В `src/VvCash/App.axaml.cs`, строка 393:

```csharp
        services.AddSingleton<ICustomerDisplayService, NullCustomerDisplayService>();
```

(в Task 11 она станет `ConfiguredCustomerDisplayService`; здесь — чтобы сборка не разъезжалась)

- [ ] **Step 5: Коммит**

```bash
git add -A src/VvCash/Services src/VvCash/App.axaml.cs
git commit -m "refactor(hardware): drop dead mocks and name the empty display honestly"
```

---

## Task 10: Дисплей получает канал для ошибки и перестаёт калечить кириллицу

**Files:**
- Modify: `src/VvCash/Services/Hardware/ICustomerDisplayService.cs`
- Modify: `src/VvCash/Services/Hardware/VfdDisplayService.cs` (переписывается целиком)
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (пять вызовов — только если компилятор потребует)
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs:247` (`FakeCustomerDisplayService`)
- Create: `tests/VvCash.Tests/CustomerDisplayTest.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/VvCash.Tests/CustomerDisplayTest.cs`:

```csharp
using System.Threading.Tasks;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class CustomerDisplayTest
{
    [Fact]
    public async Task NullDisplay_ReportsSuccess()
    {
        // Касса без VFD — нормальное состояние, а не отказ.
        var display = new NullCustomerDisplayService();

        Assert.True(await display.ShowTotalAsync(100m));
        Assert.True(await display.ClearAsync());
    }

    [Fact]
    public async Task Vfd_OnAPortThatDoesNotExist_ReportsFailure()
    {
        // До правки SendAsync ловил всё и писал в Console, то есть отказ порта был
        // неотличим от успеха — ровно та болезнь, которую чинит проблема 1.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        Assert.False(await display.ShowTotalAsync(100m));
    }

    [Fact]
    public async Task Vfd_DoesNotPrintADollarSign()
    {
        // Магазины не берут доллары; на чеке это уже чинили.
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        // Строка собирается до попытки открыть порт, поэтому её видно даже когда
        // отправка провалилась.
        Assert.DoesNotContain("$", display.LastRendered);
        await display.ShowTotalAsync(100m);
        Assert.DoesNotContain("$", display.LastRendered);
        Assert.Contains("100.00", display.LastRendered);
    }

    [Fact]
    public async Task Vfd_RendersTwentyColumnsPerLine()
    {
        var display = new VfdDisplayService("COM-does-not-exist", 9600, VvCash.Models.EscPosCodePages.Cp866);

        await display.ShowLineAsync("Молоко", "50.00");

        Assert.Equal(40, display.LastRendered.Length);
        Assert.StartsWith("Молоко              ", display.LastRendered);
    }
}
```

- [ ] **Step 2: Прогнать — должно упасть**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayTest"
```

Ожидание: `CS1729: 'VfdDisplayService' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Интерфейс получает канал для отказа**

`src/VvCash/Services/Hardware/ICustomerDisplayService.cs` целиком:

```csharp
using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

/// <summary>Витрина покупателя.
///
/// Возвращает bool, а не голый Task: без него кнопка «Проверить дисплей» на экране
/// настроек физически не может отчитаться, а служба остаётся при той же болезни
/// «рапортует успех», которую батч чинит у USB-печати.
///
/// Пять вызовов из PosViewModel результат намеренно не ждут (`_ = …`): витрина не
/// должна ни задерживать продажу, ни ронять её, если у неё отвалился COM-порт.
/// bool заведён ради единственного места, где на него есть кому смотреть.</summary>
public interface ICustomerDisplayService
{
    Task<bool> ShowLineAsync(string line1, string line2);
    Task<bool> ShowItemAsync(string name, decimal price);
    Task<bool> ShowTotalAsync(decimal total);
    Task<bool> ClearAsync();
}
```

- [ ] **Step 4: Переписать VFD**

`src/VvCash/Services/Hardware/VfdDisplayService.cs` целиком:

```csharp
using System;
using System.IO.Ports;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Двухстрочный VFD на последовательном порту.
///
/// Реализация сознательно консервативная. Инициализацию (ESC @) и выбор кодовой
/// страницы (ESC t n) понимают практически все VFD; команды позиционирования
/// курсора у моделей расходятся сильнее, чем у принтеров, поэтому их здесь нет —
/// 40 символов двумя строками по 20, и модель раскладывает их сама.</summary>
public class VfdDisplayService : ICustomerDisplayService
{
    private const int Columns = 20;

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly EscPosCodePage _codePage;

    /// <summary>Последнее, что уходило на дисплей. Существует ради тестов: открыть
    /// настоящий COM-порт в юнит-тесте нельзя, а разметку и отсутствие доллара
    /// проверить надо.</summary>
    public string LastRendered { get; private set; } = string.Empty;

    public VfdDisplayService(string portName, int baudRate, EscPosCodePage codePage)
    {
        _portName = portName;
        _baudRate = baudRate;
        _codePage = codePage;
    }

    public Task<bool> ShowLineAsync(string line1, string line2)
        => SendAsync(Pad(line1) + Pad(line2));

    // Без валюты: символ был зашит в "$" на кассах, которые долларов не берут —
    // ровно то же, что уже чинили на чеке.
    public Task<bool> ShowItemAsync(string name, decimal price)
        => ShowLineAsync(name, Money(price));

    public Task<bool> ShowTotalAsync(decimal total)
        => ShowLineAsync("TOTAL", Money(total));

    public Task<bool> ClearAsync() => SendAsync(new string(' ', Columns * 2));

    private static string Money(decimal value)
        => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<bool> SendAsync(string text)
    {
        LastRendered = text;

        try
        {
            using var port = new SerialPort(_portName, _baudRate);
            port.Open();

            // ESC @, затем ESC t n. Без инициализации дисплей копит мусор от
            // предыдущей строки; без кодовой страницы кириллица уходит в ASCII и
            // превращается в вопросительные знаки.
            var prologue = new byte[] { 0x1B, 0x40, 0x1B, 0x74, _codePage.EscTSelector };
            await port.BaseStream.WriteAsync(prologue, 0, prologue.Length);

            var bytes = _codePage.Encoding.GetBytes(text);
            await port.BaseStream.WriteAsync(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            // Логируется, но не глотается: возвращённый false — единственное, по
            // чему кнопка проверки отличит рабочий дисплей от мёртвого порта.
            Console.WriteLine($"VFD error: {ex.Message}");
            return false;
        }
    }

    private static string Pad(string text)
        => text.Length >= Columns ? text[..Columns] : text.PadRight(Columns);
}
```

- [ ] **Step 5: Починить фейк в тестах**

В `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`, класс `FakeCustomerDisplayService` (строка 247) — привести четыре метода к `Task<bool>`, возвращая `Task.FromResult(true)`. Точную нынешнюю форму посмотреть на месте; менять только тип возврата и `return`.

- [ ] **Step 6: Прогнать**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`. Пять вызовов в `PosViewModel` (`_ = _customerDisplayService.…` на строках 1184, 1545, 1574, 1956, 2343) компилируются без правки — `_ =` принимает `Task<bool>` так же, как `Task`.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Services/Hardware/ICustomerDisplayService.cs src/VvCash/Services/Hardware/VfdDisplayService.cs src/VvCash/Services/Hardware/NullCustomerDisplayService.cs tests/VvCash.Tests/CustomerDisplayTest.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "fix(display): let the VFD report failure and speak Cyrillic"
```

---

## Task 11: Дисплей настраивается и переживает смену настроек

**Files:**
- Modify: `src/VvCash/Services/ISettingsService.cs`
- Modify: `src/VvCash/Services/SettingsService.cs` (`SettingsData`, свойства, `Load`)
- Create: `src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs`
- Modify: `src/VvCash/App.axaml.cs:393`
- Modify: 15 файлов с фейками `ISettingsService`
- Modify: `tests/VvCash.Tests/CustomerDisplayTest.cs`

- [ ] **Step 1: Написать падающий тест**

Дописать в `tests/VvCash.Tests/CustomerDisplayTest.cs` внутрь класса (фейк настроек взять тот же, что в `CompositePrinterServiceTest`, добавив его копию в этот файл — так же, как это сделано во всех 15 существующих тестовых файлах):

```csharp
    [Fact]
    public async Task ConfiguredDisplay_WithNoPortSet_IsSilentAndSucceeds()
    {
        var settings = new FakeSettings { CustomerDisplayPort = string.Empty };
        var display = new ConfiguredCustomerDisplayService(settings);

        Assert.True(await display.ShowTotalAsync(10m));
    }

    [Fact]
    public async Task ConfiguredDisplay_PicksUpANewPortWithoutARestart()
    {
        // Иначе после настройки порта кассу пришлось бы перезапускать — тот же
        // приём, что у CompositePrinterService.
        var settings = new FakeSettings { CustomerDisplayPort = string.Empty };
        var display = new ConfiguredCustomerDisplayService(settings);
        Assert.True(await display.ShowTotalAsync(10m));

        settings.CustomerDisplayPort = "COM-does-not-exist";
        settings.Save();

        Assert.False(await display.ShowTotalAsync(10m));
    }
```

- [ ] **Step 2: Прогнать — должно упасть**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerDisplayTest"
```

Ожидание: `CS0246: The type or namespace name 'ConfiguredCustomerDisplayService' could not be found`.

- [ ] **Step 3: Три настройки в интерфейсе**

В `src/VvCash/Services/ISettingsService.cs`, после `PhoneFormatId`:

```csharp
    /// <summary>COM-порт дисплея покупателя. Пусто — VFD на этой кассе нет, и это
    /// нормальное состояние, а не незаконченная настройка.</summary>
    string CustomerDisplayPort { get; set; }

    /// <summary>Скорость порта. Своя настройка, потому что 9600 было зашито, а VFD
    /// встречаются на 2400 и 19200.</summary>
    int CustomerDisplayBaudRate { get; set; }

    /// <summary>Id записи из EscPosCodePages — своя, отдельная от принтерной:
    /// дисплей и принтер это разные железки с разными таблицами.</summary>
    string CustomerDisplayCodePageId { get; set; }
```

- [ ] **Step 4: Хранилище**

В `src/VvCash/Services/SettingsService.cs`, в `SettingsData` после `PhoneFormatId` (строка 22):

```csharp
    public string CustomerDisplayPort { get; set; } = string.Empty;
    public int CustomerDisplayBaudRate { get; set; } = 9600;
    public string CustomerDisplayCodePageId { get; set; } = string.Empty;
```

В классе `SettingsService`, рядом с `PhoneFormatId` (около строки 98):

```csharp
    public string CustomerDisplayPort
    {
        get => _data.CustomerDisplayPort;
        set => _data.CustomerDisplayPort = value;
    }

    /// <summary>Ноль и отрицательное читаются как 9600 — тем же приёмом, что
    /// SyncIntervalMinutes выше: settings.json правят руками.</summary>
    public int CustomerDisplayBaudRate
    {
        get => _data.CustomerDisplayBaudRate <= 0 ? 9600 : _data.CustomerDisplayBaudRate;
        set => _data.CustomerDisplayBaudRate = value;
    }

    public string CustomerDisplayCodePageId
    {
        get => _data.CustomerDisplayCodePageId;
        set => _data.CustomerDisplayCodePageId = value;
    }
```

В `Load`, к цепочке проверок после `PhoneFormatId` (около строки 152):

```csharp
                if (_data.CustomerDisplayPort == null)
                {
                    _data.CustomerDisplayPort = string.Empty;
                }
                if (_data.CustomerDisplayBaudRate <= 0)
                {
                    _data.CustomerDisplayBaudRate = 9600;
                }
                if (_data.CustomerDisplayCodePageId == null)
                {
                    _data.CustomerDisplayCodePageId = string.Empty;
                }
```

- [ ] **Step 5: Обёртка, пересобирающая дисплей**

Создать `src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs`:

```csharp
using System;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Дисплей покупателя такой, каким его задали настройки — и пересобранный,
/// как только их поменяли.
///
/// По образцу CompositePrinterService, а не «прочитать настройки один раз при
/// старте»: иначе после настройки порта пришлось бы перезапускать кассу, а кнопка
/// «Проверить дисплей» проверяла бы прошлую конфигурацию.
///
/// Не «Composite»: дисплей на кассе один, складывать нечего. Общее с принтерным
/// композитом — только подписка на SettingsChanged и подмена внутренностей одним
/// движением.</summary>
public class ConfiguredCustomerDisplayService : ICustomerDisplayService
{
    private readonly ISettingsService _settingsService;

    /// <summary>volatile по той же причине, что _printers у композита: присваивание
    /// ссылки атомарно, но атомарность — не видимость.</summary>
    private volatile ICustomerDisplayService _inner = new NullCustomerDisplayService();

    public ConfiguredCustomerDisplayService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var port = _settingsService.CustomerDisplayPort;

        _inner = string.IsNullOrWhiteSpace(port)
            ? new NullCustomerDisplayService()
            : new VfdDisplayService(
                port,
                _settingsService.CustomerDisplayBaudRate,
                EscPosCodePages.Resolve(_settingsService.CustomerDisplayCodePageId));
    }

    public Task<bool> ShowLineAsync(string line1, string line2) => _inner.ShowLineAsync(line1, line2);
    public Task<bool> ShowItemAsync(string name, decimal price) => _inner.ShowItemAsync(name, price);
    public Task<bool> ShowTotalAsync(decimal total) => _inner.ShowTotalAsync(total);
    public Task<bool> ClearAsync() => _inner.ClearAsync();
}
```

- [ ] **Step 6: Регистрация**

В `src/VvCash/App.axaml.cs`, строка 393:

```csharp
        services.AddSingleton<ICustomerDisplayService, ConfiguredCustomerDisplayService>();
```

- [ ] **Step 7: Дополнить 16 фейков `ISettingsService`**

Три новых члена интерфейса ломают все реализации в тестах. В каждый класс добавить, рядом с `PhoneFormatId`:

```csharp
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
```

Полный список (16 классов в 15 файлах):

```
tests/VvCash.Tests/AuthServiceTest.cs                    FakeSettings
tests/VvCash.Tests/CashOperationServiceTest.cs           FakeSettings
tests/VvCash.Tests/CounterpartyServiceTest.cs            FakeSettings
tests/VvCash.Tests/CustomerRegistrationViewModelTest.cs  FakeSettingsService
tests/VvCash.Tests/ExchangeViewModelTest.cs              FakeSettings
tests/VvCash.Tests/ExpenseDocumentServiceTest.cs         FakeSettings
tests/VvCash.Tests/PaymentCategoryServiceTest.cs         FakeSettings
tests/VvCash.Tests/PosViewModelSellerGateTest.cs         FakeSettingsService
tests/VvCash.Tests/QuoteServiceTest.cs                   FakeSettings
tests/VvCash.Tests/ReturnServiceTest.cs                  FakeSettings
tests/VvCash.Tests/ReturnsViewModelTest.cs               FakeSettings
tests/VvCash.Tests/SellerRosterServiceTest.cs            FakeSettings
tests/VvCash.Tests/SellerRosterServiceTest.cs            ThrowingBackendUrlSettings
tests/VvCash.Tests/SettingsViewModelTest.cs              FakeSettings
tests/VvCash.Tests/ShiftServiceTest.cs                   FakeSettings
tests/VvCash.Tests/SyncServiceTest.cs                    FakeSettings
```

Проверить, что никого не пропустили:

```bash
grep -rc "CustomerDisplayPort" tests/VvCash.Tests/*.cs | grep ":0$"
```

Ожидание: в выводе нет ни одного из перечисленных выше файлов.

- [ ] **Step 8: Прогнать**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`.

- [ ] **Step 9: Коммит**

```bash
git add src/VvCash/Services/ISettingsService.cs src/VvCash/Services/SettingsService.cs src/VvCash/Services/Hardware/ConfiguredCustomerDisplayService.cs src/VvCash/App.axaml.cs tests/VvCash.Tests
git commit -m "feat(display): configure the VFD and rebuild it when settings change"
```

---

## Task 12: Кнопки проверки, поля настроек и переводы

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`
- Modify: `src/VvCash/Views/SettingsView.axaml`
- Modify: `src/VvCash/Assets/i18n/{ru,en,tg,uz,kk}.json`
- Modify: `tests/VvCash.Tests/SettingsViewModelTest.cs`

- [ ] **Step 1: Завести десять ключей в пяти локалях**

Вставлять сразу после строки `"Enabled": …` (в `ru.json` это строка 85, в остальных — 72). Терминология сверена с соседними строками того же файла.

`ru.json`:

```json
  "CodePage": "Кодовая страница",
  "TestPrint": "Пробная печать",
  "TestPrintSent": "Пробный чек отправлен",
  "TestPrintFailed": "Пробная печать не удалась:",
  "CustomerDisplay": "Дисплей покупателя",
  "DisplayPort": "Порт",
  "DisplayBaudRate": "Скорость",
  "CheckDisplay": "Проверить дисплей",
  "DisplayCheckOk": "На дисплей отправлена проверочная строка",
  "DisplayCheckFailed": "Проверка дисплея не удалась:",
```

`en.json`:

```json
  "CodePage": "Code page",
  "TestPrint": "Test print",
  "TestPrintSent": "Test receipt sent",
  "TestPrintFailed": "Test print failed:",
  "CustomerDisplay": "Customer display",
  "DisplayPort": "Port",
  "DisplayBaudRate": "Baud rate",
  "CheckDisplay": "Check display",
  "DisplayCheckOk": "Test line sent to the display",
  "DisplayCheckFailed": "Display check failed:",
```

`tg.json`:

```json
  "CodePage": "Саҳифаи рамзӣ",
  "TestPrint": "Чопи санҷишӣ",
  "TestPrintSent": "Чеки санҷишӣ фиристода шуд",
  "TestPrintFailed": "Чопи санҷишӣ иҷро нашуд:",
  "CustomerDisplay": "Дисплейи харидор",
  "DisplayPort": "Порт",
  "DisplayBaudRate": "Суръат",
  "CheckDisplay": "Санҷиши дисплей",
  "DisplayCheckOk": "Сатри санҷишӣ ба дисплей фиристода шуд",
  "DisplayCheckFailed": "Санҷиши дисплей иҷро нашуд:",
```

`uz.json`:

```json
  "CodePage": "Kod sahifasi",
  "TestPrint": "Sinov chop etish",
  "TestPrintSent": "Sinov cheki yuborildi",
  "TestPrintFailed": "Sinov chop etish bajarilmadi:",
  "CustomerDisplay": "Xaridor displeyi",
  "DisplayPort": "Port",
  "DisplayBaudRate": "Tezlik",
  "CheckDisplay": "Displeyni tekshirish",
  "DisplayCheckOk": "Displeyga sinov satri yuborildi",
  "DisplayCheckFailed": "Displeyni tekshirish bajarilmadi:",
```

`kk.json`:

```json
  "CodePage": "Код беті",
  "TestPrint": "Сынақ басып шығару",
  "TestPrintSent": "Сынақ чегі жіберілді",
  "TestPrintFailed": "Сынақ басып шығару орындалмады:",
  "CustomerDisplay": "Сатып алушы дисплейі",
  "DisplayPort": "Порт",
  "DisplayBaudRate": "Жылдамдық",
  "CheckDisplay": "Дисплейді тексеру",
  "DisplayCheckOk": "Дисплейге сынақ жолы жіберілді",
  "DisplayCheckFailed": "Дисплейді тексеру орындалмады:",
```

- [ ] **Step 2: Проверить, что все пять файлов остались валидным JSON и содержат все ключи**

```bash
for f in src/VvCash/Assets/i18n/*.json; do python -c "import json,sys; d=json.load(open(sys.argv[1],encoding='utf-8-sig')); ks=['CodePage','TestPrint','TestPrintSent','TestPrintFailed','CustomerDisplay','DisplayPort','DisplayBaudRate','CheckDisplay','DisplayCheckOk','DisplayCheckFailed']; missing=[k for k in ks if k not in d]; print(sys.argv[1], 'OK' if not missing else missing)" "$f"; done
```

Ожидание: пять строк, каждая заканчивается `OK`.

- [ ] **Step 3: Написать падающий тест**

Дописать в `tests/VvCash.Tests/SettingsViewModelTest.cs`:

```csharp
    [Fact]
    public async Task TestPrint_OnAnUnreachablePrinter_ReportsTheReasonRatherThanStayingSilent()
    {
        // Точка проверяет кодовую страницу этой кнопкой, поэтому молчащий отказ
        // означает звонок разработчику — то есть кнопка не сделала того, ради
        // чего заведена.
        var vm = Build(out _);
        vm.AddPrinterCommand.Execute(null);
        vm.Printers[0].ConnectionType = PrinterConnectionType.LAN;
        vm.Printers[0].ConnectionString = "127.0.0.1:9199";

        await vm.TestPrintCommand.ExecuteAsync(vm.Printers[0]);

        Assert.True(vm.HasError);
        Assert.Empty(vm.StatusMessage);
    }

    [Fact]
    public async Task CheckDisplay_WithNoPortConfigured_ReportsSuccess()
    {
        // Касса без VFD — не отказ.
        var vm = BuildWith(new FakeSettings { CustomerDisplayPort = string.Empty });

        await vm.CheckDisplayCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.NotEmpty(vm.StatusMessage);
    }
```

Первый тест объявляется `public async Task TestPrint_…`. Обе команды объявлены как `private async Task` (Step 6), поэтому `[RelayCommand]` порождает `IAsyncRelayCommand` с `ExecuteAsync` — синхронный `Execute` вернул бы управление до отправки и тест ловил бы гонку. `BuildWith` добавлен в Task 6, Step 1.

- [ ] **Step 4: Прогнать — должно упасть**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelTest"
```

Ожидание: `CS1061: 'SettingsViewModel' does not contain a definition for 'TestPrintCommand'`.

- [ ] **Step 5: Нейтральный баннер результата**

В `src/VvCash/ViewModels/SettingsViewModel.cs`, рядом с `_errorMessage` (строка 95):

```csharp
    /// <summary>Результат кнопок проверки, когда он не отказ. Отдельно от
    /// ErrorMessage: тот красный, с иконкой предупреждения, и «Пробный чек
    /// отправлен» в такой рамке читается как отказ. Пустеет при каждой новой
    /// проверке, чтобы прошлый успех не висел над свежей ошибкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
```

- [ ] **Step 6: Две команды**

В `src/VvCash/ViewModels/SettingsViewModel.cs`, рядом с `RemovePrinterCommand` (после строки 243):

```csharp
    /// <summary>Печатает образец на том, что сейчас на экране, а НЕ на сохранённых
    /// настройках. Иначе связка «поменял кодовую страницу → напечатал → посмотрел»
    /// требовала бы сохранения и выхода с экрана, то есть перестала бы быть
    /// проверкой этого выбора.
    ///
    /// Команда живёт на SettingsViewModel, а не на строке принтера, ровно как
    /// RemovePrinter рядом: строка не знает про баннеры, а привязка
    /// $parent[UserControl].DataContext с CommandParameter="{Binding}" на этом
    /// экране уже работает.</summary>
    [RelayCommand]
    private async Task TestPrint(PrinterConfigViewModel? printer)
    {
        if (printer == null) return;

        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        var service = new EscPosPrinterService(
            printer.ConnectionType,
            printer.ConnectionString,
            printer.SelectedCodePage ?? EscPosCodePages.Default);

        try
        {
            await service.PrintTestReceiptAsync();
            StatusMessage = I18nService.Instance["TestPrintSent"];
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{I18nService.Instance["TestPrintFailed"]} {ex.Message}";
        }
    }

    /// <summary>Строит дисплей из того, что сейчас в полях, по той же причине.
    /// Одна кнопка, а не по одной на запись: дисплей на кассе один.</summary>
    [RelayCommand]
    private async Task CheckDisplay()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        ICustomerDisplayService display = string.IsNullOrWhiteSpace(CustomerDisplayPort)
            ? new NullCustomerDisplayService()
            : new VfdDisplayService(
                CustomerDisplayPort,
                int.TryParse(CustomerDisplayBaudRateText, out var baud) && baud > 0 ? baud : 9600,
                SelectedDisplayCodePage ?? EscPosCodePages.Default);

        var ok = await display.ShowLineAsync("VV CASH", "Проверка / Test");

        if (ok) StatusMessage = I18nService.Instance["DisplayCheckOk"];
        else ErrorMessage = I18nService.Instance["DisplayCheckFailed"];
    }
```

- [ ] **Step 7: Три поля VFD во вью-модели**

В `src/VvCash/ViewModels/SettingsViewModel.cs`, рядом с `_returnPrintReceipt` (около строки 137):

```csharp
    [ObservableProperty]
    private string _customerDisplayPort = string.Empty;

    /// <summary>Строкой, а не int: то же, что SyncIntervalText рядом — TextBox с
    /// частично набранным числом не должен ронять привязку.</summary>
    [ObservableProperty]
    private string _customerDisplayBaudRateText = "9600";

    [ObservableProperty]
    private EscPosCodePage? _selectedDisplayCodePage = EscPosCodePages.Default;
```

**Здесь риск из Task 6 становится настоящим.** До этой задачи `SelectedCodePage` и `SelectedDisplayCodePage` никуда не были привязаны, то есть занулить их было некому. Теперь ComboBox привязан, а `SelectingItemsControl` приводит `SelectedItem` к `null` и пишет его обратно через TwoWay, если присвоенного значения не нашлось в `ItemsSource` по ссылке. Сегодня находится всегда — `Resolve` возвращает экземпляры из `All`, — но ровно на этот незаписанный инвариант комментарий у `SelectedPhoneFormat` просит не опираться. `?? EscPosCodePages.Default` в `Save` это и покрывает: откат на CP866 совпадает с тем, что `Resolve` отдаёт ненастроенному принтеру.

```csharp

    /// <summary>COM-порты машины. Тот же источник, что у принтеров на COM.</summary>
    public ObservableCollection<string> AvailableDisplayPorts { get; } = new();

    public IReadOnlyList<EscPosCodePage> AvailableCodePages { get; } = EscPosCodePages.All;
```

В конструкторе, после `ReturnPrintReceipt = …` (строка 192):

```csharp
        CustomerDisplayPort = _settingsService.CustomerDisplayPort;
        CustomerDisplayBaudRateText = _settingsService.CustomerDisplayBaudRate.ToString();
        SelectedDisplayCodePage = EscPosCodePages.Resolve(_settingsService.CustomerDisplayCodePageId);
        foreach (var port in PrinterDiscoveryService.GetComPorts())
            AvailableDisplayPorts.Add(port);
```

В `Save`, рядом с `_settingsService.ReturnPrintReceipt = …` (около строки 366):

```csharp
        _settingsService.CustomerDisplayPort = CustomerDisplayPort;
        if (int.TryParse(CustomerDisplayBaudRateText, out var displayBaud) && displayBaud > 0)
            _settingsService.CustomerDisplayBaudRate = displayBaud;
        if (SelectedDisplayCodePage != null)
            _settingsService.CustomerDisplayCodePageId = SelectedDisplayCodePage.Id;
```

И в самом начале `Save`, там где уже стоит `ErrorMessage = string.Empty;` (строка 345), добавить строкой ниже:

```csharp
        StatusMessage = string.Empty;
```

- [ ] **Step 8: Разметка — колонка кодовой страницы и кнопка в карточке принтера**

В `src/VvCash/Views/SettingsView.axaml` заменить `ColumnDefinitions` внутри `DataTemplate` (строка 220):

```xml
                                        <Grid ColumnDefinitions="*, *, *, Auto, Auto, Auto, Auto" RowDefinitions="Auto, Auto, Auto">
```

Добавить подпись в строку меток, после подписи `Enabled` (после строки 228):

```xml
                                            <TextBlock Grid.Row="0" Grid.Column="4" Text="{Binding [CodePage], Source={x:Static services:I18nService.Instance}}" FontSize="11" FontWeight="SemiBold" Foreground="{StaticResource Slate500Brush}" Margin="0,0,8,4"/>
```

Заменить `CheckBox` и кнопку удаления (строки 233-238) на:

```xml
                                            <CheckBox Grid.Row="1" Grid.Column="3" IsChecked="{Binding IsEnabled, Mode=TwoWay}" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,0,8,0"/>
                                            <ComboBox Grid.Row="1" Grid.Column="4" ItemsSource="{Binding AvailableCodePages}" SelectedItem="{Binding SelectedCodePage, Mode=TwoWay}"
                                                      DisplayMemberBinding="{Binding DisplayName}" Classes="PrinterCombo" Margin="0,0,8,0"/>
                                            <!-- Пробная печать своя у каждой записи: кодовая страница задаётся на
                                                 принтер, и проверять надо именно её. Привязка через
                                                 $parent[UserControl].DataContext — тот же приём, что у кнопки
                                                 удаления справа; приводить DataContext предка к типу VM в XAML
                                                 нельзя, это компилируется и падает в рантайме. -->
                                            <Button Grid.Row="1" Grid.Column="5" VerticalAlignment="Center" Margin="0,0,8,0"
                                                    Command="{Binding $parent[UserControl].DataContext.TestPrintCommand}"
                                                    CommandParameter="{Binding}">
                                                <StackPanel Orientation="Horizontal" Spacing="6">
                                                    <material:MaterialIcon Kind="Printer" Width="18" Height="18"/>
                                                    <TextBlock Text="{Binding [TestPrint], Source={x:Static services:I18nService.Instance}}" VerticalAlignment="Center"/>
                                                </StackPanel>
                                            </Button>
                                            <Button Grid.Row="1" Grid.Column="6" Background="Transparent" Foreground="{StaticResource DangerBrush}" VerticalAlignment="Center"
                                                    Command="{Binding $parent[UserControl].DataContext.RemovePrinterCommand}"
                                                    CommandParameter="{Binding}">
                                                <material:MaterialIcon Kind="TrashCan" Width="20" Height="20"/>
                                            </Button>
```

- [ ] **Step 9: Разметка — карточка дисплея покупателя**

В том же файле, сразу после закрывающего `</ItemsControl>` карточки принтеров (после строки 245) и перед `</StackPanel>`:

```xml
                        <!-- Дисплей покупателя. Одна кнопка проверки, а не по одной
                             на запись: дисплей на кассе один. -->
                        <StackPanel Orientation="Horizontal" Spacing="12" Margin="0,24,0,12">
                            <material:MaterialIcon Kind="CashRegister" Width="24" Height="24" Foreground="{StaticResource Slate700Brush}"/>
                            <TextBlock Text="{Binding [CustomerDisplay], Source={x:Static services:I18nService.Instance}}" FontSize="20" FontWeight="Bold" Foreground="{StaticResource Slate800Brush}" VerticalAlignment="Center"/>
                        </StackPanel>

                        <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="12">
                            <Grid ColumnDefinitions="*, *, *, Auto" RowDefinitions="Auto, Auto">
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
                            </Grid>
                        </Border>
```

- [ ] **Step 10: Разметка — зелёный баннер результата**

В том же файле, непосредственно перед красным баннером `HasError` (перед строкой 297):

```xml
                <!-- Успех проверки. Отдельно от красного баннера ниже: «Пробный чек
                     отправлен» в рамке с иконкой предупреждения читается как отказ. -->
                <Border IsVisible="{Binding HasStatus}" Background="#F0FDF4" BorderBrush="#86EFAC" BorderThickness="1"
                        CornerRadius="10" Padding="14,10" Margin="0,0,0,12">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <material:MaterialIcon Kind="CheckCircle" Width="20" Height="20" Foreground="#15803D" VerticalAlignment="Center"/>
                        <TextBlock Text="{Binding StatusMessage}" Foreground="#15803D" TextWrapping="Wrap" VerticalAlignment="Center"/>
                    </StackPanel>
                </Border>
```

- [ ] **Step 11: Прогнать**

```bash
& ./run-tests.ps1
```

Ожидание: `Failed: 0`.

- [ ] **Step 12: Запустить приложение и посмотреть на экран настроек**

```bash
dotnet run --project src/VvCash/VvCash.csproj -c Debug
```

Проверить глазами: в карточке принтера появилась колонка кодовой страницы и кнопка «Пробная печать»; ниже — карточка «Дисплей покупателя» с портом, скоростью, кодовой страницей и кнопкой; нигде не видно текста в квадратных скобках вида `[CodePage]` (так `I18nService` рендерит недостающий ключ — если такое видно, ключ не доехал в текущую локаль).

Привязки в этом проекте **не компилируемые**: неверный путь соберётся молча и просто ничего не покажет. Поэтому шаг обязательный, а не «если будет время».

- [ ] **Step 13: Коммит**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs src/VvCash/Views/SettingsView.axaml src/VvCash/Assets/i18n tests/VvCash.Tests/SettingsViewModelTest.cs
git commit -m "feat(settings): add test print and display check buttons"
```

---

## Приёмка

Из спеки, дословно: этот батч **нельзя закончить в репозитории** — принтера нет ни у заказчика, ни у исполнителя.

1. **Здесь:** зелёная сборка, зелёные тесты, и код, который перестал рапортовать успех там, где его не было.

```bash
& ./run-tests.ps1
```

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

2. **На точке с принтером:** пробная печать → **русская** строка читается, кодовая страница угадана; мусор — выбрать другую из списка; не подошла ни одна — правка каталога и релиз. Строка `TJ/KK` покажет `?` при **любой** записи каталога — это задокументированная граница, а не промах с выбором.

3. Находки #3 и #4 считаются закрытыми только после шага 2.

---

## Самопроверка плана

**Покрытие спеки:**

| Раздел спеки | Задача |
|---|---|
| Проблема 1 — USB | Task 2 (интероп), Task 3 (имя принтера), Task 4 (текст исключения) |
| Проблема 2 — кодовая страница | Task 1 (каталог + провайдер), Task 5 (билдеры, `WriteInit`, пробный чек), Task 6 (настройка) |
| Проблема 3 — VFD | Task 10 (интерфейс, служба), Task 11 (настройки, пересборка), Task 12 (кнопка, поля) |
| Проблема 4 — дробное количество | Task 8 |
| Проблема 5 — мёртвый код | Task 9 |
| Проблема 10 — гонка | Task 7 |
| i18n | Task 12, Step 1-2 |
| Тестирование | таблица спеки покрыта: каталог (Task 1), байты в кодировке (Task 5), `ESC t n` (Task 5), `WriteInit` во всех четырёх (Task 5), фолбэк `?` (Task 1), количество (Task 8), регистрация провайдера (Task 1) |
| Приёмка | раздел выше |

**Согласованность имён между задачами:** `EscPosCodePage`/`EscPosCodePages` (Task 1) — те же в 5, 6, 7, 10, 11, 12. `WriteInit(ms, codePage)` (Task 5) — зовётся в четырёх местах там же. `QuantityFormat.Display(value, format)` (Task 8) — та же сигнатура в `CartItem` и в чеке. `SelectedCodePage` на строке принтера (Task 6) — читается в Task 12, Step 6. `StatusMessage`/`HasStatus` (Task 12, Step 5) — используется в тестах Step 3 и в разметке Step 10. `CustomerDisplayPort`/`CustomerDisplayBaudRate`/`CustomerDisplayCodePageId` (Task 11) — те же в фейках, в `ConfiguredCustomerDisplayService` и в Task 12.

**Известная нестыковка порядка:** `NullCustomerDisplayService` создаётся в Task 9 уже под `Task<bool>`, а интерфейс меняется в Task 10 — промежуточный коммит Task 9 не соберётся. Указано на месте вместе с обходом. Задачи идут подряд, и `subagent-driven-development` проверяет сборку после каждой — если это неприемлемо, слить Task 9 и Task 10 в одну.
