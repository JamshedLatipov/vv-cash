# Order Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Производственная очередь заказов: касса печатает талон с непредсказуемым номером и бегунок на кухню, один из аппаратов точки поднимает локальный сервер, кухонный экран и табло в зале работают как веб-страницы.

**Architecture:** Номер выдаёт та касса, которая пробила заказ, из своего перемешанного непересекающегося куска пула — поэтому сервер никогда не блокирует продажу. Касса-сервер (Kestrel на 8770) хранит заказы точки и отдаёт `/kds` и `/board`; кассы-клиенты шлют заказы, буферизуя при отказе. Печать и сетевая очередь независимы: касса с `QueueRole = Off` всё равно печатает талон и бегунок.

**Tech Stack:** .NET 10, Avalonia 11.2.3, `Microsoft.Data.Sqlite`, Kestrel (`FrameworkReference Microsoft.AspNetCore.App`), xunit, ванильный JS.

**Спека:** [`docs/superpowers/specs/2026-08-31-order-queue-design.md`](../specs/2026-08-31-order-queue-design.md)

---

## Как запускать тесты

```bash
& ./run-tests.ps1
```

Фильтр по одному классу:

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest"
```

`pwsh` на этой машине нет — запускать через `&`, не `pwsh ./run-tests.ps1`. Скрипт
собирает в `build/verify-tests`, чтобы запущенное приложение не держало вывод.

Полный прогон изредка роняет случайный тест через гонку Avalonia Dispatcher.
Упал тест не по теме задачи — смотреть стек, а не свой диф.

---

## Отклонения от спеки

Одно, сознательное: спека говорит заводить таблицы очереди «через существующий
путь `InitializeCoreAsync`». План кладёт их в **отдельный файл `queue.db`** со
своим `QueueStorage`. Причины: `OfflineStorageService` уже за тысячу строк, а
две независимые схемы в одном файле дают конкурентные записи из двух соединений
и «database is locked» на ровном месте. Механика миграций та же —
`CREATE TABLE IF NOT EXISTS`, ничего нового.

---

## Структура файлов

**Создаются:**

| Файл | Ответственность |
|---|---|
| `src/VvCash/Models/PrintRole.cs` | Флаги ролей принтера |
| `src/VvCash/Models/SaleReceiptData.cs` | Аргументы чека одним объектом (нужен бегунку) |
| `src/VvCash/Models/QueueOrder.cs` | Заказ очереди |
| `src/VvCash/Models/QueueOrderState.cs` | Состояния и допустимые переходы |
| `src/VvCash/Services/Queue/IQueueSettings.cs` | Настройки очереди отдельным интерфейсом |
| `src/VvCash/Services/Queue/IQueueStorage.cs`, `QueueStorage.cs` | SQLite: пул номеров, заказы, исходящий буфер |
| `src/VvCash/Services/Queue/INumberPool.cs`, `NumberPool.cs` | Выдача и возврат номеров |
| `src/VvCash/Services/Queue/IQueueTransport.cs`, `HttpQueueTransport.cs` | HTTP к кассе-серверу |
| `src/VvCash/Services/Queue/IQueueClient.cs`, `QueueClient.cs` | Постановка заказа, буфер, досыл |
| `src/VvCash/Services/Queue/QueueServer.cs` | Kestrel, эндпоинты, вебсокет-рассылка |
| `src/VvCash/Services/Queue/QueueFlushLoop.cs` | Фоновый досыл буфера раз в 15 секунд |
| `src/VvCash/Assets/Web/theme.css`, `kds.html`, `board.html` | Экраны, `EmbeddedResource` |
| `tests/VvCash.Tests/*` | По тесту на задачу, имена в задачах |

**Изменяются:**

| Файл | Что |
|---|---|
| `src/VvCash/Models/PrinterConfig.cs` | `+ Roles` |
| `src/VvCash/Services/Hardware/EscPosPrinterService.cs` | `+ Roles`, `+ BuildTicket`, `queueNumber` в `BuildSaleReceipt`, `SendAsync` → `protected virtual` |
| `src/VvCash/Services/Hardware/IPrinterService.cs` | `+ PrintTicketAsync`, `+ PrintKitchenOrderAsync` |
| `src/VvCash/Services/Hardware/CompositePrinterService.cs` | Фанаут по ролям, фабрика принтеров |
| `src/VvCash/Services/SettingsService.cs` | `SettingsData` + пять полей очереди, реализация `IQueueSettings` |
| `src/VvCash/ViewModels/SettingsViewModel.cs`, `Views/SettingsView.axaml` | Галки ролей, блок настроек очереди |
| `src/VvCash/ViewModels/PosViewModel.cs` | Постановка заказа и печать талона/бегунка после продажи |
| `src/VvCash/App.axaml.cs` | Регистрация служб, старт сервера |
| `src/VvCash/VvCash.csproj` | `FrameworkReference`, `EmbeddedResource` |
| `build/installer/*.iss` | Правило файрвола |

---

# Фаза 1. Печать

Ценность появляется сразу и не зависит ни от сети, ни от сервера.

### Task 1: Роли принтера и их миграция

**Files:**
- Create: `src/VvCash/Models/PrintRole.cs`
- Modify: `src/VvCash/Models/PrinterConfig.cs`
- Test: `tests/VvCash.Tests/PrinterRolesSettingsTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/PrinterRolesSettingsTest.cs`:

```csharp
using System.IO;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

/// <summary>Миграция ролей печати. Настройка появляется у парка, который её
/// никогда не видел, поэтому «поля нет в файле» — основной случай, а не крайний.</summary>
public class PrinterRolesSettingsTest
{
    private static string WriteSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vv-settings-{Path.GetRandomFileName()}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void PrinterWithoutRolesInFile_PrintsReceiptsAsBefore()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100", "IsEnabled": true }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Receipt, settings.Printers[0].Roles);
    }

    [Fact]
    public void RolesAreReadAsNames_BecauseThisFileIsEditedByHand()
    {
        var path = WriteSettings("""
        {
          "Printers": [
            { "Name": "a", "ConnectionType": 2, "ConnectionString": "10.0.0.1:9100",
              "IsEnabled": true, "Roles": "Ticket, KitchenOrder" }
          ]
        }
        """);

        var settings = new SettingsService(path);

        Assert.Equal(PrintRole.Ticket | PrintRole.KitchenOrder, settings.Printers[0].Roles);
    }

    [Fact]
    public void RolesAreWrittenBackAsNames()
    {
        var path = WriteSettings("{}");
        var settings = new SettingsService(path);
        settings.Printers = new()
        {
            new PrinterConfig { Name = "a", Roles = PrintRole.Receipt | PrintRole.Ticket }
        };

        settings.Save();

        Assert.Contains("\"Receipt, Ticket\"", File.ReadAllText(path));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PrinterRolesSettingsTest"
```

Ожидается: не компилируется — `PrintRole` не существует.

- [ ] **Step 3: Реализация**

`src/VvCash/Models/PrintRole.cs`:

```csharp
using System;
using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>Какие документы печатает конкретный принтер. Набор, а не одно
/// значение: точка ставит один принтер на чеки, второй на талоны, третий на
/// кухню — но с тем же успехом сажает всё на один аппарат.
///
/// Сериализуется именами, а не числом: settings.json на точках правят руками,
/// и "Receipt, Ticket" там читается, а 3 — нет.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrintRole
{
    None = 0,
    Receipt = 1,
    Ticket = 2,
    KitchenOrder = 4
}
```

В `src/VvCash/Models/PrinterConfig.cs` дописать поле после `CodePageId`:

```csharp
    /// <summary>Значение инициализатора и есть миграция: у кассы, обновлённой с
    /// прежней версии, поля в settings.json нет, System.Text.Json оставляет
    /// Receipt, и принтер печатает ровно то, что печатал вчера. Тот же приём,
    /// что у CodePageId выше.
    ///
    /// None — законная настройка, а не недонастроенность: так гасят принтер, не
    /// снимая его с учёта. Полное выключение по-прежнему делается IsEnabled.</summary>
    public PrintRole Roles { get; set; } = PrintRole.Receipt;
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PrinterRolesSettingsTest"
```

Ожидается: 3 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/PrintRole.cs src/VvCash/Models/PrinterConfig.cs tests/VvCash.Tests/PrinterRolesSettingsTest.cs
git commit -m "feat(printing): give each printer a set of document roles"
```

- [ ] **Step 6: Снисходительное чтение роли**

Добавлено по итогам код-ревью Task 1, в плане изначально не было.

`Roles` — первое строковое поле в `settings.json`, и оно же то самое, которое
правят руками. Опечатка (`"Roles": "Bogus"`) роняет
`JsonSerializer.Deserialize<SettingsData>` исключением, а `SettingsService.Load()`
ловит всё подряд, откладывает файл как `.corrupt-<timestamp>` и сбрасывает
настройки целиком: касса теряет `BackendUrl`, токен и все принтеры разом из-за
одной буквы. Числовой `PrinterConnectionType` рядом так себя не ведёт —
непонятное значение он переживает и портит только место использования.

Поэтому `JsonStringEnumConverter` заменяется на свой конвертер в
`src/VvCash/Models/PrintRoleJsonConverter.cs`, который не бросает никогда:
непонятный токен, число вместо строки и `null` читаются как `PrintRole.Receipt`
— то же значение, что и у отсутствующего поля, то есть опечатка вырождается в
«печатает как раньше», а не в «настроек больше нет». Запись не меняется:
по-прежнему имена через запятую.

Частично верный список (`"Ticket, Bogus"`) считается опечаткой целиком и тоже
даёт `Receipt`: применить половину неверной настройки хуже, чем не применить её
вовсе — касса начнёт печатать то, чего никто не выбирал.

Тесты дописываются в тот же файл. Ключевой из них проверяет не роль, а
**соседнее поле**: что `BackendUrl` из того же JSON уцелел. Без этой проверки
тест был бы зелёным и в мире, где файл сбросило целиком. Плюс тест на явный
`"Roles": "None"` — законную конфигурацию, отличную от отсутствующего поля.

---

### Task 2: Номер очереди в чеке и отдельный талон

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs:51-108` (`BuildSaleReceipt`)
- Test: `tests/VvCash.Tests/QueueDocumentsTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueDocumentsTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Раскладка талона и бегунка. Latin1 для разбора байтов: ASCII в любой
/// однобайтовой таблице ESC/POS совпадает сам с собой, а проверяются здесь только
/// цифры и латиница.</summary>
public class QueueDocumentsTest
{
    private static string Text(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static List<CartItem> OneCoffee() => new()
    {
        new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m }
    };

    [Fact]
    public void KitchenOrderCarriesTheNumber()
    {
        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, OneCoffee(), 24m, 0m, 24m, queueNumber: "305");

        Assert.Contains("# 305", Text(bytes));
    }

    [Fact]
    public void CustomerReceiptCarriesNoNumber()
    {
        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, OneCoffee(), 24m, 0m, 24m);

        Assert.DoesNotContain("#", Text(bytes));
    }

    [Fact]
    public void TicketCarriesNumberTimeAndStore()
    {
        var bytes = EscPosPrinterService.BuildTicket(
            EscPosCodePages.Default, "305", "14:22", "Market 1");

        var text = Text(bytes);
        Assert.Contains("305", text);
        Assert.Contains("14:22", text);
        Assert.Contains("Market 1", text);
    }

    /// <summary>Сравнение с полным талоном, а не одна проверка на номер: талон без
    /// склада и времени обязан не нести пустых строк, а тест, который ищет только
    /// номер, зелен и без обоих охранных условий.</summary>
    [Fact]
    public void TicketOmitsWhatItWasNotGiven()
    {
        var bare = EscPosPrinterService.BuildTicket(EscPosCodePages.Default, "305");
        var full = EscPosPrinterService.BuildTicket(
            EscPosCodePages.Default, "305", "14:22", "Market 1");

        var text = Text(bare);
        Assert.Contains("305", text);
        Assert.DoesNotContain("14:22", text);
        Assert.DoesNotContain("Market 1", text);
        Assert.True(bare.Length < full.Length);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDocumentsTest"
```

Ожидается: не компилируется — нет параметра `queueNumber` и нет `BuildTicket`.

- [ ] **Step 3: Реализация**

В `BuildSaleReceipt` дописать параметр последним (последним — чтобы ни один из
существующих вызовов не переехал):

```csharp
    public static byte[] BuildSaleReceipt(
        EscPosCodePage codePage,
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null,
        string? documentNumber = null, string? warehouseName = null,
        string? sellerName = null, string? saleDate = null,
        string? queueNumber = null)
```

Сразу после `Write(ms, CmdDoubleSizeOff);` под заголовком «VV CASH POS»:

```csharp
        // Бегунок — это тот же чек с номером в шапке, а не отдельный документ:
        // расходиться с чеком при первой правке раскладки ему незачем. Пусто —
        // печатается клиентский чек, и номера на нём нет по решению спеки.
        if (!string.IsNullOrWhiteSpace(queueNumber))
        {
            Write(ms, CmdDoubleSizeOn);
            Write(ms, CmdBoldOn);
            WriteLine(ms, $"# {queueNumber}", codePage);
            Write(ms, CmdBoldOff);
            Write(ms, CmdDoubleSizeOff);
        }
```

Рядом с `BuildPreReceipt` добавить:

```csharp
    /// <summary>Талон клиенту: номер и ничего лишнего. Отдельный документ, а не
    /// строка на чеке — клиент отдаёт талон, получая заказ, а чек оставляет себе.
    /// Время и точка печатаются, когда переданы: талон из кассы без склада в
    /// настройках не должен нести пустую строку.</summary>
    public static byte[] BuildTicket(EscPosCodePage codePage, string number,
        string? time = null, string? warehouseName = null)
    {
        using var ms = new MemoryStream();
        WriteInit(ms, codePage);
        Write(ms, CmdAlignCenter);
        WriteLine(ms, "----------------------------", codePage);
        Write(ms, CmdDoubleSizeOn);
        Write(ms, CmdBoldOn);
        WriteLine(ms, number, codePage);
        Write(ms, CmdBoldOff);
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, "----------------------------", codePage);
        if (!string.IsNullOrWhiteSpace(warehouseName)) WriteLine(ms, warehouseName!, codePage);
        if (!string.IsNullOrWhiteSpace(time)) WriteLine(ms, time!, codePage);
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDocumentsTest"
```

Ожидается: 4 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs tests/VvCash.Tests/QueueDocumentsTest.cs
git commit -m "feat(printing): add the ticket document and the kitchen order header"
```

---

### Task 3: Аргументы чека одним объектом

Бегунок печатает те же десять аргументов, что и чек. Второй раз выписывать этот
список в интерфейсе — верный способ разойтись с чеком при первой правке.

**Files:**
- Create: `src/VvCash/Models/SaleReceiptData.cs`
- Test: `tests/VvCash.Tests/QueueDocumentsTest.cs` (дополняется)

- [ ] **Step 1: Написать падающий тест**

Дописать в `QueueDocumentsTest`:

```csharp
    [Fact]
    public void SaleReceiptDataCarriesEverythingTheReceiptPrints()
    {
        var data = new SaleReceiptData(OneCoffee(), 24m, 4m, 20m,
            DiscountName: "Happy hour", DocumentNumber: "A-7",
            WarehouseName: "Market 1", SellerName: "Ann", SaleDate: "2026-08-31 14:22");

        var bytes = EscPosPrinterService.BuildSaleReceipt(
            EscPosCodePages.Default, data.Items, data.Subtotal, data.Discount, data.Total,
            data.DiscountName, data.DocumentNumber, data.WarehouseName, data.SellerName,
            data.SaleDate, queueNumber: "305");

        var text = Text(bytes);
        Assert.Contains("# 305", text);
        Assert.Contains("A-7", text);
        Assert.Contains("Ann", text);
        Assert.Contains("Happy hour", text);
    }
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDocumentsTest"
```

Ожидается: не компилируется — `SaleReceiptData` не существует.

- [ ] **Step 3: Реализация**

`src/VvCash/Models/SaleReceiptData.cs`:

```csharp
using System.Collections.Generic;

namespace VvCash.Models;

/// <summary>Аргументы чека одним объектом. Заведён ради бегунка: он печатает тот
/// же документ, и повторять десять параметров в третий раз — способ разойтись с
/// чеком на первой же правке.
///
/// PrintReceiptAsync намеренно оставлен со своим прежним списком параметров.
/// Переписать его — значит тронуть возвраты, обмены и три вью-модели ради
/// нулевого выигрыша; новый код берёт запись, старый остаётся как есть.</summary>
public sealed record SaleReceiptData(
    IReadOnlyList<CartItem> Items,
    decimal Subtotal,
    decimal Discount,
    decimal Total,
    string? DiscountName = null,
    string? DocumentNumber = null,
    string? WarehouseName = null,
    string? SellerName = null,
    string? SaleDate = null);
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDocumentsTest"
```

Ожидается: 5 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/SaleReceiptData.cs tests/VvCash.Tests/QueueDocumentsTest.cs
git commit -m "feat(printing): carry receipt arguments as one record"
```

---

### Task 4: Печать талона и бегунка на конкретном принтере

**Files:**
- Modify: `src/VvCash/Services/Hardware/IPrinterService.cs`, `src/VvCash/Services/Hardware/EscPosPrinterService.cs`
- Test: `tests/VvCash.Tests/QueueDocumentsTest.cs` (дополняется)

- [ ] **Step 1: Написать падающий тест**

Дописать в `QueueDocumentsTest`:

```csharp
    /// <summary>ConnectionType вне диапазона enum уводит SendAsync в свою ветку
    /// default: и бросает, не тронув транспорт ни на байт. Тот же приём, что в
    /// CompositePrinterServiceTest: тесту нужно «не делает ввода-вывода».</summary>
    [Fact]
    public async Task TicketAndKitchenOrderReportFailureRatherThanThrowing()
    {
        var printer = new EscPosPrinterService(
            (PrinterConnectionType)99, "nowhere", EscPosCodePages.Default);

        Assert.False(await printer.PrintTicketAsync("305", "14:22", "Market 1"));
        Assert.False(await printer.PrintKitchenOrderAsync(
            new SaleReceiptData(OneCoffee(), 24m, 0m, 24m), "305"));
    }
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDocumentsTest"
```

Ожидается: не компилируется — методов нет.

- [ ] **Step 3: Реализация**

В `IPrinterService`:

```csharp
    /// <param name="number">Номер очереди как он печатается — строка, а не int:
    /// печатать нечего решать, форматирование уже сделано вызывающим.</param>
    /// <param name="time">Время выдачи, уже отформатированное. Пусто — строки нет.</param>
    /// <param name="warehouseName">Точка. Пусто — строки нет.</param>
    Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null);

    /// <summary>Бегунок на кухню: тот же чек, что и клиенту, плюс номер в шапке.</summary>
    Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber);
```

В `EscPosPrinterService` рядом с `PrintPreReceiptAsync`:

```csharp
    public async Task<bool> PrintTicketAsync(string number, string? time = null,
        string? warehouseName = null)
    {
        try
        {
            await SendAsync(BuildTicket(_codePage, number, time, warehouseName));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }

    public async Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
    {
        try
        {
            await SendAsync(BuildSaleReceipt(_codePage, sale.Items, sale.Subtotal, sale.Discount,
                sale.Total, sale.DiscountName, sale.DocumentNumber, sale.WarehouseName,
                sale.SellerName, sale.SaleDate, queueNumber));
            SetStatus(PrinterStatus.Ready);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
```

`CompositePrinterService` тоже реализует `IPrinterService` — он перестанет
компилироваться до Task 5. Чтобы шаг оставался зелёным, добавить в него
временные реализации, которые Task 5 заменит:

```csharp
    public Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null)
        => Task.FromResult(false);

    public Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
        => Task.FromResult(false);
```

Интерфейс реализуют ещё три заглушки в тестах — без них проект тестов не
соберётся. Дописать в каждую (`tests/VvCash.Tests/PosViewModelSellerGateTest.cs:235`
`FakePrinterService`, `tests/VvCash.Tests/ReturnsViewModelTest.cs:52` и
`tests/VvCash.Tests/ExchangeViewModelTest.cs:142` — обе `CountingPrinter`):

```csharp
        public Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null)
            => Task.FromResult(true);
        public Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
            => Task.FromResult(true);
```

`true`, а не `false`: эти заглушки изображают исправный принтер, и внезапный
отказ печати сдвинул бы чужие тесты, к очереди отношения не имеющие.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDocumentsTest"
```

Ожидается: 6 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Hardware/
git commit -m "feat(printing): print a ticket and a kitchen order from one printer"
```

---

### Task 5: Шов для проверки маршрутизации

`CompositePrinterService` создаёт принтеры сам, поэтому проверить «кто какой
документ получил» сейчас нечем. Ставим фабрику и делаем отправку переопределяемой.

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs:251` (`SendAsync`), `src/VvCash/Services/Hardware/CompositePrinterService.cs`
- Test: `tests/VvCash.Tests/PrinterRoutingTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/PrinterRoutingTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Куда какой документ уехал. Без этого набор ролей проверяется только
/// глазами на точке — а ошибка тут выглядит как «кухня молчит», и её ищут в сети.</summary>
public class PrinterRoutingTest
{
    private sealed class RecordingPrinter : EscPosPrinterService
    {
        public List<string> Sent { get; } = new();
        public bool Fails { get; set; }

        public RecordingPrinter(PrinterConfig config)
            : base(config.ConnectionType, config.ConnectionString,
                   EscPosCodePages.Resolve(config.CodePageId), config.Roles) { }

        protected override Task SendAsync(byte[] data)
        {
            if (Fails) throw new InvalidOperationException("printer is on fire");
            Sent.Add(Encoding.Latin1.GetString(data));
            return Task.CompletedTask;
        }
    }

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

    private static (CompositePrinterService Composite, List<RecordingPrinter> Printers)
        Build(params PrintRole[] roles)
    {
        var made = new List<RecordingPrinter>();
        var settings = new FakeSettings
        {
            Printers = roles.Select((r, i) => new PrinterConfig
            {
                Name = $"p{i}",
                ConnectionType = PrinterConnectionType.LAN,
                ConnectionString = $"10.0.0.{i}:9100",
                IsEnabled = true,
                Roles = r
            }).ToList()
        };

        var composite = new CompositePrinterService(settings, config =>
        {
            var p = new RecordingPrinter(config);
            made.Add(p);
            return p;
        });

        return (composite, made);
    }

    private static List<CartItem> OneCoffee() => new()
    {
        new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m }
    };

    [Fact]
    public async Task EachDocumentGoesOnlyToPrintersHoldingItsRole()
    {
        var (composite, printers) = Build(
            PrintRole.Receipt,
            PrintRole.Ticket,
            PrintRole.Receipt | PrintRole.KitchenOrder);

        await composite.PrintReceiptAsync(OneCoffee(), 24m, 0m, 24m, Array.Empty<Coupon>());
        await composite.PrintTicketAsync("305", "14:22", "Market 1");
        await composite.PrintKitchenOrderAsync(new SaleReceiptData(OneCoffee(), 24m, 0m, 24m), "305");

        Assert.Single(printers[0].Sent);
        Assert.Single(printers[1].Sent);
        Assert.Equal(2, printers[2].Sent.Count);
        Assert.Contains("305", printers[1].Sent[0]);
        Assert.Contains("# 305", printers[2].Sent[1]);
    }

    [Fact]
    public async Task ADeadKitchenPrinterDoesNotFailTheReceipt()
    {
        var (composite, printers) = Build(PrintRole.Receipt, PrintRole.KitchenOrder);
        printers[1].Fails = true;

        var receipt = await composite.PrintReceiptAsync(OneCoffee(), 24m, 0m, 24m, Array.Empty<Coupon>());
        var kitchen = await composite.PrintKitchenOrderAsync(
            new SaleReceiptData(OneCoffee(), 24m, 0m, 24m), "305");

        Assert.True(receipt);
        Assert.False(kitchen);
    }

    [Fact]
    public async Task NoPrinterHoldsTheTicketRole_ReportsFailureRatherThanThrowing()
    {
        var (composite, _) = Build(PrintRole.Receipt);

        Assert.False(await composite.PrintTicketAsync("305"));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PrinterRoutingTest"
```

Ожидается: не компилируется — у `CompositePrinterService` нет второго параметра,
у `EscPosPrinterService` нет четвёртого, `SendAsync` не переопределяем.

- [ ] **Step 3: Реализация**

В `EscPosPrinterService`: поле и параметр конструктора,

```csharp
    private readonly PrintRole _roles;

    /// <summary>Какие документы печатает этот аппарат. Значение по умолчанию —
    /// Receipt: служба, собранная на экране настроек ради пробной печати, ролями
    /// не пользуется вовсе, и заставлять её их объявлять незачем.</summary>
    public PrintRole Roles => _roles;

    public EscPosPrinterService(PrinterConnectionType connectionType, string connectionString,
        EscPosCodePage codePage, PrintRole roles = PrintRole.Receipt)
    {
        _connectionType = connectionType;
        _connectionString = connectionString;
        _codePage = codePage;
        _roles = roles;
    }
```

и отправка становится переопределяемой:

```csharp
    /// <summary>protected virtual, а не private: иначе маршрутизацию документов по
    /// ролям нельзя проверить, не открыв сокет. Боевой код это не меняет — ветки
    /// транспорта остаются здесь же, ниже.</summary>
    protected virtual async Task SendAsync(byte[] data)
```

В `CompositePrinterService`: фабрика и фильтр по роли.

```csharp
    private readonly Func<PrinterConfig, EscPosPrinterService> _factory;

    /// <summary>Фабрика существует ради проверки маршрутизации: без неё состав
    /// принтеров создаётся внутри и подменить его нечем. По умолчанию — обычное
    /// создание, боевой путь тот же, что был.</summary>
    public CompositePrinterService(ISettingsService settingsService,
        Func<PrinterConfig, EscPosPrinterService>? printerFactory = null)
    {
        _factory = printerFactory ?? (config => new EscPosPrinterService(
            config.ConnectionType, config.ConnectionString,
            EscPosCodePages.Resolve(config.CodePageId), config.Roles));
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
        InitializePrinters();
    }
```

Внутри `InitializePrinters` заменить создание на `var printer = _factory(config);`.

Добавить выборку и переписать три фанаута:

```csharp
    /// <summary>Состав под конкретный документ. Пустой список означает «на этой
    /// точке такой документ не печатают» — законная настройка, поэтому вызывающие
    /// возвращают false, а не бросают.</summary>
    private IReadOnlyList<EscPosPrinterService> For(PrintRole role)
        => _printers.Where(p => p.Roles.HasFlag(role)).ToList();
```

`PrintReceiptAsync` начинается с `var printers = For(PrintRole.Receipt);` вместо
`var printers = _printers;`. Временные заглушки из Task 4 заменяются на:

```csharp
    public async Task<bool> PrintTicketAsync(string number, string? time = null,
        string? warehouseName = null)
    {
        var printers = For(PrintRole.Ticket);
        if (printers.Count == 0) return false;
        var tasks = printers.Select(p => p.PrintTicketAsync(number, time, warehouseName)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }

    public async Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
    {
        var printers = For(PrintRole.KitchenOrder);
        if (printers.Count == 0) return false;
        var tasks = printers.Select(p => p.PrintKitchenOrderAsync(sale, queueNumber)).ToList();
        await Task.WhenAll(tasks);
        return tasks.Any(t => t.Result);
    }
```

`PrintPreReceiptAsync`, `OpenCashDrawerAsync`, `PrintReturnReceiptAsync` и
`PrintExchangeReceiptAsync` ролью не размечаются — их печатает тот же аппарат,
что и чек, и вводить для них четвёртую роль незачем. Но и на весь `_printers`
целиком они не идут:

```csharp
    /// <summary>Предчек, дёрг ящика, возврат и обмен не размечены ролью — на них
    /// работает тот же аппарат, что и на чеке, — но None обязан всё равно их
    /// глушить: это единственное, чем настройка отличается от «гасим принтер, не
    /// снимая его IsEnabled». Фильтр не For(PrintRole.Receipt) — точка, где
    /// настроен только талонный принтер и ни одного чекового, тогда осталась бы
    /// вовсе без возвратов; редкий лишний чек на кухонном аппарате — меньшая
    /// беда, чем немой возврат.</summary>
    private IReadOnlyList<EscPosPrinterService> AllButSilenced()
        => _printers.Where(p => p.Roles != PrintRole.None).ToList();
```

Правка внесена по итогам код-ревью Tasks 3–5: исходный план оставлял эти четыре
на всём списке, и тогда `Roles = None` не глушил принтер, хотя комментарий в
`PrinterConfig` ровно это обещает. Кассир ставил `None`, чтобы аппарат замолчал,
и получал на нём каждый предчек и каждый возврат; кухонный принтер печатал все
чеки возврата и обмена.

Решение закрепляется тестом — иначе кто-нибудь потом «исправит
непоследовательность», переведя эти четыре на `For(PrintRole.Receipt)`, и ничего
не упадёт.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PrinterRoutingTest"
```

Ожидается: 3 passed.

- [ ] **Step 5: Прогнать весь набор — не сломан ли прежний тест композита**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CompositePrinterServiceTest"
```

Ожидается: 4 passed.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/Services/Hardware/ tests/VvCash.Tests/PrinterRoutingTest.cs
git commit -m "feat(printing): route each document to the printers holding its role"
```

---

### Task 6: Галки ролей на экране настроек

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs:18-60` (`PrinterConfigViewModel`), `:245-256` (загрузка), `:514-525` (сохранение), `src/VvCash/Views/SettingsView.axaml`
- Test: `tests/VvCash.Tests/SettingsViewModelRolesTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/SettingsViewModelRolesTest.cs`:

```csharp
using VvCash.Models;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>Перевод трёх галок в набор флагов и обратно. Отдельным тестом,
/// потому что XAML в этом проекте связывается отражением: опечатка в пути
/// биндинга собирается молча и падает только на точке.</summary>
public class SettingsViewModelRolesTest
{
    [Fact]
    public void CheckboxesBecomeFlags()
    {
        var vm = new PrinterConfigViewModel
        {
            PrintsReceipt = true,
            PrintsTicket = false,
            PrintsKitchenOrder = true
        };

        Assert.Equal(PrintRole.Receipt | PrintRole.KitchenOrder, vm.Roles);
    }

    [Fact]
    public void FlagsBecomeCheckboxes()
    {
        var vm = new PrinterConfigViewModel { Roles = PrintRole.Ticket };

        Assert.False(vm.PrintsReceipt);
        Assert.True(vm.PrintsTicket);
        Assert.False(vm.PrintsKitchenOrder);
    }

    [Fact]
    public void NoBoxTickedIsAValidConfiguration()
    {
        var vm = new PrinterConfigViewModel { Roles = PrintRole.Receipt };

        vm.PrintsReceipt = false;

        Assert.Equal(PrintRole.None, vm.Roles);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelRolesTest"
```

Ожидается: не компилируется — свойств нет.

- [ ] **Step 3: Реализация**

В `PrinterConfigViewModel` (`SettingsViewModel.cs`):

```csharp
    /// <summary>Роли держатся набором флагов, а на экране — тремя независимыми
    /// галками: «печатает чеки и бегунки» это обычная настройка, а не исключение.
    /// Хранить их тремя bool и собирать флаги на сохранении было бы вторым
    /// источником правды — здесь один, а галки его проекции.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrintsReceipt))]
    [NotifyPropertyChangedFor(nameof(PrintsTicket))]
    [NotifyPropertyChangedFor(nameof(PrintsKitchenOrder))]
    private PrintRole _roles = PrintRole.Receipt;

    public bool PrintsReceipt
    {
        get => Roles.HasFlag(PrintRole.Receipt);
        set => Roles = value ? Roles | PrintRole.Receipt : Roles & ~PrintRole.Receipt;
    }

    public bool PrintsTicket
    {
        get => Roles.HasFlag(PrintRole.Ticket);
        set => Roles = value ? Roles | PrintRole.Ticket : Roles & ~PrintRole.Ticket;
    }

    public bool PrintsKitchenOrder
    {
        get => Roles.HasFlag(PrintRole.KitchenOrder);
        set => Roles = value ? Roles | PrintRole.KitchenOrder : Roles & ~PrintRole.KitchenOrder;
    }
```

В загрузке принтеров (`SettingsViewModel.cs:247`) добавить в инициализатор
`Roles = printer.Roles,`. В сохранении (`:514`) — `Roles = p.Roles,`.

В `SettingsView.axaml`, в шаблоне строки принтера, рядом с существующей галкой
включения:

```xml
<StackPanel Orientation="Horizontal" Spacing="16" Margin="0,8,0,0">
    <CheckBox Content="{Binding Source={x:Static services:I18nService.Instance}, Path=[PrintsReceipt]}"
              IsChecked="{Binding PrintsReceipt}"/>
    <CheckBox Content="{Binding Source={x:Static services:I18nService.Instance}, Path=[PrintsTicket]}"
              IsChecked="{Binding PrintsTicket}"/>
    <CheckBox Content="{Binding Source={x:Static services:I18nService.Instance}, Path=[PrintsKitchenOrder]}"
              IsChecked="{Binding PrintsKitchenOrder}"/>
</StackPanel>
```

Взять из соседней разметки готовый способ обращения к `I18nService` — в файле уже
есть строки с переводом, повторить их форму, а не изобретать. Добавить три ключа
(`PrintsReceipt`, `PrintsTicket`, `PrintsKitchenOrder`) во все языковые файлы,
которые перечисляет `AvailableLanguages`: `ru`, `en`, `tg`, `uz`, `kk`.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~SettingsViewModelRolesTest"
```

Ожидается: 3 passed.

- [ ] **Step 5: Проверить экран глазами**

Биндинги в этом проекте не компилируются (`AvaloniaUseCompiledBindingsByDefault`
= false), поэтому опечатка в пути видна только в работающем приложении. Собрать в
`build/verify`, чтобы не упереться в блокировку файлов, запустить и открыть
настройки:

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Ожидается: три галки видны у каждого принтера, снятие и установка сохраняются
после перезапуска.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs src/VvCash/Views/SettingsView.axaml src/VvCash/Assets tests/VvCash.Tests/SettingsViewModelRolesTest.cs
git commit -m "feat(settings): let each printer be assigned its document roles"
```

---

# Фаза 2. Номера

### Task 7: Хранилище очереди и его схема

**Files:**
- Create: `src/VvCash/Services/Queue/IQueueStorage.cs`, `src/VvCash/Services/Queue/QueueStorage.cs`
- Test: `tests/VvCash.Tests/QueueStorageTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueStorageTest.cs`:

```csharp
using System.IO;
using System.Threading.Tasks;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueStorageTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    [Fact]
    public async Task InitializeIsIdempotent()
    {
        var path = TempDb();
        var storage = new QueueStorage(path);

        await storage.InitializeAsync();
        await storage.InitializeAsync();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task StateSurvivesReopening()
    {
        var path = TempDb();
        var first = new QueueStorage(path);
        await first.InitializeAsync();
        await first.SetStateAsync("Day", "2026-08-31");

        var second = new QueueStorage(path);
        await second.InitializeAsync();

        Assert.Equal("2026-08-31", await second.GetStateAsync("Day"));
    }

    [Fact]
    public async Task MissingStateReadsAsNull()
    {
        var storage = new QueueStorage(TempDb());
        await storage.InitializeAsync();

        Assert.Null(await storage.GetStateAsync("Day"));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueStorageTest"
```

Ожидается: не компилируется — `QueueStorage` не существует.

- [ ] **Step 3: Реализация**

`src/VvCash/Services/Queue/IQueueStorage.cs`:

```csharp
using System.Threading.Tasks;

namespace VvCash.Services.Queue;

/// <summary>SQLite очереди. Отдельный файл, а не таблицы в offline_data.db:
/// схема продаж и схема очереди живут независимо, чистятся в разное время, и
/// два соединения к одному файлу дали бы «database is locked» на ровном месте.</summary>
public interface IQueueStorage
{
    Task InitializeAsync();
    Task<string?> GetStateAsync(string key);
    Task SetStateAsync(string key, string value);
}
```

`src/VvCash/Services/Queue/QueueStorage.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VvCash.Services.Queue;

public class QueueStorage : IQueueStorage
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized;

    internal string ConnectionString => _connectionString;

    /// <summary>Путь по умолчанию — рядом с offline_data.db, тем же приёмом, что
    /// в OfflineStorageService: тесты передают свой, боевой код не передаёт
    /// ничего.</summary>
    public QueueStorage(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appDataPath, "VvCash");
            Directory.CreateDirectory(appDir);
            dbPath = Path.Combine(appDir, "queue.db");
        }
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                -- IssuedSeq: на какой по счёту выдаче номер ушёл. NULL — номер свободен.
                -- ReleasedAtSeq: на какой выдаче вернулся. NULL — ни разу не возвращался.
                -- Position — место в перемешанном порядке; именно оно, а не Number,
                -- определяет очерёдность выдачи, и именно поэтому по двум талонам
                -- нельзя посчитать оборот.
                CREATE TABLE IF NOT EXISTS NumberPool (
                    Number INTEGER PRIMARY KEY,
                    Position INTEGER NOT NULL,
                    IssuedSeq INTEGER,
                    ReleasedAtSeq INTEGER
                );

                CREATE TABLE IF NOT EXISTS QueueState (
                    Key TEXT PRIMARY KEY,
                    Value TEXT
                );

                CREATE TABLE IF NOT EXISTS QueueOrders (
                    Id TEXT PRIMARY KEY,
                    Number INTEGER NOT NULL,
                    TillIndex INTEGER NOT NULL,
                    State TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    ReadyAt TEXT,
                    ClosedAt TEXT,
                    SaleDocumentNumber TEXT,
                    Lines TEXT NOT NULL
                );

                -- Исходящий буфер кассы-клиента. Тот же смысл, что у
                -- UnsyncedDocuments в offline_data.db, и живёт по тем же правилам.
                CREATE TABLE IF NOT EXISTS QueueOutbox (
                    Id TEXT PRIMARY KEY,
                    Payload TEXT NOT NULL,
                    Kind TEXT NOT NULL
                );";
            await command.ExecuteNonQueryAsync();

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string?> GetStateAsync(string key)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM QueueState WHERE Key = $k";
        command.Parameters.AddWithValue("$k", key);
        var value = await command.ExecuteScalarAsync();
        return value as string;
    }

    public async Task SetStateAsync(string key, string value)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO QueueState (Key, Value) VALUES ($k, $v) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = $v";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        await command.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueStorageTest"
```

Ожидается: 3 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/ tests/VvCash.Tests/QueueStorageTest.cs
git commit -m "feat(queue): add the queue database and its schema"
```

---

### Task 8: Выдача номеров из перемешанного пула

**Files:**
- Create: `src/VvCash/Services/Queue/INumberPool.cs`, `src/VvCash/Services/Queue/NumberPool.cs`
- Test: `tests/VvCash.Tests/NumberPoolTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/NumberPoolTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Пул номеров. Главное требование заказчика — по двум талонам нельзя
/// посчитать, сколько чеков пробито за день, поэтому «не подряд» здесь такое же
/// требование, как «без дубликатов».</summary>
public class NumberPoolTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static NumberPool Pool(int tillIndex = 0, string? db = null, Func<DateTime>? now = null)
        => new(new QueueStorage(db ?? TempDb()), tillIndex, "secret", now ?? (() => new DateTime(2026, 8, 31, 10, 0, 0)));

    [Fact]
    public async Task IssuedNumbersAreThreeDigitsAndBelongToThisTillsSlice()
    {
        var pool = Pool(tillIndex: 2);

        for (var i = 0; i < 20; i++)
        {
            var number = await pool.IssueAsync();
            Assert.InRange(number, 100, 999);
            Assert.Equal(2, number % 5);
        }
    }

    [Fact]
    public async Task TwoTillsNeverCollide()
    {
        var first = Pool(tillIndex: 0);
        var second = Pool(tillIndex: 1);

        var a = new List<int>();
        var b = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            a.Add(await first.IssueAsync());
            b.Add(await second.IssueAsync());
        }

        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public async Task NoNumberIsIssuedTwiceWhileTheSliceLasts()
    {
        var pool = Pool();

        var issued = new List<int>();
        for (var i = 0; i < 180; i++) issued.Add(await pool.IssueAsync());

        Assert.Equal(180, issued.Distinct().Count());
    }

    /// <summary>Тот самый анти-подсчёт. Порог мягкий нарочно: доказывать
    /// случайность одним прогоном нельзя, а поймать «забыли перемешать» — можно,
    /// и это ровно та ошибка, которая проходит все прочие тесты.</summary>
    [Fact]
    public async Task IssueOrderIsNotMonotonic()
    {
        var pool = Pool();

        var issued = new List<int>();
        for (var i = 0; i < 30; i++) issued.Add(await pool.IssueAsync());

        var ascendingSteps = issued.Zip(issued.Skip(1), (a, b) => b > a).Count(x => x);
        Assert.InRange(ascendingSteps, 5, 24);
    }

    [Fact]
    public async Task TheShuffleIsStableAcrossRestartsWithinADay()
    {
        var db = TempDb();
        var first = Pool(db: db);
        var a = await first.IssueAsync();
        var b = await first.IssueAsync();

        var reopened = Pool(db: db);
        var c = await reopened.IssueAsync();

        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest"
```

Ожидается: не компилируется — `NumberPool` не существует.

- [ ] **Step 3: Реализация**

`src/VvCash/Services/Queue/INumberPool.cs`:

```csharp
using System.Threading.Tasks;

namespace VvCash.Services.Queue;

public interface INumberPool
{
    /// <summary>Следующий номер для клиента. Никого не спрашивает по сети — на
    /// этом стоит вся оффлайн-устойчивость очереди.</summary>
    Task<int> IssueAsync();

    /// <summary>Возвращает номер в оборот. Раньше кулдауна он всё равно не
    /// выдастся — см. NumberPool.CooldownIssues.</summary>
    Task ReleaseAsync(int number);
}
```

`src/VvCash/Services/Queue/NumberPool.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VvCash.Services.Queue;

/// <summary>Трёхзначные номера из перемешанного пула. Кассе достаётся свой класс
/// вычетов по модулю <see cref="Tills"/>, поэтому кассы не пересекаются, ни о чём
/// не договариваются и работают без сети.
///
/// Порядок выдачи задаётся перемешиванием, а не возрастанием: клиент с двумя
/// талонами не должен уметь вычесть из них дневной оборот точки.</summary>
public class NumberPool : INumberPool
{
    /// <summary>Сколько касс делят пул. Пять — потолок парка из спеки; менять
    /// это число на работающей точке нельзя: слайсы разъедутся и два аппарата
    /// начнут выдавать один номер.</summary>
    public const int Tills = 5;
    public const int FirstNumber = 100;
    public const int LastNumber = 999;

    /// <summary>Сколько выдач должно пройти, прежде чем освобождённый номер уйдёт
    /// снова. Без отсрочки два человека одновременно держат один «305».</summary>
    public const int CooldownIssues = 50;

    private const string DayKey = "Day";
    private const string SeqKey = "IssueSeq";

    private readonly QueueStorage _storage;
    private readonly int _tillIndex;
    private readonly string _secret;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NumberPool(QueueStorage storage, int tillIndex, string secret, Func<DateTime> now)
    {
        _storage = storage;
        _tillIndex = tillIndex;
        _secret = secret;
        _now = now;
    }

    public async Task<int> IssueAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _storage.InitializeAsync();
            await EnsureTodaysPoolAsync();

            using var connection = new SqliteConnection(_storage.ConnectionString);
            await connection.OpenAsync();

            var seq = await NextSeqAsync(connection);

            // Порядок предпочтения: ни разу не выданный по перемешанному порядку;
            // затем возвращённый и отстоявший кулдаун, самый давний первым; затем,
            // если не осталось и таких, — выданный раньше всех. Последняя ветка
            // существует, чтобы касса не встала: на точке без кухонного экрана
            // никто не закрывает заказы, и номера не возвращаются вовсе.
            using var pick = connection.CreateCommand();
            pick.CommandText = @"
                SELECT Number FROM NumberPool
                WHERE IssuedSeq IS NULL AND ReleasedAtSeq IS NULL
                ORDER BY Position LIMIT 1";
            var number = await pick.ExecuteScalarAsync() as long?;

            if (number is null)
            {
                using var reused = connection.CreateCommand();
                reused.CommandText = @"
                    SELECT Number FROM NumberPool
                    WHERE IssuedSeq IS NULL AND ReleasedAtSeq IS NOT NULL
                      AND $seq - ReleasedAtSeq >= $cooldown
                    ORDER BY ReleasedAtSeq LIMIT 1";
                reused.Parameters.AddWithValue("$seq", seq);
                reused.Parameters.AddWithValue("$cooldown", CooldownIssues);
                number = await reused.ExecuteScalarAsync() as long?;
            }

            if (number is null)
            {
                using var oldest = connection.CreateCommand();
                oldest.CommandText = @"
                    SELECT Number FROM NumberPool
                    ORDER BY COALESCE(IssuedSeq, ReleasedAtSeq, 0) LIMIT 1";
                number = await oldest.ExecuteScalarAsync() as long?;
            }

            using var take = connection.CreateCommand();
            take.CommandText =
                "UPDATE NumberPool SET IssuedSeq = $seq, ReleasedAtSeq = NULL WHERE Number = $n";
            take.Parameters.AddWithValue("$seq", seq);
            take.Parameters.AddWithValue("$n", number!.Value);
            await take.ExecuteNonQueryAsync();

            return (int)number.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(int number)
    {
        await _gate.WaitAsync();
        try
        {
            await _storage.InitializeAsync();
            using var connection = new SqliteConnection(_storage.ConnectionString);
            await connection.OpenAsync();

            var seq = await ReadSeqAsync(connection);

            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE NumberPool SET IssuedSeq = NULL, ReleasedAtSeq = $seq WHERE Number = $n";
            command.Parameters.AddWithValue("$seq", seq);
            command.Parameters.AddWithValue("$n", number);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Первая выдача нового дня перемешивает пул заново и обнуляет
    /// счётчик. День берётся местный, а не UTC: граница суток — свойство точки,
    /// а не часового пояса сервера.</summary>
    private async Task EnsureTodaysPoolAsync()
    {
        var today = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (await _storage.GetStateAsync(DayKey) == today) return;

        using var connection = new SqliteConnection(_storage.ConnectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM NumberPool";
            await clear.ExecuteNonQueryAsync();
        }

        var position = 0;
        foreach (var number in Shuffled(today))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO NumberPool (Number, Position) VALUES ($n, $p)";
            insert.Parameters.AddWithValue("$n", number);
            insert.Parameters.AddWithValue("$p", position++);
            await insert.ExecuteNonQueryAsync();
        }

        transaction.Commit();

        await _storage.SetStateAsync(SeqKey, "0");
        await _storage.SetStateAsync(DayKey, today);
    }

    /// <summary>Fisher–Yates с сидом от даты, номера кассы и секрета точки.
    /// Детерминированность нужна не ради воспроизводимости, а чтобы сид нельзя
    /// было угадать, зная только дату.</summary>
    private List<int> Shuffled(string day)
    {
        var numbers = Enumerable
            .Range(FirstNumber, LastNumber - FirstNumber + 1)
            .Where(n => n % Tills == _tillIndex)
            .ToList();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{day}|{_tillIndex}|{_secret}"));
        var random = new Random(BitConverter.ToInt32(hash, 0));

        for (var i = numbers.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (numbers[i], numbers[j]) = (numbers[j], numbers[i]);
        }

        return numbers;
    }

    private async Task<long> ReadSeqAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM QueueState WHERE Key = $k";
        command.Parameters.AddWithValue("$k", SeqKey);
        var value = await command.ExecuteScalarAsync() as string;
        return long.TryParse(value, out var seq) ? seq : 0;
    }

    private async Task<long> NextSeqAsync(SqliteConnection connection)
    {
        var seq = await ReadSeqAsync(connection) + 1;
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO QueueState (Key, Value) VALUES ($k, $v) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = $v";
        command.Parameters.AddWithValue("$k", SeqKey);
        command.Parameters.AddWithValue("$v", seq.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
        return seq;
    }
}
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest"
```

Ожидается: 5 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/ tests/VvCash.Tests/NumberPoolTest.cs
git commit -m "feat(queue): issue unpredictable three-digit numbers from a per-till slice"
```

---

### Task 9: Возврат номера, кулдаун и смена дня

**Files:**
- Test: `tests/VvCash.Tests/NumberPoolTest.cs` (дополняется)
- Modify: `src/VvCash/Services/Queue/NumberPool.cs` — только если тесты покажут расхождение

- [ ] **Step 1: Написать падающий тест**

Дописать в `NumberPoolTest`:

```csharp
    [Fact]
    public async Task AReleasedNumberDoesNotComeBackImmediately()
    {
        var pool = Pool();
        var first = await pool.IssueAsync();
        await pool.ReleaseAsync(first);

        var next = new List<int>();
        for (var i = 0; i < NumberPool.CooldownIssues; i++) next.Add(await pool.IssueAsync());

        Assert.DoesNotContain(first, next);
    }

    [Fact]
    public async Task AReleasedNumberComesBackAfterTheCooldown()
    {
        var pool = Pool();

        // Слайс — 180 номеров, кулдаун — 50. Выдав все и вернув первые
        // пятьдесят, доводим пул до состояния, где свежих номеров нет, а
        // отстоявшие есть.
        var issued = new List<int>();
        for (var i = 0; i < 180; i++) issued.Add(await pool.IssueAsync());
        foreach (var number in issued.Take(NumberPool.CooldownIssues))
            await pool.ReleaseAsync(number);

        var next = await pool.IssueAsync();

        Assert.Contains(next, issued.Take(NumberPool.CooldownIssues));
    }

    [Fact]
    public async Task AnExhaustedSliceReusesTheOldestRatherThanStalling()
    {
        var pool = Pool();

        for (var i = 0; i < 180; i++) await pool.IssueAsync();
        var afterExhaustion = await pool.IssueAsync();

        Assert.InRange(afterExhaustion, 100, 999);
        Assert.Equal(0, afterExhaustion % 5);
    }

    [Fact]
    public async Task ANewDayReshufflesAndStartsOver()
    {
        var db = TempDb();
        var day = new DateTime(2026, 8, 31, 10, 0, 0);
        var pool = Pool(db: db, now: () => day);

        var yesterday = new List<int>();
        for (var i = 0; i < 20; i++) yesterday.Add(await pool.IssueAsync());

        day = day.AddDays(1);
        var today = new List<int>();
        for (var i = 0; i < 20; i++) today.Add(await pool.IssueAsync());

        Assert.NotEqual(yesterday, today);
    }
```

Обратить внимание: `day` захвачена лямбдой по ссылке, поэтому присваивание
`day = day.AddDays(1)` действительно двигает часы пула.

- [ ] **Step 2: Прогнать**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~NumberPoolTest"
```

Ожидается: 9 passed. Реализация из Task 8 уже покрывает эти правила — тест
существует, чтобы они не разъехались при следующей правке SQL.

- [ ] **Step 3: Если какой-то из четырёх упал — чинить `NumberPool`, а не тест**

Правила — из спеки, раздел «Номера». Тест переписывать только если он
противоречит спеке.

- [ ] **Step 4: Коммит**

```bash
git add tests/VvCash.Tests/NumberPoolTest.cs
git commit -m "test(queue): pin the cooldown, exhaustion and day-change rules"
```

---

# Фаза 3. Заказ и касса-клиент

### Task 10: Модель заказа и допустимые переходы

**Files:**
- Create: `src/VvCash/Models/QueueOrder.cs`, `src/VvCash/Models/QueueOrderState.cs`
- Test: `tests/VvCash.Tests/QueueOrderStateTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueOrderStateTest.cs`:

```csharp
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class QueueOrderStateTest
{
    [Theory]
    [InlineData(QueueOrderState.New, QueueOrderState.InProgress)]
    [InlineData(QueueOrderState.InProgress, QueueOrderState.Ready)]
    [InlineData(QueueOrderState.Ready, QueueOrderState.Closed)]
    [InlineData(QueueOrderState.New, QueueOrderState.Cancelled)]
    [InlineData(QueueOrderState.InProgress, QueueOrderState.Cancelled)]
    [InlineData(QueueOrderState.Ready, QueueOrderState.Cancelled)]
    public void AllowedTransitions(QueueOrderState from, QueueOrderState to)
        => Assert.True(QueueOrderStates.CanMove(from, to));

    [Theory]
    [InlineData(QueueOrderState.Closed, QueueOrderState.Ready)]
    [InlineData(QueueOrderState.Cancelled, QueueOrderState.New)]
    [InlineData(QueueOrderState.New, QueueOrderState.Closed)]
    [InlineData(QueueOrderState.Ready, QueueOrderState.New)]
    [InlineData(QueueOrderState.Closed, QueueOrderState.Closed)]
    public void RejectedTransitions(QueueOrderState from, QueueOrderState to)
        => Assert.False(QueueOrderStates.CanMove(from, to));
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueOrderStateTest"
```

Ожидается: не компилируется.

- [ ] **Step 3: Реализация**

`src/VvCash/Models/QueueOrderState.cs`:

```csharp
namespace VvCash.Models;

public enum QueueOrderState
{
    New,
    InProgress,
    Ready,
    Closed,
    Cancelled
}

/// <summary>Допустимые переходы. Отдельно от модели и без исключений внутри:
/// решение «можно ли» принимает сервер по приходящему запросу, а не заказ
/// сам о себе.</summary>
public static class QueueOrderStates
{
    /// <summary>Вперёд по цепочке — по одному шагу; отмена — с любого рабочего
    /// состояния. Закрытый и отменённый — конечные: KDS с задержкой на сети не
    /// должен уметь «оживить» выданный заказ повторным нажатием.</summary>
    public static bool CanMove(QueueOrderState from, QueueOrderState to) => (from, to) switch
    {
        (QueueOrderState.New, QueueOrderState.InProgress) => true,
        (QueueOrderState.InProgress, QueueOrderState.Ready) => true,
        (QueueOrderState.Ready, QueueOrderState.Closed) => true,
        (QueueOrderState.New or QueueOrderState.InProgress or QueueOrderState.Ready,
            QueueOrderState.Cancelled) => true,
        _ => false
    };
}
```

`src/VvCash/Models/QueueOrder.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace VvCash.Models;

/// <summary>Заказ очереди. Id — GUID заказа, выданный кассой, которая его пробила: по нему сервер
/// узнаёт повтор при досыле буфера, поэтому он и есть ключ идемпотентности.
///
/// SaleDocumentNumber пуст у продажи, пробитой без интернета: номер документа
/// придёт с бэкенда позже, и ни печать, ни экраны от него не зависят.</summary>
public class QueueOrder
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public int TillIndex { get; set; }
    public QueueOrderState State { get; set; } = QueueOrderState.New;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadyAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string SaleDocumentNumber { get; set; } = string.Empty;
    public List<QueueOrderLine> Lines { get; set; } = new();
}

public class QueueOrderLine
{
    public string Name { get; set; } = string.Empty;
    public string Quantity { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueOrderStateTest"
```

Ожидается: 11 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/QueueOrder.cs src/VvCash/Models/QueueOrderState.cs tests/VvCash.Tests/QueueOrderStateTest.cs
git commit -m "feat(queue): model the order and its allowed transitions"
```

---

### Task 11: Настройки очереди

Отдельным интерфейсом, а не пятью полями в `ISettingsService`: этот интерфейс
реализуют полтора десятка тестовых заглушек, и каждое новое свойство ломает их все.

**Files:**
- Create: `src/VvCash/Services/Queue/IQueueSettings.cs`
- Modify: `src/VvCash/Services/SettingsService.cs`
- Test: `tests/VvCash.Tests/QueueSettingsTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueSettingsTest.cs`:

```csharp
using System.IO;
using VvCash.Services;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueSettingsTest
{
    private static string WriteSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vv-settings-{Path.GetRandomFileName()}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AnUntouchedRegisterHasTheQueueSwitchedOff()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("{}"));

        Assert.Equal(QueueRole.Off, settings.QueueRole);
        Assert.Equal(8770, settings.QueuePort);
        Assert.Equal(0, settings.TillIndex);
    }

    [Fact]
    public void PortZeroOrNegativeReadsAsTheDefault()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("""{ "QueuePort": 0 }"""));

        Assert.Equal(8770, settings.QueuePort);
    }

    [Fact]
    public void TillIndexIsClampedIntoTheSlice()
    {
        IQueueSettings tooBig = new SettingsService(WriteSettings("""{ "TillIndex": 9 }"""));
        IQueueSettings negative = new SettingsService(WriteSettings("""{ "TillIndex": -3 }"""));

        Assert.Equal(4, tooBig.TillIndex);
        Assert.Equal(0, negative.TillIndex);
    }

    [Fact]
    public void RoleIsReadAsAName()
    {
        IQueueSettings settings = new SettingsService(WriteSettings("""{ "QueueRole": "Server" }"""));

        Assert.Equal(QueueRole.Server, settings.QueueRole);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueSettingsTest"
```

Ожидается: не компилируется.

- [ ] **Step 3: Реализация**

`src/VvCash/Services/Queue/IQueueSettings.cs`:

```csharp
using System.Text.Json.Serialization;

namespace VvCash.Services.Queue;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueRole
{
    /// <summary>Очереди как сетевой системы на этой кассе нет. Печать талона и
    /// бегунка при этом работает — документы и сервер независимы.</summary>
    Off,
    Server,
    Client
}

/// <summary>Настройки очереди. Свой интерфейс, а не пять полей в
/// ISettingsService: тот реализуют полтора десятка тестовых заглушек, и каждое
/// новое свойство ломает их все, ничего не давая взамен.</summary>
public interface IQueueSettings
{
    QueueRole QueueRole { get; set; }

    /// <summary>Адрес кассы-сервера у клиента: «10.0.0.5:8770». Пусто у сервера
    /// и у выключенной очереди.</summary>
    string QueueServerAddress { get; set; }

    int QueuePort { get; set; }

    /// <summary>Общий секрет точки. Отсекает случайный планшет в гостевом
    /// Wi-Fi; криптографией не является и защитой от своих не считается.</summary>
    string QueueSecret { get; set; }

    /// <summary>Номер кассы 0..4 — он же её класс вычетов в пуле. Две кассы с
    /// одинаковым индексом начнут выдавать одинаковые номера, поэтому значение
    /// зажимается в диапазон, а не принимается как есть.</summary>
    int TillIndex { get; set; }
}
```

В `SettingsData` дописать:

```csharp
    public QueueRole QueueRole { get; set; } = QueueRole.Off;
    public string QueueServerAddress { get; set; } = string.Empty;
    public int QueuePort { get; set; } = 8770;
    public string QueueSecret { get; set; } = string.Empty;
    public int TillIndex { get; set; }
```

В объявление класса: `public class SettingsService : ISettingsService, IQueueSettings`.
Свойства — тем же приёмом, что `SyncIntervalMinutes` и `CustomerDisplayBaudRate`:

```csharp
    public QueueRole QueueRole
    {
        get => _data.QueueRole;
        set => _data.QueueRole = value;
    }

    public string QueueServerAddress
    {
        get => _data.QueueServerAddress;
        set => _data.QueueServerAddress = value ?? string.Empty;
    }

    /// <summary>Ноль и отрицательное читаются как 8770 — тем же приёмом, что
    /// SyncIntervalMinutes выше: settings.json правят руками.</summary>
    public int QueuePort
    {
        get => _data.QueuePort <= 0 ? 8770 : _data.QueuePort;
        set => _data.QueuePort = value;
    }

    public string QueueSecret
    {
        get => _data.QueueSecret;
        set => _data.QueueSecret = value ?? string.Empty;
    }

    public int TillIndex
    {
        get => Math.Clamp(_data.TillIndex, 0, NumberPool.Tills - 1);
        set => _data.TillIndex = value;
    }
```

Дописать `using VvCash.Services.Queue;` в шапку файла.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueSettingsTest"
```

Ожидается: 5 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/IQueueSettings.cs src/VvCash/Services/SettingsService.cs tests/VvCash.Tests/QueueSettingsTest.cs
git commit -m "feat(queue): add queue settings as their own interface"
```

---

### Task 12: Постановка заказа с буфером

**Files:**
- Create: `src/VvCash/Services/Queue/IQueueTransport.cs`, `src/VvCash/Services/Queue/IQueueClient.cs`, `src/VvCash/Services/Queue/QueueClient.cs`
- Modify: `src/VvCash/Services/Queue/IQueueStorage.cs`, `QueueStorage.cs` (буфер и заказы)
- Test: `tests/VvCash.Tests/QueueClientTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueClientTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Fail-open: сервер лежит — номер всё равно выдан, бумага всё равно
/// вышла, заказ лёг в буфер. Продажа не встаёт никогда, это решение спеки.</summary>
public class QueueClientTest
{
    private sealed class FakeTransport : IQueueTransport
    {
        public bool Reachable { get; set; } = true;
        public List<QueueOrder> Posted { get; } = new();

        public Task<bool> PostOrderAsync(QueueOrder order)
        {
            if (!Reachable) return Task.FromResult(false);
            // Идемпотентность живёт на сервере; здесь просто копим всё, что дошло,
            // чтобы тест увидел дубль, если клиент пошлёт его дважды.
            Posted.Add(order);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex)
            => Task.FromResult<IReadOnlyList<QueueOrder>>(Array.Empty<QueueOrder>());
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static (QueueClient Client, FakeTransport Transport) Build(string? db = null)
    {
        var storage = new QueueStorage(db ?? TempDb());
        var pool = new NumberPool(storage, 0, "secret", () => new DateTime(2026, 8, 31, 10, 0, 0));
        var transport = new FakeTransport();
        return (new QueueClient(storage, pool, transport, tillIndex: 0,
            () => new DateTime(2026, 8, 31, 10, 0, 0)), transport);
    }

    private static SaleReceiptData Sale() => new(
        new List<CartItem> { new() { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m } },
        24m, 0m, 24m);

    [Fact]
    public async Task AnOrderGetsANumberAndReachesTheServer()
    {
        var (client, transport) = Build();

        var order = await client.EnqueueAsync(Sale());

        Assert.InRange(order.Number, 100, 999);
        Assert.Single(transport.Posted);
        Assert.Equal(order.Id, transport.Posted[0].Id);
    }

    [Fact]
    public async Task TheServerBeingDownStillYieldsANumber()
    {
        var (client, transport) = Build();
        transport.Reachable = false;

        var order = await client.EnqueueAsync(Sale());

        Assert.InRange(order.Number, 100, 999);
        Assert.Empty(transport.Posted);
    }

    [Fact]
    public async Task WhatCouldNotBeSentIsSentWhenTheServerReturns()
    {
        var (client, transport) = Build();
        transport.Reachable = false;
        var first = await client.EnqueueAsync(Sale());
        var second = await client.EnqueueAsync(Sale());

        transport.Reachable = true;
        await client.FlushAsync();

        Assert.Equal(2, transport.Posted.Count);
        Assert.Contains(transport.Posted, o => o.Id == first.Id);
        Assert.Contains(transport.Posted, o => o.Id == second.Id);
    }

    [Fact]
    public async Task FlushingTwiceDoesNotSendTheSameOrderTwice()
    {
        var (client, transport) = Build();
        transport.Reachable = false;
        await client.EnqueueAsync(Sale());

        transport.Reachable = true;
        await client.FlushAsync();
        await client.FlushAsync();

        Assert.Single(transport.Posted);
    }

    [Fact]
    public async Task TheBufferSurvivesARestart()
    {
        var db = TempDb();
        var (client, transport) = Build(db);
        transport.Reachable = false;
        var order = await client.EnqueueAsync(Sale());

        var (reopened, secondTransport) = Build(db);
        await reopened.FlushAsync();

        Assert.Single(secondTransport.Posted);
        Assert.Equal(order.Id, secondTransport.Posted[0].Id);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueClientTest"
```

Ожидается: не компилируется.

- [ ] **Step 3: Реализация**

`src/VvCash/Services/Queue/IQueueTransport.cs`:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Разговор кассы-клиента с кассой-сервером. Отдельным интерфейсом,
/// чтобы поведение при недоступном сервере проверялось без сокета: отказ
/// соединения к закрытому порту loopback на этой машине занимает ~2.2 с и
/// превращает такие тесты в минутные.</summary>
public interface IQueueTransport
{
    /// <summary>false — сервер недоступен. Не исключение: недоступный сервер это
    /// штатное состояние, а не ошибка.</summary>
    Task<bool> PostOrderAsync(QueueOrder order);

    /// <summary>Закрытые заказы этой кассы — чтобы вернуть их номера в пул.</summary>
    Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex);
}
```

`src/VvCash/Services/Queue/IQueueClient.cs`:

```csharp
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

public interface IQueueClient
{
    /// <summary>Выдаёт номер, пишет заказ локально и пробует отправить. Отказ
    /// отправки не отменяет ни номер, ни заказ — на этом стоит fail-open.</summary>
    Task<QueueOrder> EnqueueAsync(SaleReceiptData sale);

    /// <summary>Досылает буфер и возвращает в пул номера закрытых заказов.</summary>
    Task FlushAsync();
}
```

`src/VvCash/Services/Queue/QueueClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

public class QueueClient : IQueueClient
{
    private const string OrderKind = "Order";

    private readonly QueueStorage _storage;
    private readonly INumberPool _pool;
    private readonly IQueueTransport _transport;
    private readonly int _tillIndex;
    private readonly Func<DateTime> _now;

    public QueueClient(QueueStorage storage, INumberPool pool, IQueueTransport transport,
        int tillIndex, Func<DateTime> now)
    {
        _storage = storage;
        _pool = pool;
        _transport = transport;
        _tillIndex = tillIndex;
        _now = now;
    }

    public async Task<QueueOrder> EnqueueAsync(SaleReceiptData sale)
    {
        var order = new QueueOrder
        {
            Id = Guid.NewGuid(),
            Number = await _pool.IssueAsync(),
            TillIndex = _tillIndex,
            CreatedAt = _now(),
            SaleDocumentNumber = sale.DocumentNumber ?? string.Empty,
            Lines = sale.Items.Select(i => new QueueOrderLine
            {
                Name = i.Product.Name,
                Quantity = i.QuantityDisplay
            }).ToList()
        };

        await _storage.SaveOutboxAsync(order.Id, OrderKind, JsonSerializer.Serialize(order));

        // Отправка после записи в буфер, а не вместо неё: упасть между «отправил»
        // и «записал» — значит потерять заказ, а лишний досыл сервер отсеет сам
        // по GUID.
        if (await _transport.PostOrderAsync(order))
            await _storage.DeleteOutboxAsync(order.Id);

        return order;
    }

    public async Task FlushAsync()
    {
        foreach (var (id, payload) in await _storage.GetOutboxAsync(OrderKind))
        {
            var order = JsonSerializer.Deserialize<QueueOrder>(payload);
            if (order is null)
            {
                // Нечитаемая запись не должна держать очередь вечно: она уже не
                // станет читаемой, а всё, что за ней, ждать не обязано.
                await _storage.DeleteOutboxAsync(id);
                continue;
            }

            if (await _transport.PostOrderAsync(order))
                await _storage.DeleteOutboxAsync(id);
            else
                break; // Сервер недоступен — остальные тоже не уйдут, экономим попытки.
        }

        // Только свои закрытые: чужой номер лежит в чужом пуле, и вернуть его
        // отсюда значило бы освободить у себя номер, который никто не занимал.
        foreach (var closed in await _transport.GetClosedAsync(_tillIndex))
            await _pool.ReleaseAsync(closed.Number);
    }
}
```

В `IQueueStorage` и `QueueStorage` дописать работу с буфером:

```csharp
    Task SaveOutboxAsync(Guid id, string kind, string payload);
    Task<IReadOnlyList<(Guid Id, string Payload)>> GetOutboxAsync(string kind);
    Task DeleteOutboxAsync(Guid id);
```

```csharp
    public async Task SaveOutboxAsync(Guid id, string kind, string payload)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO QueueOutbox (Id, Kind, Payload) VALUES ($id, $kind, $payload) " +
            "ON CONFLICT(Id) DO UPDATE SET Payload = $payload";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<(Guid Id, string Payload)>> GetOutboxAsync(string kind)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Payload FROM QueueOutbox WHERE Kind = $kind ORDER BY rowid";
        command.Parameters.AddWithValue("$kind", kind);

        var result = new List<(Guid, string)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return result;
    }

    public async Task DeleteOutboxAsync(Guid id)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM QueueOutbox WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueClientTest"
```

Ожидается: 5 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/ tests/VvCash.Tests/QueueClientTest.cs
git commit -m "feat(queue): enqueue orders locally and buffer what the server missed"
```

---

# Фаза 4. Сервер

### Task 13: Kestrel, который не роняет кассу

**Files:**
- Modify: `src/VvCash/VvCash.csproj`
- Create: `src/VvCash/Services/Queue/QueueServer.cs`
- Test: `tests/VvCash.Tests/QueueServerTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueServerTest.cs`:

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueServerTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private HttpClient _client = null!;

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    public async Task InitializeAsync()
    {
        // Порт 0 — операционная система выдаёт свободный. Фиксированный 8770 в
        // тестах ловил бы чужой запущенный сервер и падал через раз.
        _server = new QueueServer(new QueueStorage(TempDb()), port: 0, secret: "secret");
        var port = await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _client.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    [Fact]
    public async Task AFreshServerHasNoOrders()
    {
        var response = await _client.GetStringAsync("orders");

        Assert.Equal("[]", response.Trim());
    }

    [Fact]
    public async Task AWrongSecretIsRefused()
    {
        using var stranger = new HttpClient { BaseAddress = _client.BaseAddress };

        var response = await stranger.GetAsync("orders");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnOccupiedPortIsReportedRatherThanThrown()
    {
        var occupied = new QueueServer(new QueueStorage(TempDb()),
            port: _client.BaseAddress!.Port, secret: "secret");

        var port = await occupied.StartAsync();

        Assert.Equal(-1, port);
        Assert.False(string.IsNullOrEmpty(occupied.LastError));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest"
```

Ожидается: не компилируется.

- [ ] **Step 3: Реализация**

В `VvCash.csproj`, в `ItemGroup` с пакетами:

```xml
    <!-- Kestrel для локального сервера очереди. Publish self-contained, значит
         рантайм ASP.NET Core уезжает внутрь сборки: около +12 МБ, автообновление
         донесёт его вместе с остальным. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
```

`src/VvCash/Services/Queue/QueueServer.cs`:

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VvCash.Services.Queue;

/// <summary>Локальный сервер очереди на кассе, назначенной сервером. Держит
/// заказы точки и отдаёт экраны.
///
/// Ни один отказ старта не должен ронять кассу: занятый порт — это неверно
/// заполненная настройка, а не причина не продавать. StartAsync возвращает -1 и
/// кладёт причину в LastError, экран настроек её показывает.</summary>
public class QueueServer
{
    private readonly QueueStorage _storage;
    private readonly int _port;
    private readonly string _secret;
    private WebApplication? _app;

    public string? LastError { get; private set; }

    public QueueServer(QueueStorage storage, int port, string secret)
    {
        _storage = storage;
        _port = port;
        _secret = secret;
    }

    /// <summary>Фактический порт, или -1, если поднять не удалось. Порт 0 просит
    /// свободный у системы — так работают тесты.</summary>
    public async Task<int> StartAsync()
    {
        try
        {
            await _storage.InitializeAsync();

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.ListenAnyIP(_port, listen => listen.Protocols = HttpProtocols.Http1));

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                // Страницы отдаются без секрета в заголовке — браузер его не
                // поставит; они предъявляют его параметром запроса, см. Task 19.
                if (context.Request.Headers["X-Queue-Secret"] != _secret &&
                    context.Request.Query["secret"] != _secret)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            });

            app.MapGet("/orders", async () => await _storage.GetOrdersAsync());

            await app.StartAsync();
            _app = app;

            var address = app.Urls.First();
            return new Uri(address).Port;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return -1;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
```

В `IQueueStorage`/`QueueStorage` дописать чтение заказов:

```csharp
    Task<IReadOnlyList<QueueOrder>> GetOrdersAsync();
```

```csharp
    public async Task<IReadOnlyList<QueueOrder>> GetOrdersAsync()
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, " +
            "SaleDocumentNumber, Lines FROM QueueOrders ORDER BY CreatedAt";

        var orders = new List<QueueOrder>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            orders.Add(new QueueOrder
            {
                Id = Guid.Parse(reader.GetString(0)),
                Number = reader.GetInt32(1),
                TillIndex = reader.GetInt32(2),
                State = Enum.Parse<QueueOrderState>(reader.GetString(3)),
                CreatedAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                ReadyAt = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                ClosedAt = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                SaleDocumentNumber = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                Lines = JsonSerializer.Deserialize<List<QueueOrderLine>>(reader.GetString(8)) ?? new()
            });
        }
        return orders;
    }
```

Дописать в шапку `QueueStorage.cs`: `using System.Collections.Generic;`,
`using System.Globalization;`, `using System.Text.Json;`, `using VvCash.Models;`.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest"
```

Ожидается: 3 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/VvCash.csproj src/VvCash/Services/Queue/ tests/VvCash.Tests/QueueServerTest.cs
git commit -m "feat(queue): host the queue server on the register without risking the till"
```

---

### Task 14: Приём заказов и идемпотентность

**Files:**
- Modify: `src/VvCash/Services/Queue/QueueServer.cs`, `QueueStorage.cs`, `IQueueStorage.cs`
- Test: `tests/VvCash.Tests/QueueServerTest.cs` (дополняется)

- [ ] **Step 1: Написать падающий тест**

Дописать в `QueueServerTest`:

```csharp
    private static QueueOrder Order(Guid id, int number) => new()
    {
        Id = id,
        Number = number,
        TillIndex = 1,
        CreatedAt = new DateTime(2026, 8, 31, 14, 22, 0),
        Lines = new() { new QueueOrderLine { Name = "Coffee", Quantity = "2" } }
    };

    [Fact]
    public async Task APostedOrderComesBackInTheList()
    {
        var id = Guid.NewGuid();

        await _client.PostAsJsonAsync("orders", Order(id, 305));
        var orders = await _client.GetFromJsonAsync<List<QueueOrder>>("orders");

        Assert.Single(orders!);
        Assert.Equal(305, orders![0].Number);
        Assert.Equal(QueueOrderState.New, orders[0].State);
    }

    /// <summary>Досыл буфера повторяет то, что уже дошло. Второй заказ здесь
    /// означал бы два бегунка на кухне за одну продажу.</summary>
    [Fact]
    public async Task PostingTheSameOrderTwiceCreatesOne()
    {
        var id = Guid.NewGuid();

        await _client.PostAsJsonAsync("orders", Order(id, 305));
        await _client.PostAsJsonAsync("orders", Order(id, 305));
        var orders = await _client.GetFromJsonAsync<List<QueueOrder>>("orders");

        Assert.Single(orders!);
    }
```

Дописать в шапку файла `using System.Collections.Generic;`,
`using System.Net.Http.Json;`, `using VvCash.Models;`.

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest"
```

Ожидается: `APostedOrderComesBackInTheList` падает — маршрута `POST /orders` нет
(404).

- [ ] **Step 3: Реализация**

В `QueueServer.StartAsync` после `app.MapGet("/orders", ...)`:

```csharp
        app.MapPost("/orders", async (QueueOrder order) =>
        {
            await _storage.SaveOrderAsync(order);
            return Results.Accepted();
        });
```

В `IQueueStorage`/`QueueStorage`:

```csharp
    Task SaveOrderAsync(QueueOrder order);
```

```csharp
    /// <summary>Повторная запись того же Id ничего не меняет — на этом стоит
    /// идемпотентность досыла: касса шлёт буфер снова, не зная, что дошло.
    /// DO NOTHING, а не UPDATE: заказ мог уже уехать по состояниям, и приход
    /// старой копии не должен возвращать его в New.</summary>
    public async Task SaveOrderAsync(QueueOrder order)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO QueueOrders
                (Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt,
                 SaleDocumentNumber, Lines)
            VALUES ($id, $number, $till, $state, $created, $ready, $closed, $doc, $lines)
            ON CONFLICT(Id) DO NOTHING";
        command.Parameters.AddWithValue("$id", order.Id.ToString());
        command.Parameters.AddWithValue("$number", order.Number);
        command.Parameters.AddWithValue("$till", order.TillIndex);
        command.Parameters.AddWithValue("$state", order.State.ToString());
        command.Parameters.AddWithValue("$created", order.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ready", (object?)order.ReadyAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$closed", (object?)order.ClosedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$doc", order.SaleDocumentNumber);
        command.Parameters.AddWithValue("$lines", JsonSerializer.Serialize(order.Lines));
        await command.ExecuteNonQueryAsync();
    }
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest"
```

Ожидается: 5 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/ tests/VvCash.Tests/QueueServerTest.cs
git commit -m "feat(queue): accept orders idempotently by their id"
```

---

### Task 15: Переходы состояний по сети

**Files:**
- Modify: `src/VvCash/Services/Queue/QueueServer.cs`, `QueueStorage.cs`, `IQueueStorage.cs`
- Test: `tests/VvCash.Tests/QueueServerTest.cs` (дополняется)

- [ ] **Step 1: Написать падающий тест**

Дописать в `QueueServerTest`:

```csharp
    [Fact]
    public async Task TheKitchenMovesAnOrderForward()
    {
        var id = Guid.NewGuid();
        await _client.PostAsJsonAsync("orders", Order(id, 305));

        var response = await _client.PostAsJsonAsync($"orders/{id}/state", QueueOrderState.InProgress);
        var orders = await _client.GetFromJsonAsync<List<QueueOrder>>("orders");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(QueueOrderState.InProgress, orders![0].State);
    }

    /// <summary>KDS с задержкой на сети не должен уметь оживить выданный заказ
    /// повторным нажатием.</summary>
    [Fact]
    public async Task AForbiddenTransitionIsRefusedAndChangesNothing()
    {
        var id = Guid.NewGuid();
        await _client.PostAsJsonAsync("orders", Order(id, 305));

        var response = await _client.PostAsJsonAsync($"orders/{id}/state", QueueOrderState.Closed);
        var orders = await _client.GetFromJsonAsync<List<QueueOrder>>("orders");

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(QueueOrderState.New, orders![0].State);
    }

    [Fact]
    public async Task AStateChangeForAnUnknownOrderIsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            $"orders/{Guid.NewGuid()}/state", QueueOrderState.InProgress);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadyAndClosedAreStamped()
    {
        var id = Guid.NewGuid();
        await _client.PostAsJsonAsync("orders", Order(id, 305));

        await _client.PostAsJsonAsync($"orders/{id}/state", QueueOrderState.InProgress);
        await _client.PostAsJsonAsync($"orders/{id}/state", QueueOrderState.Ready);
        await _client.PostAsJsonAsync($"orders/{id}/state", QueueOrderState.Closed);
        var orders = await _client.GetFromJsonAsync<List<QueueOrder>>("orders");

        Assert.NotNull(orders![0].ReadyAt);
        Assert.NotNull(orders[0].ClosedAt);
    }
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest"
```

Ожидается: четыре новых теста падают — маршрута нет.

- [ ] **Step 3: Реализация**

Конструктор `QueueServer` получает часы — время «готов» и «закрыт» ставит сервер,
а не клиент, чтобы разошедшиеся часы кухонного планшета не попадали в базу:

```csharp
    private readonly Func<DateTime> _now;

    public QueueServer(QueueStorage storage, int port, string secret, Func<DateTime>? now = null)
    {
        _storage = storage;
        _port = port;
        _secret = secret;
        _now = now ?? (() => DateTime.Now);
    }
```

Маршрут:

```csharp
        app.MapPost("/orders/{id:guid}/state", async (Guid id, QueueOrderState to) =>
        {
            var order = await _storage.GetOrderAsync(id);
            if (order is null) return Results.NotFound();
            if (!QueueOrderStates.CanMove(order.State, to)) return Results.Conflict();

            order.State = to;
            if (to == QueueOrderState.Ready) order.ReadyAt = _now();
            if (to is QueueOrderState.Closed or QueueOrderState.Cancelled) order.ClosedAt = _now();

            await _storage.UpdateOrderStateAsync(order);
            return Results.Ok();
        });
```

В `IQueueStorage`/`QueueStorage`:

```csharp
    Task<QueueOrder?> GetOrderAsync(Guid id);
    Task UpdateOrderStateAsync(QueueOrder order);
```

```csharp
    public async Task<QueueOrder?> GetOrderAsync(Guid id)
        => (await GetOrdersAsync()).FirstOrDefault(o => o.Id == id);

    public async Task UpdateOrderStateAsync(QueueOrder order)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE QueueOrders SET State = $state, ReadyAt = $ready, ClosedAt = $closed " +
            "WHERE Id = $id";
        command.Parameters.AddWithValue("$state", order.State.ToString());
        command.Parameters.AddWithValue("$ready", (object?)order.ReadyAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$closed", (object?)order.ClosedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", order.Id.ToString());
        await command.ExecuteNonQueryAsync();
    }
```

Дописать `using System.Linq;` в `QueueStorage.cs`.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerTest"
```

Ожидается: 9 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/ tests/VvCash.Tests/QueueServerTest.cs
git commit -m "feat(queue): move orders through their states over http"
```

---

### Task 16: Вебсокет-рассылка

**Files:**
- Modify: `src/VvCash/Services/Queue/QueueServer.cs`
- Test: `tests/VvCash.Tests/QueueServerSocketTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueServerSocketTest.cs`:

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Экран, который не узнаёт об изменениях, тихо показывает вчерашнее —
/// самая частая поломка таких табло, поэтому пуш проверяется отдельно.</summary>
public class QueueServerSocketTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var db = Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");
        _server = new QueueServer(new QueueStorage(db), port: 0, secret: "secret");
        _port = await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        _client.DefaultRequestHeaders.Add("X-Queue-Secret", "secret");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    [Fact]
    public async Task ANewOrderIsPushedToASubscriber()
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws?secret=secret"), timeout.Token);

        await _client.PostAsJsonAsync("orders", new QueueOrder
        {
            Id = Guid.NewGuid(),
            Number = 305,
            TillIndex = 1,
            CreatedAt = new DateTime(2026, 8, 31, 14, 22, 0)
        });

        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        var text = Encoding.UTF8.GetString(buffer, 0, received.Count);

        Assert.Contains("305", text);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerSocketTest"
```

Ожидается: падает на `ConnectAsync` — маршрута `/ws` нет.

- [ ] **Step 3: Реализация**

В `QueueServer` — список подписчиков и рассылка:

```csharp
    private readonly List<WebSocket> _subscribers = new();
    private readonly object _subscribersGate = new();

    /// <summary>Рассылает всем живым подписчикам. Мёртвые убираются здесь же:
    /// отдельного «отписаться» у браузера нет — вкладку просто закрывают.</summary>
    private async Task BroadcastAsync()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(await _storage.GetOrdersAsync());

        List<WebSocket> targets;
        lock (_subscribersGate) targets = _subscribers.ToList();

        foreach (var socket in targets)
        {
            try
            {
                if (socket.State != WebSocketState.Open) throw new InvalidOperationException();
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                lock (_subscribersGate) _subscribers.Remove(socket);
            }
        }
    }
```

Включить вебсокеты и маршрут — до `app.StartAsync()`:

```csharp
        app.UseWebSockets();

        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            lock (_subscribersGate) _subscribers.Add(socket);

            // Полный снимок сразу после подключения: переподключившийся экран не
            // должен ждать следующего заказа, чтобы перестать врать.
            await socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(await _storage.GetOrdersAsync()),
                WebSocketMessageType.Text, true, CancellationToken.None);

            // Держим соединение, пока браузер его не закроет. Читать нам нечего:
            // экраны разговаривают с сервером через POST, а не через сокет.
            var buffer = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                    await socket.ReceiveAsync(buffer, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Оборванная вкладка — обычное дело, не ошибка.
            }
            finally
            {
                lock (_subscribersGate) _subscribers.Remove(socket);
            }
        });
```

В `MapPost("/orders")` и `MapPost("/orders/{id:guid}/state")` дописать
`await BroadcastAsync();` перед возвратом результата.

Дописать в шапку: `using System.Collections.Generic;`, `using System.Net.WebSockets;`,
`using System.Text.Json;`, `using System.Threading;`.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerSocketTest"
```

Ожидается: 1 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Queue/QueueServer.cs tests/VvCash.Tests/QueueServerSocketTest.cs
git commit -m "feat(queue): push order changes to subscribed screens"
```

---

### Task 17: HTTP-транспорт кассы-клиента

**Files:**
- Create: `src/VvCash/Services/Queue/HttpQueueTransport.cs`
- Test: `tests/VvCash.Tests/HttpQueueTransportTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/HttpQueueTransportTest.cs`:

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class HttpQueueTransportTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var db = Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");
        _server = new QueueServer(new QueueStorage(db), port: 0, secret: "secret");
        _port = await _server.StartAsync();
    }

    public Task DisposeAsync() => _server.StopAsync();

    private HttpQueueTransport Transport(string address, string secret = "secret")
        => new(new HttpClient(), () => address, () => secret);

    private static QueueOrder Order() => new()
    {
        Id = Guid.NewGuid(),
        Number = 305,
        TillIndex = 1,
        CreatedAt = new DateTime(2026, 8, 31, 14, 22, 0)
    };

    [Fact]
    public async Task ItReachesARunningServer()
        => Assert.True(await Transport($"127.0.0.1:{_port}").PostOrderAsync(Order()));

    [Fact]
    public async Task AWrongSecretReadsAsUnreachable()
        => Assert.False(await Transport($"127.0.0.1:{_port}", "wrong").PostOrderAsync(Order()));

    /// <summary>Пустой адрес — это «касса ещё не настроена», а не сбой. Он не
    /// должен ни бросать, ни стоить времени: до сокета дело не доходит.</summary>
    [Fact]
    public async Task AnEmptyAddressReadsAsUnreachableWithoutTryingTheNetwork()
        => Assert.False(await Transport("").PostOrderAsync(Order()));
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~HttpQueueTransportTest"
```

Ожидается: не компилируется.

- [ ] **Step 3: Реализация**

`src/VvCash/Services/Queue/HttpQueueTransport.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Queue;

/// <summary>Разговор с кассой-сервером по локалке. Адрес и секрет читаются
/// функциями, а не сохраняются в поле: настройки на кассе меняют без перезапуска,
/// и транспорт, запомнивший старый адрес, молча уходит в никуда.</summary>
public class HttpQueueTransport : IQueueTransport
{
    /// <summary>Локалка отвечает за миллисекунды. Дольше ждать нечего: касса не
    /// должна стоять из-за выключенного соседа.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly Func<string> _address;
    private readonly Func<string> _secret;

    public HttpQueueTransport(HttpClient http, Func<string> address, Func<string> secret)
    {
        _http = http;
        _address = address;
        _secret = secret;
    }

    public async Task<bool> PostOrderAsync(QueueOrder order)
    {
        var request = Build(HttpMethod.Post, "orders");
        if (request is null) return false;
        request.Content = JsonContent.Create(order);

        try
        {
            using var cancellation = new CancellationTokenSource(Timeout);
            using var response = await _http.SendAsync(request, cancellation.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // Недоступный сервер — штатное состояние точки, а не ошибка: заказ
            // остаётся в буфере и уйдёт позже.
            return false;
        }
        finally
        {
            request.Dispose();
        }
    }

    public async Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex)
    {
        var request = Build(HttpMethod.Get, $"orders?till={tillIndex}&state=Closed");
        if (request is null) return Array.Empty<QueueOrder>();

        try
        {
            using var cancellation = new CancellationTokenSource(Timeout);
            using var response = await _http.SendAsync(request, cancellation.Token);
            if (!response.IsSuccessStatusCode) return Array.Empty<QueueOrder>();
            return await response.Content.ReadFromJsonAsync<List<QueueOrder>>(cancellation.Token)
                   ?? (IReadOnlyList<QueueOrder>)Array.Empty<QueueOrder>();
        }
        catch (Exception)
        {
            return Array.Empty<QueueOrder>();
        }
        finally
        {
            request.Dispose();
        }
    }

    private HttpRequestMessage? Build(HttpMethod method, string path)
    {
        var address = _address();
        if (string.IsNullOrWhiteSpace(address)) return null;

        var request = new HttpRequestMessage(method, $"http://{address}/{path}");
        request.Headers.Add("X-Queue-Secret", _secret());
        return request;
    }
}
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~HttpQueueTransportTest"
```

Ожидается: 3 passed.

- [ ] **Step 5: Отфильтровать закрытые заказы на сервере**

`GetClosedAsync` дёргает `orders?till=&state=`, а маршрут пока отдаёт всё.
Дописать в `QueueServer` фильтр:

```csharp
        app.MapGet("/orders", async (int? till, string? state) =>
        {
            var orders = await _storage.GetOrdersAsync();
            if (till is not null) orders = orders.Where(o => o.TillIndex == till).ToList();
            if (!string.IsNullOrEmpty(state) && Enum.TryParse<QueueOrderState>(state, out var parsed))
                orders = orders.Where(o => o.State == parsed).ToList();
            return orders;
        });
```

- [ ] **Step 6: Прогнать оба серверных набора**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServer"
```

Ожидается: 10 passed.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Services/Queue/ tests/VvCash.Tests/HttpQueueTransportTest.cs
git commit -m "feat(queue): talk to the queue server over the local network"
```

---

# Фаза 5. Экраны

### Task 18: Палитра, общая с приложением

**Files:**
- Create: `src/VvCash/Assets/Web/theme.css`
- Test: `tests/VvCash.Tests/WebThemeTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/WebThemeTest.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace VvCash.Tests;

/// <summary>Экраны должны выглядеть продолжением кассы. Без этого теста первый
/// же правленый в Colors.axaml цвет молча оставит кухню и табло в старом:
/// две палитры расходятся тихо, потому что ни одна сборка на это не смотрит.</summary>
public class WebThemeTest
{
    private static readonly Dictionary<string, string> Mapping = new()
    {
        ["PrimaryColor"] = "--primary",
        ["PrimaryDarkColor"] = "--primary-dark",
        ["PrimaryLightColor"] = "--primary-light",
        ["BackgroundColor"] = "--background",
        ["TextPrimary"] = "--text-primary",
        ["TextSecondary"] = "--text-secondary",
        ["TextMuted"] = "--text-muted",
        ["SuccessColor"] = "--success",
        ["DangerColor"] = "--danger",
        ["BorderDarkColor"] = "--border"
    };

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void TheWebPaletteMatchesTheApplicationPalette()
    {
        var colors = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Styles", "Colors.axaml"));
        var theme = File.ReadAllText(Path.Combine(Root(), "src", "VvCash", "Assets", "Web", "theme.css"));

        foreach (var (key, variable) in Mapping)
        {
            var expected = Regex.Match(colors, $"<Color x:Key=\"{key}\">(#[0-9a-fA-F]{{6}})</Color>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(expected), $"{key} исчез из Colors.axaml");

            var actual = Regex.Match(theme, $@"{variable}:\s*(#[0-9a-fA-F]{{6}})\s*;").Groups[1].Value;
            Assert.Equal(expected.ToLowerInvariant(), actual.ToLowerInvariant());
        }
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~WebThemeTest"
```

Ожидается: падает — `theme.css` не существует.

- [ ] **Step 3: Реализация**

`src/VvCash/Assets/Web/theme.css`:

```css
/* Палитра кассы. Источник правды — Assets/Styles/Colors.axaml; расхождение
   ловит WebThemeTest. Правится этот файл только вместе с ним. */
:root {
    --primary: #0075e2;
    --primary-dark: #0063c7;
    --primary-light: #e6f1fc;
    --background: #f6f8f8;
    --card: #ffffff;
    --text-primary: #0f172a;
    --text-secondary: #64748b;
    --text-muted: #94a3b8;
    --success: #22c55e;
    --danger: #ef4444;
    --border: #e2e8f0;

    --radius-s: 8px;
    --radius-m: 12px;
    --radius-l: 16px;
}

* { box-sizing: border-box; }

body {
    margin: 0;
    background: var(--background);
    color: var(--text-primary);
    /* Inter — шрифт приложения. Из сети не тянется: на точке может не быть
       интернета, а экран должен подниматься в любом случае. */
    font-family: Inter, "Segoe UI", system-ui, sans-serif;
}

.card {
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: var(--radius-l);
}

.muted { color: var(--text-muted); }

.stale {
    background: var(--danger);
    color: #fff;
    padding: 8px 16px;
    text-align: center;
    font-weight: 700;
}
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~WebThemeTest"
```

Ожидается: 1 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Assets/Web/theme.css tests/VvCash.Tests/WebThemeTest.cs
git commit -m "feat(queue): share the register palette with the web screens"
```

---

### Task 19: Раздача статики

**Files:**
- Modify: `src/VvCash/VvCash.csproj`, `src/VvCash/Services/Queue/QueueServer.cs`
- Create: `src/VvCash/Assets/Web/board.html`, `src/VvCash/Assets/Web/kds.html`
- Test: `tests/VvCash.Tests/QueueServerStaticTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueServerStaticTest.cs`:

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueServerStaticTest : IAsyncLifetime
{
    private QueueServer _server = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var db = Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");
        _server = new QueueServer(new QueueStorage(db), port: 0, secret: "secret");
        var port = await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    [Theory]
    [InlineData("kds")]
    [InlineData("board")]
    public async Task ScreensAreServedFromTheAssembly(string page)
    {
        // Секрет параметром запроса: заголовок браузеру поставить неоткуда.
        var response = await _client.GetAsync($"{page}?secret=secret");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html", html);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheStylesheetIsServed()
    {
        var response = await _client.GetAsync("theme.css?secret=secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("--primary", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AScreenWithoutTheSecretIsRefused()
    {
        var response = await _client.GetAsync("board");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerStaticTest"
```

Ожидается: 404 — маршрутов нет.

- [ ] **Step 3: Реализация**

В `VvCash.csproj`, рядом с `AvaloniaResource`:

```xml
  <ItemGroup>
    <!-- Экраны едут внутри сборки, а не файлами рядом: иначе автообновление
         разъедется с кодом и на точке останется вчерашняя страница поверх
         сегодняшнего сервера. -->
    <EmbeddedResource Include="Assets/Web/*" />
  </ItemGroup>
```

В `QueueServer` — отдача из манифеста:

```csharp
    /// <summary>Читает файл, вшитый в сборку. Имя ресурса складывается из
    /// корневого namespace и пути с точками вместо слэшей.</summary>
    private static async Task<string?> ReadAssetAsync(string fileName)
    {
        var assembly = typeof(QueueServer).Assembly;
        await using var stream = assembly.GetManifestResourceStream($"VvCash.Assets.Web.{fileName}");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<IResult> AssetAsync(string fileName, string contentType)
    {
        var body = await ReadAssetAsync(fileName);
        return body is null ? Results.NotFound() : Results.Content(body, contentType);
    }
```

Маршруты — рядом с остальными:

```csharp
        app.MapGet("/kds", () => AssetAsync("kds.html", "text/html"));
        app.MapGet("/board", () => AssetAsync("board.html", "text/html"));
        app.MapGet("/theme.css", () => AssetAsync("theme.css", "text/css"));
```

Дописать `using System.IO;` в шапку.

Заготовки страниц — Task 20 и 21 наполнят их:

`src/VvCash/Assets/Web/board.html`:

```html
<!doctype html>
<html lang="ru">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Очередь</title>
    <link rel="stylesheet" href="theme.css">
</head>
<body></body>
</html>
```

`src/VvCash/Assets/Web/kds.html` — тот же каркас с `<title>Кухня</title>`.

Обратить внимание: `theme.css` со страницы запрашивается без секрета в адресе,
поэтому браузер получит 401. Чтобы страницы поднимались, ссылка на стиль должна
нести тот же секрет — это делает Task 20 первым же шагом, подставляя его из
адреса страницы. Пока достаточно, чтобы тесты этой задачи были зелёными.

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueServerStaticTest"
```

Ожидается: 4 passed.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/VvCash.csproj src/VvCash/Assets/Web/ src/VvCash/Services/Queue/QueueServer.cs tests/VvCash.Tests/QueueServerStaticTest.cs
git commit -m "feat(queue): serve the screens from inside the assembly"
```

---

### Task 20: Табло

**Files:**
- Modify: `src/VvCash/Assets/Web/board.html`

Тестов на разметку нет намеренно: проверять вёрстку из xunit дороже, чем
посмотреть на неё. Проверка — шаг 2, глазами.

- [ ] **Step 1: Написать страницу**

`src/VvCash/Assets/Web/board.html`:

```html
<!doctype html>
<html lang="ru">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Очередь</title>
    <script>
        // Секрет приезжает в адресе один раз и оседает в localStorage: телевизор
        // в зале открывают со ссылкой, а дальше он перезагружается сам.
        const fromUrl = new URLSearchParams(location.search).get('secret');
        if (fromUrl) localStorage.setItem('secret', fromUrl);
        const secret = localStorage.getItem('secret') || '';
        document.write(`<link rel="stylesheet" href="theme.css?secret=${encodeURIComponent(secret)}">`);
    </script>
</head>
<body>
<div id="stale" class="stale" hidden>Нет связи с кассой</div>
<main>
    <section>
        <h2>Готовятся</h2>
        <ul id="cooking" class="numbers"></ul>
    </section>
    <section>
        <h2>Готовы</h2>
        <ul id="ready" class="numbers"></ul>
    </section>
</main>

<style>
    main { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; padding: 24px; height: 100vh; }
    section { display: flex; flex-direction: column; }
    h2 { margin: 0 0 16px; font-size: 2rem; color: var(--text-secondary); text-transform: uppercase; }
    .numbers { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 16px; align-content: flex-start; }
    .numbers li {
        background: var(--card); border: 1px solid var(--border); border-radius: var(--radius-l);
        padding: 16px 32px; font-weight: 800; font-variant-numeric: tabular-nums;
        /* Размер зависит от заполненности: пять номеров видно от двери, сорок —
           хотя бы помещаются. */
        font-size: clamp(2.5rem, 8vw, 7rem);
    }
    #ready li { background: var(--success); color: #fff; border-color: var(--success); }
    #ready li.fresh { animation: pop 3s ease-out; }
    @keyframes pop {
        0%, 60% { background: var(--primary); border-color: var(--primary); }
        100% { background: var(--success); border-color: var(--success); }
    }
</style>

<script>
    const seen = new Set();

    function render(orders) {
        const put = (id, list) => {
            document.getElementById(id).innerHTML = list
                .map(o => `<li class="${id === 'ready' && !seen.has(o.id) ? 'fresh' : ''}">${o.number}</li>`)
                .join('');
        };

        put('cooking', orders.filter(o => o.state === 'New' || o.state === 'InProgress'));
        const ready = orders.filter(o => o.state === 'Ready');
        put('ready', ready);
        ready.forEach(o => seen.add(o.id));
    }

    // Переподключение с нарастающей паузой и полным перезапросом. Без этого
    // табло замирает со вчерашними номерами, и никто этого не замечает.
    let delay = 500;
    function connect() {
        const socket = new WebSocket(
            `ws://${location.host}/ws?secret=${encodeURIComponent(secret)}`);

        socket.onopen = () => {
            delay = 500;
            document.getElementById('stale').hidden = true;
        };
        socket.onmessage = event => render(JSON.parse(event.data));
        socket.onclose = () => {
            document.getElementById('stale').hidden = false;
            setTimeout(connect, delay);
            delay = Math.min(delay * 2, 15000);
        };
    }

    connect();
</script>
</body>
</html>
```

- [ ] **Step 2: Посмотреть глазами**

Собрать и запустить кассу с `QueueRole = Server`, открыть
`http://localhost:8770/board?secret=<секрет>`.

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Ожидается: два столбца на палитре кассы; выключение сервера показывает полосу
«Нет связи с кассой», включение — убирает её и восстанавливает список.

- [ ] **Step 3: Коммит**

```bash
git add src/VvCash/Assets/Web/board.html
git commit -m "feat(queue): show ready numbers on the hall board"
```

---

### Task 21: Кухонный экран

**Files:**
- Modify: `src/VvCash/Assets/Web/kds.html`

- [ ] **Step 1: Написать страницу**

`src/VvCash/Assets/Web/kds.html`:

```html
<!doctype html>
<html lang="ru">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Кухня</title>
    <script>
        const fromUrl = new URLSearchParams(location.search).get('secret');
        if (fromUrl) localStorage.setItem('secret', fromUrl);
        const secret = localStorage.getItem('secret') || '';
        document.write(`<link rel="stylesheet" href="theme.css?secret=${encodeURIComponent(secret)}">`);
    </script>
</head>
<body>
<div id="stale" class="stale" hidden>Нет связи с кассой</div>
<main id="orders"></main>

<style>
    main { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 16px; padding: 16px; }
    /* Крупно и с запасом: на кухне руки мокрые и в перчатках, промах по мелкой
       кнопке стоит заказа. */
    article { padding: 16px; cursor: pointer; user-select: none; min-height: 180px; }
    article header { display: flex; justify-content: space-between; align-items: baseline; }
    .number { font-size: 3rem; font-weight: 800; font-variant-numeric: tabular-nums; }
    .lines { list-style: none; margin: 12px 0 0; padding: 0; font-size: 1.25rem; }
    .lines li { display: flex; justify-content: space-between; padding: 4px 0; }
    .next { margin-top: 12px; font-weight: 700; color: var(--primary); text-transform: uppercase; }
    article.InProgress { border-color: var(--primary); border-width: 2px; }
    article.Ready { border-color: var(--success); border-width: 2px; }
</style>

<script>
    const NEXT = { New: 'InProgress', InProgress: 'Ready', Ready: 'Closed' };
    const LABEL = { New: 'Взять в работу', InProgress: 'Готов', Ready: 'Выдан' };

    async function move(id, to) {
        await fetch(`orders/${id}/state?secret=${encodeURIComponent(secret)}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(to)
        });
    }

    function render(orders) {
        const live = orders.filter(o => o.state !== 'Closed' && o.state !== 'Cancelled');
        document.getElementById('orders').innerHTML = live.map(o => `
            <article class="card ${o.state}" data-id="${o.id}" data-state="${o.state}">
                <header>
                    <span class="number">${o.number}</span>
                    <span class="muted">${new Date(o.createdAt).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</span>
                </header>
                <ul class="lines">
                    ${o.lines.map(l => `<li><span>${l.name}</span><span class="muted">${l.quantity}</span></li>`).join('')}
                </ul>
                <div class="next">${LABEL[o.state]}</div>
            </article>`).join('');
    }

    // Короткий тап двигает вперёд, долгое нажатие отменяет. Отмена намеренно
    // неудобна: смахнуть рукавом заказ, который уже готовят, нельзя.
    let held = null;
    document.getElementById('orders').addEventListener('pointerdown', event => {
        const card = event.target.closest('article');
        if (!card) return;
        held = setTimeout(() => {
            held = null;
            if (confirm(`Отменить заказ ${card.querySelector('.number').textContent}?`))
                move(card.dataset.id, 'Cancelled');
        }, 1200);
    });

    document.getElementById('orders').addEventListener('pointerup', event => {
        const card = event.target.closest('article');
        if (!card || held === null) return;
        clearTimeout(held);
        held = null;
        move(card.dataset.id, NEXT[card.dataset.state]);
    });

    let delay = 500;
    function connect() {
        const socket = new WebSocket(
            `ws://${location.host}/ws?secret=${encodeURIComponent(secret)}`);

        socket.onopen = () => {
            delay = 500;
            document.getElementById('stale').hidden = true;
        };
        socket.onmessage = event => render(JSON.parse(event.data));
        socket.onclose = () => {
            document.getElementById('stale').hidden = false;
            setTimeout(connect, delay);
            delay = Math.min(delay * 2, 15000);
        };
    }

    connect();
</script>
</body>
</html>
```

- [ ] **Step 2: Посмотреть глазами**

Открыть `http://localhost:8770/kds?secret=<секрет>`, пробить продажу на кассе.

Ожидается: карточка появилась без перезагрузки; тап двигает состояние; долгое
нажатие спрашивает про отмену; закрытый заказ исчезает.

- [ ] **Step 3: Коммит**

```bash
git add src/VvCash/Assets/Web/kds.html
git commit -m "feat(queue): drive orders from the kitchen screen"
```

---

# Фаза 6. Сборка воедино

### Task 22: Постановка заказа при продаже

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:2245-2340` (`ProceedToPayAsync`)
- Test: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`

Тесты живут в существующем файле, а не в новом. Причина: собрать `PosViewModel`
стоит двадцати двух зависимостей, и весь их набор уже написан там — `Deps` и
`CreateViewModel` на `PosViewModelSellerGateTest.cs:594-640`. Заглушки объявлены
`private` внутри класса, снаружи их не достать, а копировать три тысячи строк или
вытаскивать их в общий файл — работа, которая к очереди отношения не имеет и
разойдётся с оригиналом при первой же правке конструктора.

- [ ] **Step 1: Расширить существующий харнесс**

В `PosViewModelSellerGateTest.cs` заменить `FakePrinterService` (строка 235) на
записывающий — прежние тесты от этого не меняются, все методы по-прежнему
возвращают `true`:

```csharp
    private class FakePrinterService : IPrinterService
    {
        public List<string> Tickets { get; } = new();
        public List<string> KitchenOrders { get; } = new();

        /// <summary>Кухонный принтер, который отказывает. Нужен ровно одному
        /// тесту — тому, что продажа не срывается вместе с ним.</summary>
        public bool KitchenFails { get; set; }

        public PrinterStatus Status => PrinterStatus.Ready;
        public event EventHandler<PrinterStatus>? StatusChanged;
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null,
            string? documentNumber = null, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() => Task.FromResult(true);
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> lines, decimal totalRefund, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);

        public Task<bool> PrintTicketAsync(string number, string? time = null, string? warehouseName = null)
        {
            Tickets.Add(number);
            return Task.FromResult(true);
        }

        public Task<bool> PrintKitchenOrderAsync(SaleReceiptData sale, string queueNumber)
        {
            if (KitchenFails) return Task.FromResult(false);
            KitchenOrders.Add(queueNumber);
            return Task.FromResult(true);
        }
    }
```

Рядом с прочими заглушками добавить клиента очереди:

```csharp
    private sealed class FakeQueueClient : IQueueClient
    {
        public List<SaleReceiptData> Enqueued { get; } = new();
        public int Number { get; set; } = 305;

        public Task<QueueOrder> EnqueueAsync(SaleReceiptData sale)
        {
            Enqueued.Add(sale);
            return Task.FromResult(new QueueOrder { Id = Guid.NewGuid(), Number = Number });
        }

        public Task FlushAsync() => Task.CompletedTask;
    }
```

В `Deps` дописать два поля:

```csharp
        public FakePrinterService PrinterService { get; } = new();
        public FakeQueueClient QueueClient { get; } = new();
```

В `CreateViewModel` заменить `new FakePrinterService()` на `deps.PrinterService`
и дописать `deps.QueueClient` в конец списка аргументов — там же, где Task 22
добавляет параметр конструктору.

Дописать в шапку файла `using VvCash.Services.Queue;`.

- [ ] **Step 2: Написать падающие тесты**

Дописать в `PosViewModelSellerGateTest` четыре теста. `FakeSettingsService` в
этом файле уже реализует `ISettingsService` — роль принтера задаётся через её
`Printers`:

```csharp
    private static PrinterConfig Printer(PrintRole roles) => new()
    {
        Name = "p", ConnectionType = PrinterConnectionType.LAN,
        ConnectionString = "10.0.0.1:9100", IsEnabled = true, Roles = roles
    };

    [Fact]
    public async Task ASaleIssuesANumberAndPrintsBothQueueDocuments()
    {
        var vm = CreateViewModel(out var deps, d => d.SettingsService.Printers =
            new List<PrinterConfig> { Printer(PrintRole.Receipt | PrintRole.Ticket | PrintRole.KitchenOrder) });
        vm.CartItems.Add(new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 1m });

        await vm.ProceedToPayCommand.ExecuteAsync(null);

        Assert.Single(deps.QueueClient.Enqueued);
        Assert.Contains("305", deps.PrinterService.Tickets);
        Assert.Contains("305", deps.PrinterService.KitchenOrders);
    }

    /// <summary>Половина парка — точки без талонного принтера. Продажа там не
    /// должна ни выдавать номер, ни спотыкаться о его отсутствие.</summary>
    [Fact]
    public async Task ARegisterWithNoTicketPrinterIssuesNoNumber()
    {
        var vm = CreateViewModel(out var deps, d => d.SettingsService.Printers =
            new List<PrinterConfig> { Printer(PrintRole.Receipt) });
        vm.CartItems.Add(new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 1m });

        await vm.ProceedToPayCommand.ExecuteAsync(null);

        Assert.Empty(deps.QueueClient.Enqueued);
        Assert.Empty(deps.PrinterService.Tickets);
        Assert.Empty(vm.CartItems);
    }

    [Fact]
    public async Task ADeadKitchenPrinterDoesNotBlockTheSale()
    {
        var vm = CreateViewModel(out var deps, d => d.SettingsService.Printers =
            new List<PrinterConfig> { Printer(PrintRole.Receipt | PrintRole.KitchenOrder) });
        deps.PrinterService.KitchenFails = true;
        vm.CartItems.Add(new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 1m });

        await vm.ProceedToPayCommand.ExecuteAsync(null);

        // Корзина очищена — значит продажа дошла до конца, а не сорвалась на печати.
        Assert.Empty(vm.CartItems);
    }

    /// <summary>Кнопка «Печать чека» печатает копию уже пробитого чека. Новый
    /// номер на неё означал бы лишний заказ на кухне при каждом повторе.</summary>
    [Fact]
    public async Task ReprintingAReceiptIssuesNoNewNumber()
    {
        var vm = CreateViewModel(out var deps, d => d.SettingsService.Printers =
            new List<PrinterConfig> { Printer(PrintRole.Receipt | PrintRole.Ticket) });
        vm.CartItems.Add(new CartItem { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 1m });

        await vm.PrintReceiptCommand.ExecuteAsync(null);

        Assert.Empty(deps.QueueClient.Enqueued);
    }
```

Если `ProceedToPayCommand` или `PrintReceiptCommand` называются иначе — брать
фактические имена из `PosViewModel`, `[RelayCommand]` их порождает по имени
метода. Порядок вызова платежа списать с существующих тестов оплаты в этом же
файле, а не выдумывать: там уже настроены смена, документ и корзина.

- [ ] **Step 3: Убедиться, что тесты падают**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest"
```

Ожидается: не компилируется — `PosViewModel` не принимает `IQueueClient`.

- [ ] **Step 4: Реализация**

`PosViewModel` получает последним параметром конструктора `IQueueClient? queueClient`
и кладёт его в поле `_queueClient`. Nullable — касса может быть собрана без
очереди, и это не повод падать.

В `ProceedToPayAsync`, в ветке `if (outcome.Posted || outcome.Queued)`, сразу
после `await _printerService.PrintReceiptAsync(...)` и **до** `_cartService.ClearCart()`
— иначе позиции для бегунка уже вычищены:

```csharp
                        // Номер выдаётся, если точке есть что им напечатать: печать
                        // талона и бегунка не зависит от того, поднят ли сервер
                        // очереди. Касса с QueueRole = Off и талонным принтером —
                        // рабочая конфигурация, а не недонастроенная.
                        var needsNumber = _settingsService.Printers.Any(p => p.IsEnabled
                            && (p.Roles.HasFlag(PrintRole.Ticket) || p.Roles.HasFlag(PrintRole.KitchenOrder)));

                        if (needsNumber && _queueClient is not null)
                        {
                            var sale = new SaleReceiptData(
                                _cartService.Items.ToList(),
                                Subtotal, TotalDiscount, TotalAmount,
                                _cartService.AppliedDiscountName,
                                outcome.DocumentNumber,
                                null,
                                _sellerSession.Current?.FullName,
                                DateTime.Now.ToString("dd.MM.yyyy HH:mm"));

                            // Отказ любой из двух печатей продажу не отменяет: она уже
                            // закрыта, а заказ в очереди есть, и кухня увидит его на
                            // экране. Возвращаемые значения не проверяются намеренно —
                            // проверять тут нечего: откатывать нечего.
                            var order = await _queueClient.EnqueueAsync(sale);
                            var number = order.Number.ToString(CultureInfo.InvariantCulture);

                            await _printerService.PrintTicketAsync(number,
                                DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), null);
                            await _printerService.PrintKitchenOrderAsync(sale, number);
                        }
```

`_cartService.Items.ToList()` — копия, а не сам список: `ClearCart()` следующей
строкой опустошит оригинал, и бегунок, если печать окажется медленнее, уедет
пустым.

Дописать `using System.Globalization;` и `using VvCash.Services.Queue;`, если их
нет в шапке.

- [ ] **Step 5: Тесты зелёные**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PosViewModelSellerGateTest"
```

Ожидается: все прежние тесты файла плюс четыре новых.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(queue): enqueue the order and print its documents on a sale"
```

---

### Task 23: Регистрация служб и старт сервера

**Files:**
- Modify: `src/VvCash/App.axaml.cs:293-392`
- Test: ручная проверка, шаг 3

- [ ] **Step 1: Зарегистрировать службы**

В `ConfigureServices`, рядом с `services.AddSingleton<IPrinterService, CompositePrinterService>();`:

```csharp
        // Фабрикой, а не по типу: у QueueStorage единственный конструктор с
        // необязательной строкой, и контейнер попытается разрешить string.
        services.AddSingleton(sp => new QueueStorage());
        services.AddSingleton<IQueueStorage>(sp => sp.GetRequiredService<QueueStorage>());

        services.AddSingleton<INumberPool>(sp =>
        {
            var settings = (IQueueSettings)sp.GetRequiredService<ISettingsService>();
            return new NumberPool(sp.GetRequiredService<QueueStorage>(),
                settings.TillIndex, settings.QueueSecret, () => DateTime.Now);
        });

        services.AddSingleton<IQueueTransport>(sp =>
        {
            var settings = (IQueueSettings)sp.GetRequiredService<ISettingsService>();
            // Адрес и секрет читаются на каждом запросе: настройки правят без
            // перезапуска кассы.
            return new HttpQueueTransport(new HttpClient(),
                () => settings.QueueRole == QueueRole.Server
                    ? $"127.0.0.1:{settings.QueuePort}"
                    : settings.QueueServerAddress,
                () => settings.QueueSecret);
        });

        services.AddSingleton<IQueueClient>(sp => new QueueClient(
            sp.GetRequiredService<QueueStorage>(),
            sp.GetRequiredService<INumberPool>(),
            sp.GetRequiredService<IQueueTransport>(),
            ((IQueueSettings)sp.GetRequiredService<ISettingsService>()).TillIndex,
            () => DateTime.Now));
```

Обратить внимание: касса-сервер разговаривает сама с собой через тот же
транспорт по `127.0.0.1`. Отдельной ветки «я сервер, пишу напрямую» нет
намеренно — один путь означает один набор ошибок.

- [ ] **Step 2: Поднять сервер при старте**

В `OnFrameworkInitializationCompleted`, после инициализации хранилищ:

```csharp
            var queueSettings = (IQueueSettings)Services.GetRequiredService<ISettingsService>();
            if (queueSettings.QueueRole == QueueRole.Server)
            {
                var server = new QueueServer(Services.GetRequiredService<QueueStorage>(),
                    queueSettings.QueuePort, queueSettings.QueueSecret);

                // Результат намеренно не проверяется на успех: занятый порт — это
                // неверная настройка, а не причина не продавать. Причина ложится в
                // server.LastError, экран настроек её показывает.
                _ = server.StartAsync();
            }
```

- [ ] **Step 3: Проверить на живой кассе**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Проверить три конфигурации:

1. `QueueRole = Off` — касса продаёт, порт 8770 закрыт (`Test-NetConnection -Port 8770 localhost` даёт отказ).
2. `QueueRole = Server` — `/board` и `/kds` открываются, продажа появляется на экранах.
3. `QueueRole = Server` при уже занятом порту — касса запускается и продаёт.

- [ ] **Step 4: Коммит**

```bash
git add src/VvCash/App.axaml.cs
git commit -m "feat(queue): wire the queue services and start the server"
```

---

### Task 24: Настройки очереди на экране и правило файрвола

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`, `src/VvCash/Views/SettingsView.axaml`, `build/installer/VvCashInstaller.iss`

- [ ] **Step 1: Свойства во вью-модели**

В `SettingsViewModel`, рядом с полями дисплея покупателя:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueServer))]
    [NotifyPropertyChangedFor(nameof(IsQueueClient))]
    private QueueRole _queueRole = QueueRole.Off;

    [ObservableProperty] private string _queueServerAddress = string.Empty;
    [ObservableProperty] private int _queuePort = 8770;
    [ObservableProperty] private string _queueSecret = string.Empty;
    [ObservableProperty] private int _tillIndex;

    public Array QueueRoles => Enum.GetValues(typeof(QueueRole));

    public bool IsQueueServer => QueueRole == QueueRole.Server;
    public bool IsQueueClient => QueueRole == QueueRole.Client;
```

В методе загрузки настроек — там же, где читается `CustomerDisplayPort`:

```csharp
        var queueSettings = (IQueueSettings)_settingsService;
        QueueRole = queueSettings.QueueRole;
        QueueServerAddress = queueSettings.QueueServerAddress;
        QueuePort = queueSettings.QueuePort;
        QueueSecret = queueSettings.QueueSecret;
        TillIndex = queueSettings.TillIndex;
```

В сохранении — там же, где пишется `CustomerDisplayPort`:

```csharp
        var queueSettings = (IQueueSettings)_settingsService;
        queueSettings.QueueRole = QueueRole;
        queueSettings.QueueServerAddress = QueueServerAddress;
        queueSettings.QueuePort = QueuePort;
        queueSettings.QueueSecret = QueueSecret;
        queueSettings.TillIndex = TillIndex;
```

Приведение к `IQueueSettings` работает, потому что `SettingsService` реализует
оба интерфейса (Task 11). Вью-модель держит `ISettingsService` — менять её поле
на второй тип ради пяти свойств не стоит.

- [ ] **Step 2: Разметка**

В `SettingsView.axaml`, блоком рядом с настройками дисплея покупателя:

```xml
<StackPanel Spacing="8" Margin="0,16,0,0">
    <TextBlock Text="Очередь заказов" FontWeight="Bold"/>
    <ComboBox ItemsSource="{Binding QueueRoles}" SelectedItem="{Binding QueueRole}"/>
    <NumericUpDown Value="{Binding TillIndex}" Minimum="0" Maximum="4"
                   FormatString="0" Increment="1"/>
    <TextBox Text="{Binding QueueServerAddress}" Watermark="10.0.0.5:8770"
             IsVisible="{Binding IsQueueClient}"/>
    <NumericUpDown Value="{Binding QueuePort}" Minimum="1" Maximum="65535"
                   FormatString="0" Increment="1" IsVisible="{Binding IsQueueServer}"/>
    <TextBox Text="{Binding QueueSecret}" Watermark="Общий секрет точки"/>
</StackPanel>
```

Подписи взять через `I18nService` тем же способом, что соседние поля в этом
файле, и добавить ключи во все пять языковых файлов.

Номер кассы виден всегда, а не только у клиента: он определяет слайс пула, и
касса-сервер тоже продаёт. Две кассы с одинаковым номером начнут выдавать
одинаковые номера — при приёмке проверить это первым делом.

- [ ] **Step 3: Проверить глазами**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Ожидается: смена роли переключает видимость полей; значения переживают
перезапуск. Биндинги здесь отражательные — молчаливая опечатка видна только так.

- [ ] **Step 4: Правило файрвола в инсталляторе**

В `build/installer/VvCashInstaller.iss`, в секцию `[Run]` (строка 96):

```
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""VvCash Queue"" dir=in action=allow protocol=TCP localport=8770"; Flags: runhidden; StatusMsg: "Настройка сетевого доступа..."
```

Правило добавляется всегда, даже если очередь выключена: переставлять
инсталлятор ради галки в настройках на точке никто не будет. Порт открыт только
во внутренней сети — профиль по умолчанию у `netsh` именно такой.

- [ ] **Step 5: Полный прогон**

```bash
& ./run-tests.ps1
```

Ожидается: всё зелёное. Упавший тест не по теме очереди — сперва посмотреть
стек: у репозитория есть известная гонка Avalonia Dispatcher.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs src/VvCash/Views/SettingsView.axaml build/installer/
git commit -m "feat(settings): configure the queue and open its port on install"
```

---

### Task 25: Смена дня, досыл по таймеру и значок неотправленного

Три требования спеки, до которых не дотянулась ни одна задача выше: вчерашние
незакрытые заказы, регулярный досыл буфера и видимость этого буфера кассиру.

**Files:**
- Modify: `src/VvCash/Services/Queue/QueueStorage.cs`, `IQueueStorage.cs`, `QueueServer.cs`, `IQueueClient.cs`, `QueueClient.cs`
- Create: `src/VvCash/Services/Queue/QueueFlushLoop.cs`
- Modify: `src/VvCash/App.axaml.cs`, `src/VvCash/ViewModels/PosViewModel.cs`
- Test: `tests/VvCash.Tests/QueueDayRolloverTest.cs`

- [ ] **Step 1: Написать падающий тест**

`tests/VvCash.Tests/QueueDayRolloverTest.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Заказ, забытый открытым со вчера, держит свой номер занятым навсегда
/// и висит на кухонном экране поверх сегодняшних. Закрывать его должен сервер,
/// а не человек.</summary>
public class QueueDayRolloverTest
{
    private static QueueStorage Storage() => new(
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db"));

    private static QueueOrder Order(DateTime createdAt, QueueOrderState state) => new()
    {
        Id = Guid.NewGuid(),
        Number = 305,
        TillIndex = 0,
        State = state,
        CreatedAt = createdAt
    };

    [Fact]
    public async Task YesterdaysUnfinishedOrdersAreClosed()
    {
        var storage = Storage();
        await storage.SaveOrderAsync(Order(new DateTime(2026, 8, 30, 21, 0, 0), QueueOrderState.New));

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        var order = (await storage.GetOrdersAsync()).Single();
        Assert.Equal(QueueOrderState.Closed, order.State);
        Assert.NotNull(order.ClosedAt);
    }

    [Fact]
    public async Task TodaysOrdersAreLeftAlone()
    {
        var storage = Storage();
        await storage.SaveOrderAsync(Order(new DateTime(2026, 8, 31, 8, 0, 0), QueueOrderState.InProgress));

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        Assert.Equal(QueueOrderState.InProgress, (await storage.GetOrdersAsync()).Single().State);
    }

    /// <summary>Отменённый вчера заказ не должен переписываться в закрытый:
    /// это разные исходы, и отчёт по ним однажды спросят.</summary>
    [Fact]
    public async Task AlreadyFinishedOrdersAreNotRewritten()
    {
        var storage = Storage();
        await storage.SaveOrderAsync(Order(new DateTime(2026, 8, 30, 21, 0, 0), QueueOrderState.Cancelled));

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        Assert.Equal(QueueOrderState.Cancelled, (await storage.GetOrdersAsync()).Single().State);
    }

    [Fact]
    public async Task ThePendingCountIsWhatTheBufferHolds()
    {
        var storage = Storage();
        await storage.SaveOutboxAsync(Guid.NewGuid(), "Order", "{}");
        await storage.SaveOutboxAsync(Guid.NewGuid(), "Order", "{}");

        Assert.Equal(2, await storage.GetOutboxCountAsync("Order"));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDayRolloverTest"
```

Ожидается: не компилируется — `CloseStaleOrdersAsync` и `GetOutboxCountAsync` не
существуют.

- [ ] **Step 3: Реализация в хранилище**

В `IQueueStorage`:

```csharp
    /// <summary>Закрывает всё, что осталось незавершённым за прошлые дни.</summary>
    Task CloseStaleOrdersAsync(DateTime today);

    Task<int> GetOutboxCountAsync(string kind);
```

В `QueueStorage`:

```csharp
    /// <summary>date() читает ISO-8601, в котором CreatedAt и записан ("O"),
    /// поэтому сравнение идёт по календарным суткам, а не по абсолютному
    /// времени: заказ, пробитый в 23:59, закрывается утром, а не через сутки.
    ///
    /// Закрытые и отменённые не трогаются: это разные исходы, и переписывать
    /// один в другой ради единообразия нельзя.</summary>
    public async Task CloseStaleOrdersAsync(DateTime today)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE QueueOrders
            SET State = 'Closed', ClosedAt = $now
            WHERE State NOT IN ('Closed', 'Cancelled')
              AND date(CreatedAt) < date($now)";
        command.Parameters.AddWithValue("$now", today.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetOutboxCountAsync(string kind)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM QueueOutbox WHERE Kind = $kind";
        command.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
```

- [ ] **Step 4: Тест зелёный**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~QueueDayRolloverTest"
```

Ожидается: 4 passed.

- [ ] **Step 5: Позвать уборку с сервера**

В `QueueServer.StartAsync`, сразу после `await _storage.InitializeAsync();`:

```csharp
            await _storage.CloseStaleOrdersAsync(_now());
```

и в обработчике `POST /orders`, первой строкой — чтобы точка, работающая без
перезапуска касс, всё равно переворачивала день:

```csharp
            await _storage.CloseStaleOrdersAsync(_now());
```

- [ ] **Step 6: Досыл по таймеру**

`src/VvCash/Services/Queue/QueueFlushLoop.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Queue;

/// <summary>Гоняет QueueClient.FlushAsync, пока касса жива. Отдельно от
/// SyncService: тот ходит на бэкенд раз в минуты, а сосед по локалке может
/// вернуться через секунды, и держать заказ в буфере всё это время незачем.</summary>
public sealed class QueueFlushLoop : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IQueueClient _client;
    private readonly CancellationTokenSource _cancellation = new();

    public QueueFlushLoop(IQueueClient client) => _client = client;

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation.Token))
            {
                try
                {
                    await _client.FlushAsync();
                }
                catch (Exception ex)
                {
                    // Досыл — фоновая работа. Уронить кассу он не имеет права ни
                    // при какой ошибке, включая нечитаемое, что вернул сосед.
                    Console.WriteLine($"Queue flush error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Обычное завершение при закрытии кассы.
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
```

В `App.axaml.cs`, рядом со стартом сервера:

```csharp
            if (queueSettings.QueueRole != QueueRole.Off)
            {
                var flushLoop = new QueueFlushLoop(Services.GetRequiredService<IQueueClient>());
                flushLoop.Start();
            }
```

- [ ] **Step 7: Значок неотправленного**

В `IQueueClient` добавить `Task<int> PendingCountAsync();`, в `QueueClient` —
`=> _storage.GetOutboxCountAsync(OrderKind);`. Заглушка `FakeQueueClient` в
`PosViewModelSellerGateTest.cs` получает `public Task<int> PendingCountAsync() => Task.FromResult(0);`.

В `PosViewModel` — свойство `UnsentQueueOrders`, обновляемое там же, где сейчас
обновляется счётчик неотправленных документов, и показанное рядом с ним в
`PosView.axaml`. Отдельный счётчик, а не сложение с документами: заказ, не
доехавший до соседней кассы, и чек, не доехавший до бэкенда, чинятся по-разному,
и слитый счётчик отправит кассира не туда.

- [ ] **Step 8: Проверить глазами**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Выключить кассу-сервер, пробить продажу — счётчик показывает 1. Включить обратно
— в течение пятнадцати секунд обнуляется сам, заказ появляется на `/kds`.

- [ ] **Step 9: Коммит**

```bash
git add src/VvCash/Services/Queue/ src/VvCash/App.axaml.cs src/VvCash/ViewModels/PosViewModel.cs src/VvCash/Views/PosView.axaml tests/VvCash.Tests/
git commit -m "feat(queue): roll the day over, retry the buffer and surface what is unsent"
```

---

## Приёмка на точке

Сначала — полный прогон, целиком, а не по фильтру:

```bash
& ./run-tests.ps1
```

Упавший тест не по теме очереди — сперва посмотреть стек: у репозитория есть
известная гонка Avalonia Dispatcher, роняющая случайный тест.

Дальше собрать инсталлятор и проверить руками — тесты сюда не дотягиваются:

- [ ] Две кассы: одна `Server`, вторая `Client` с адресом первой. Продажа на второй появляется на `/kds` первой.
- [ ] Выключить кассу-сервер. Продажа на клиенте проходит, талон и бегунок печатаются. Включить обратно — заказ доезжает сам.
- [ ] Телевизор с `/board`: номера читаются с дальнего конца зала.
- [ ] Планшет с `/kds`: карточка нажимается мокрым пальцем с первого раза.
- [ ] Два талона за день не дают вычислить, сколько было продаж между ними.
- [ ] Касса с `QueueRole = Off` и талонным принтером печатает номер.
