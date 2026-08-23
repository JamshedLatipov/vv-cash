# Синк и хранилище (батч C) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** закрыть шесть находок код-ревью — сверку каталога с остатками, типы денежных колонок в SQLite, гонку инициализации хранилища, проверку кредитного лимита, построчную скидку в корзине POS и границу кэша картинок.

**Архитектура:** правки лежат в четырёх независимых слоях и почти не пересекаются. Хранилище (`OfflineStorageService`) получает замок инициализации, перестройку таблиц с `REAL` на `TEXT` и колонку остатка. Синк (`SyncService`) получает постраничный обход `/cashes/remain/` и применение сверки. Вью-модели (`MixedPaymentViewModel`, `CartService`) получают проверку лимита и заполнение скидочных полей. `ProductImageLoader` получает LRU. Единственная жёсткая зависимость по порядку — замок инициализации должен появиться раньше перестройки таблиц.

**Стек:** .NET 10, Avalonia 11, `Microsoft.Data.Sqlite` 10.0.5, CommunityToolkit.Mvvm, xUnit.

**Спека:** [2026-08-23-sync-and-storage-design.md](../specs/2026-08-23-sync-and-storage-design.md)
**Ветка:** `fix/sync-and-storage` (уже создана, спека на ней закоммичена)

---

## Правила окружения — прочитать до первой правки

Эти вещи ломают работу молча, а не с ошибкой.

- **`pwsh` на машине нет.** Тесты гонять `& ./run-tests.ps1` из PowerShell. Не `pwsh ./run-tests.ps1`.
- **Никогда `dotnet build`/`dotnet test` без `-o`.** Приложение держит блокировку каталога вывода. Всегда `-o build/verify`; `run-tests.ps1` уже перенаправляет сам.
- **Проверка предупреждений требует `--no-incremental`.** Без него первая сборка после правки инкрементальная и честно показывает ноль. Базовая линия — **одно** предупреждение, унаследованный CS8601 в `PosViewModel.cs:2266`.
- **BOM в этом репозитории — по файлу, а не по правилу.** Измерено 2026-08-23, а не предположено:

  Из 201 файла `.cs` и `.axaml` в `src` и `tests` BOM несут **десять**. Перечислены полностью, потому что обобщать тут уже дважды выходило боком:

  | Файл | BOM |
  |---|---|
  | `src/VvCash/Services/Data/SyncService.cs` | **есть** |
  | `src/VvCash/Services/Api/ExpenseDocumentService.cs` | **есть** |
  | `src/VvCash/Services/Api/ProductService.cs` | **есть** |
  | `src/VvCash/Services/Api/ShiftService.cs` | **есть** |
  | `src/VvCash/Services/Hardware/CompositePrinterService.cs` | **есть** |
  | `src/VvCash/Services/Hardware/EscPosPrinterService.cs` | **есть** |
  | `tests/VvCash.Tests/EscPosUnitTest.cs` | **есть** |
  | `tests/VvCash.Tests/ExchangeViewModelTest.cs` | **есть** |
  | `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` | **есть** |
  | `tests/VvCash.Tests/ReturnsViewModelTest.cs` | **есть** |
  | `Assets/i18n/*.json` (все пять) | **есть** |
  | **все остальные 191** | нет |

  Концы строк — CRLF везде, одиночных LF нет нигде. Из задач этого батча BOM касаются `SyncService.cs` (Task 4) и `PosViewModelSellerGateTest.cs` (задет в Task 3 как одна из шести заглушек).

  Правка, снявшая BOM с локали, ломает её загрузку молча. Правка, **добавившая** BOM туда, где его не было, — лишний шум в диффе. Снимать байтовый слепок до и после каждой правки:

  ```bash
  python -c "
  d=open(r'ПУТЬ','rb').read()
  print('BOM',d[:3]==b'\xef\xbb\xbf','CRLF',d.count(b'\r\n'),'bareLF',d.count(b'\n')-d.count(b'\r\n'))"
  ```

  Ожидание всегда: `bareLF 0`.

- **Консоль рендерит кириллицу мохиморда даже когда байты целы.** Не судить о содержимом файла по выводу `cat`; сверять `python -c` по байтам.
- **Привязки Avalonia здесь не компилируемые.** Неверный путь привязки собирается чисто, ничего не бросает и молча ничего не показывает. Ни один тест этого не ловит. Каждую новую привязку сверять глазами против объявляющего типа.
- **Полный прогон изредка роняет случайный посторонний тест** на гонке Avalonia Dispatcher. Смотреть стек прежде, чем винить свою правку.
- **`build_deploy.ps1` не отслеживается и не наш.** Никогда `git add -A`. Всегда перечислять файлы поимённо.

## Дисциплина проверки

В каждой задаче после зелёного идёт шаг «мутация». Красный от ошибки компиляции не доказывает ничего: вакуумный тест выглядит точно так же. Проверка — только так: правка откатывается ровно в одном месте, тест обязан покраснеть, правка возвращается.

Батч B отгрузил один вакуумный тест и едва не отгрузил ещё два; все три поймала мутация, а самый дорогой случай нашёлся только на финальном ревью. Шаг «мутация» не пропускать даже когда «очевидно».

**Мутация, которая не применилась, выглядит точно как мутация, которая ничего не сломала.** Найдено при исполнении Task 2, и стоит здесь, а не в разделе долга, потому что портит выводы всех последующих шагов. Скрипт заменял строку `MaxDiscount TEXT NOT NULL DEFAULT '0'` и совпал **дважды**: строка схемы с отступом в шестнадцать пробелов является подстрокой строки `Sellers_new` с отступом в двадцать. Скрипт проверял число совпадений и аварийно вышел **до записи на диск**. Прогон после него был зелёным — и не значил ничего, потому что код остался прежним.

Без проверки числа совпадений это читалось бы как «мутация не уронила тест, значит тест вакуумный», и настоящий, работающий тест был бы выброшен по ложной улике. Отсюда правило: мутация обязана **доказать, что правка легла на диск** — совпадение по полной строке, а не по подстроке; проверка числа замен; печать изменённых строк перед прогоном. Зелёный прогон после мутации — вывод только тогда, когда доказано, что мутация состоялась.

---

## Структура файлов

**Изменяются:**

| Файл | За что отвечает после правок |
|---|---|
| `src/VvCash/Services/Data/OfflineStorageService.cs` | замок инициализации, перестройка таблиц, колонка `StockQuantity`, применение сверки |
| `src/VvCash/Services/Data/IOfflineStorageService.cs` | новый метод применения сверки |
| `src/VvCash/Services/Data/SyncService.cs` | обход `/cashes/remain/`, применение сверки |
| `src/VvCash/Models/Product.cs` | `StockQuantity` и производное `IsOutOfStock` |
| `src/VvCash/ViewModels/PosViewModel.cs` | часовая каденция сверки, передача лимита и баланса на экран оплаты |
| `src/VvCash/ViewModels/MixedPaymentViewModel.cs` | проверка кредитного лимита |
| `src/VvCash/Services/CartService.cs` | заполнение и сброс скидочных полей строки |
| `src/VvCash/Services/ProductImageLoader.cs` | LRU с границей |
| `src/VvCash/Views/PosView.axaml` | плашка «нет по учёту», построчная скидка в корзине |
| `src/VvCash/Views/MixedPaymentView.axaml` | баланс, лимит и причина отказа |
| `src/VvCash/Assets/i18n/{ru,en,tg,uz,kk}.json` | новые ключи, все пять |

**Создаются:**

| Файл | За что отвечает |
|---|---|
| `src/VvCash/Models/Api/CashRemainItem.cs` | DTO одной строки и страницы ответа `/cashes/remain/` |
| `src/VvCash/Services/LruCache.cs` | ограниченная по числу записей карта с вытеснением |
| `tests/VvCash.Tests/ProductImageCacheTest.cs` | граница кэша |

**Дописываются тестами:** `OfflineStorageServiceTest.cs`, `SyncServiceTest.cs`, `MixedPaymentViewModelTest.cs`, `CartServiceQuoteTest.cs`.

Тесты обхода и сверки идут **в существующий `SyncServiceTest.cs`**, а не в новый файл. Причина конкретная: `FakeSettings`, `FakeStorage` и `FakeExpenseDocuments` объявлены там как `private sealed` вложенные классы (`SyncServiceTest.cs:18`, `:39`, `:119`) — из другого файла они недоступны, а дублировать заглушку интерфейса на два десятка методов ради трёх тестов не стоит того. Там же лежит готовый хелпер `Build(StubHttpMessageHandler handler, FakeStorage storage)` (`:130`).

---

## Task 1: Инициализация хранилища перестаёт быть гонкой

Находка #16. Идёт первой, потому что Task 2 кладёт внутрь `InitializeAsync` перестройку таблиц: два одновременных вызова после Task 2 означают два `DROP TABLE` на одних данных.

**Тест этой задачи живёт в Task 2, и это осознанно.** Сегодня `InitializeAsync` состоит из `CREATE TABLE IF NOT EXISTS` и `ALTER TABLE`, обёрнутых в «уже есть — не беда»; параллельный вызов ничему не вредит. Тест на конкурентность, написанный сейчас, был бы зелёным и без замка — то есть вакуумным. Он появляется в Task 2, где перестройка делает гонку настоящей, и там же проверяется мутацией «убрать замок».

**Files:**
- Modify: `src/VvCash/Services/Data/OfflineStorageService.cs:11-14` (поля), `:30-32` (вход в `InitializeAsync`), `:174` (выход)

- [ ] **Step 1: Добавить using и поле замка**

В шапке файла к существующим `using` добавить:

```csharp
using System.Threading;
```

Поле рядом с `_isInitialized`:

```csharp
public class OfflineStorageService : IOfflineStorageService
{
    private readonly string _connectionString;
    private bool _isInitialized = false;

    /// <summary>Serialises InitializeAsync. The fast path still reads _isInitialized
    /// without the lock — that read is only ever false-negative, and a false negative
    /// costs one uncontended WaitAsync, not a second initialisation: the flag is
    /// re-checked under the lock.
    ///
    /// Not merely defensive since the schema rebuild landed inside InitializeAsync:
    /// two concurrent callers would run two DROP TABLE against the same rows.</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);
```

- [ ] **Step 2: Обернуть тело `InitializeAsync`**

Было:

```csharp
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        using var connection = new SqliteConnection(_connectionString);
```

Стало:

```csharp
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            using var connection = new SqliteConnection(_connectionString);
```

- [ ] **Step 3: Закрыть `try` в конце метода**

Конец метода был:

```csharp
        await BackfillSearchTextAsync(connection);

        _isInitialized = true;
    }
```

Стал:

```csharp
            await BackfillSearchTextAsync(connection);

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
```

Всё тело метода между Step 2 и Step 3 сдвигается на один уровень отступа. Отступ в этом файле — четыре пробела.

- [ ] **Step 4: Собрать и прогнать тесты**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
```

Ожидание: сборка успешна, **одно** предупреждение (CS8601 в `PosViewModel.cs:2266`). Если предупреждений больше — разобраться до коммита.

```bash
& ./run-tests.ps1
```

Ожидание: 757 passed. Поведение не менялось, ни один тест не должен сдвинуться.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Services/Data/OfflineStorageService.cs
git commit -m "fix(storage): serialise database initialisation before it starts rebuilding tables"
```

---

## Task 2: Денежные колонки переезжают из REAL в TEXT

Находка #9. Самая рискованная задача батча: она трогает схему на кассах, которые уже в бою.

**Что измерено и на чём стоит решение** (воспроизводимо, не из головы):

```
REAL affinity -> typeof=real raw=1234.56 | TEXT affinity -> typeof=text raw=1234.56
GetDecimal: fromREAL=1234,56 fromTEXT=1234,56
AddWithValue(decimal) into TEXT column -> typeof=text raw=1.000000000000001
```

Под принудительной `ru-RU` в базу уходит точка, а не запятая — культура на хранение не влияет. `AddWithValue(decimal)` без явного `SqliteType` тоже кладёт точный текст, поэтому `SaveParkedSaleAsync` чинится **одной сменой типа колонки**, параметры там трогать не нужно.

Значения, которые `REAL` теряет, а `TEXT` держит (тоже измерено):

```
1,000000000000001 -> REAL теряет
12345678901234,56 -> REAL теряет
19,99             -> REAL переживает
1234,56           -> REAL переживает
12,5              -> REAL переживает
```

**Конкретное прочитанное значение здесь намеренно не приводится.** Первая редакция плана его приводила, и это оказалось фактом со скрытой зависимостью: одно и то же входное значение читается по-разному в зависимости от пути чтения — через текстовый рендер SQLite (`%!.15g`, пятнадцать значащих) или прямой конверсией `double` в `decimal` (около семнадцати). Три независимых замера дали три разных ответа, и каждый был верен для своего пути.

Инвариант, на который опирается тест, от пути не зависит: перечисленные значения `REAL` **теряет**, обычные цены — нет. Проверять теми, что тест ещё сторожит, надо откатом колонки в `REAL`, а не сверкой с запомненным числом.

Эти два и берутся в тест точности. Любое «обычное» значение вроде `1234.56` переживает REAL без потерь и дало бы зелёный тест до и после правки.

**Files:**
- Modify: `src/VvCash/Services/Data/OfflineStorageService.cs` — блок схемы `:60-123`, миграции `:134-166`, параметры `:290-292`, `:299`, `:940`
- Test: `tests/VvCash.Tests/OfflineStorageServiceTest.cs`

- [ ] **Step 1: Написать падающий тест на перестройку**

В `OfflineStorageServiceTest.cs` добавить. Тест руками строит базу в старой форме — с `REAL` и без `StockQuantity`, — потом натравливает на неё боевой `InitializeAsync`.

```csharp
    /// <summary>Builds a database in the pre-migration shape and checks that
    /// InitializeAsync rebuilds it: declared types become TEXT, the row survives, the
    /// indices come back, and the new StockQuantity column is there.
    ///
    /// The indices matter and are easy to lose: they are created in the schema block
    /// that runs earlier in the same InitializeAsync, and DROP TABLE takes them with
    /// the table.</summary>
    [Fact]
    public async Task InitializeAsync_UpgradingFromRealColumns_RebuildsAsTextAndKeepsRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-migrate-{Guid.NewGuid()}.db");
        try
        {
            using (var seed = new SqliteConnection($"Data Source={dbPath}"))
            {
                await seed.OpenAsync();
                using var cmd = seed.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Products (
                        Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Sku TEXT, Category TEXT,
                        Price REAL NOT NULL, OriginalPrice REAL, DiscountPercent REAL,
                        ImagePath TEXT, Barcode TEXT, Tags TEXT,
                        UnitId TEXT, UnitCode TEXT, UnitShortName TEXT, UnitFactor REAL,
                        IsDivisible INTEGER, SellInSecondaryUnit INTEGER, SearchText TEXT
                    );
                    INSERT INTO Products (Id, Name, Price, UnitFactor, SearchText)
                    VALUES ('p-1', 'Товар', 19.99, 2.5, 'товар');
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            await new OfflineStorageService(dbPath).InitializeAsync();

            using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();

            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "Price"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "OriginalPrice"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "DiscountPercent"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "UnitFactor"));
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "StockQuantity"));

            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT Name, Price, UnitFactor FROM Products WHERE Id = 'p-1';";
                using var rd = await cmd.ExecuteReaderAsync();
                Assert.True(await rd.ReadAsync());
                Assert.Equal("Товар", rd.GetString(0));
                Assert.Equal(19.99m, rd.GetDecimal(1));
                Assert.Equal(2.5m, rd.GetDecimal(2));
            }

            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='Products';";
                var found = new List<string>();
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) found.Add(rd.GetString(0));
                Assert.Contains("IDX_Products_Category", found);
                Assert.Contains("IDX_Products_Barcode", found);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    private static async Task<string> DeclaredTypeAsync(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return (await cmd.ExecuteScalarAsync()) as string ?? string.Empty;
    }
```

- [ ] **Step 2: Написать падающий тест на точность**

Там же:

```csharp
    /// <summary>The value matters more than the assertion. 19.99 or 1234.56 round-trip
    /// through REAL without loss, so a test using one of those would pass before and
    /// after the migration — a green test guarding nothing. These two were measured to
    /// break under REAL, whereas ordinary prices do not. The figure REAL reads back is
    /// deliberately not quoted: it depends on the read path, and three measurements gave
    /// three answers, each correct for how it was measured. Confirm this test still has
    /// teeth by reverting a column to REAL and watching it go red — not by comparing
    /// against a remembered number.</summary>
    [Fact]
    public async Task SaveProductsAsync_ValuesThatDoNotSurviveDouble_RoundTripExactly()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product
            {
                Id = "p-precise",
                Name = "Точность",
                Price = 12345678901234.56m,
                UnitFactor = 1.000000000000001m,
            }
        });

        var loaded = (await _service.GetAllProductsAsync()).Single(p => p.Id == "p-precise");

        Assert.Equal(12345678901234.56m, loaded.Price);
        Assert.Equal(1.000000000000001m, loaded.UnitFactor);
    }
```

- [ ] **Step 3: Прогнать оба теста — они обязаны упасть**

```bash
& ./run-tests.ps1
```

Ожидание: `InitializeAsync_UpgradingFromRealColumns_RebuildsAsTextAndKeepsRows` падает на `Assert.Equal("TEXT", ...)` — реально там `REAL`. `SaveProductsAsync_ValuesThatDoNotSurviveDouble_RoundTripExactly` падает на несовпадении чисел.

Если второй тест **прошёл** — значения подобраны неверно, и тест вакуумный. Остановиться и подобрать заново, а не идти дальше.

- [ ] **Step 4: Поменять объявления колонок в блоке схемы**

В `OfflineStorageService.cs`, блок `command.CommandText = @"..."` внутри `InitializeAsync`.

`Products` — было:

```
                Price REAL NOT NULL,
                OriginalPrice REAL,
                DiscountPercent REAL,
```

стало:

```
                -- TEXT, not REAL: REAL affinity converts what is written to a float,
                -- and these are money. Microsoft.Data.Sqlite writes decimal to TEXT
                -- culture-invariantly (measured under ru-RU), and GetDecimal reads it
                -- back exactly. See the batch C spec for the measurement.
                Price TEXT NOT NULL,
                OriginalPrice TEXT,
                DiscountPercent TEXT,
```

там же, `UnitFactor REAL,` → `UnitFactor TEXT,`, и сразу после `SearchText TEXT` добавить новую колонку:

```
                -- Stock for this register's warehouse as of the last complete
                -- reconciliation walk. NULL means the walk has never completed, and the
                -- register behaves exactly as it did before the walk existed.
                StockQuantity TEXT
```

`ParkedSales` — было `Total REAL NOT NULL,` и `ItemCount REAL NOT NULL,`, стало `Total TEXT NOT NULL,` и `ItemCount TEXT NOT NULL,`. Комментарий над `ItemCount` переписать: обоснование было про дробность, а не про плавающую точку, и в TEXT дробность сохраняется лучше.

```
            CREATE TABLE IF NOT EXISTS ParkedSales (
                Id TEXT PRIMARY KEY,
                Label TEXT,
                CustomerName TEXT,
                Total TEXT NOT NULL,
                -- TEXT, and not INTEGER, for the same reason it was never INTEGER: a
                -- weighted line contributes a fraction of a unit. TEXT rather than REAL
                -- because a fraction is exactly what a float rounds.
                ItemCount TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Payload TEXT NOT NULL
            );
```

`Sellers` — было `MaxDiscount REAL NOT NULL DEFAULT 0`, стало `MaxDiscount TEXT NOT NULL DEFAULT '0'`. Кавычки вокруг нуля обязательны.

- [ ] **Step 5: Поменять пять параметров записи**

Ровно пять мест, все с явным `SqliteType.Real`:

```csharp
// :290-292 в SaveProductsAsync
var priceParam = command.Parameters.Add("$Price", SqliteType.Text);
var origPriceParam = command.Parameters.Add("$OriginalPrice", SqliteType.Text);
var discountParam = command.Parameters.Add("$DiscountPercent", SqliteType.Text);
// :299 там же
var unitFactorParam = command.Parameters.Add("$UnitFactor", SqliteType.Text);
// :940 в SaveSellersAsync
var maxDiscountParam = command.Parameters.Add("$MaxDiscount", SqliteType.Text);
```

`SaveParkedSaleAsync` не трогать: там `AddWithValue`, который для `decimal` и так связывает как текст — измерено.

Проверить, что не осталось: `grep -n "SqliteType.Real" src/VvCash/Services/Data/OfflineStorageService.cs` — ожидание: пусто.

- [ ] **Step 6: Извлечь `InitializeCoreAsync`**

Внесено по итогам ревью качества Task 1. Task 1 обернула всё тело `InitializeAsync` в `try`, и guard теперь читается как двухсотстрочный блок. В репозитории уже есть правильная форма для ровно этой задачи — `UpdateService.DownloadAsync` (`src/VvCash/Services/Update/UpdateService.cs:156-167`): двенадцатистрочная обёртка с семафором вокруг `DownloadCoreAsync`.

Делается здесь, а не в Task 1, по одной причине: Task 2 всё равно вскрывает этот метод, а отдельной правкой это был бы третий большой дифф по тем же строкам. Заодно чинит побочный эффект обёртки — `git blame` без `-w` приписывает все полторы сотни строк SQL коммиту Task 1.

Разрезать так: guard остаётся в `InitializeAsync`, всё содержимое `try` уезжает в приватный `InitializeCoreAsync` и **возвращается на исходный уровень отступа**.

```csharp
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            await InitializeCoreAsync();

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Everything InitializeAsync does once it has decided it is the one doing
    /// it: schema, additive column migrations, the REAL-to-TEXT table rebuilds, and the
    /// SearchText backfill. Split out so the guard above stays readable — the same shape
    /// UpdateService.DownloadAsync uses around DownloadCoreAsync.</summary>
    private async Task InitializeCoreAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();

        // ... тело без изменений, на исходном отступе ...

        await BackfillSearchTextAsync(connection);
    }
```

`_isInitialized = true;` **остаётся в `InitializeAsync`**, а не уезжает в core: флаг — часть протокола замка, а не часть инициализации.

Проверить после извлечения, что дифф — это только отступ и перенос:

```bash
git diff -w -- src/VvCash/Services/Data/OfflineStorageService.cs
```

Ожидание: видны только новая сигнатура `InitializeCoreAsync`, её комментарий, вызов и снятая обёртка. Ни одной изменённой строки SQL. Если `git diff -w` показывает правки внутри `command.CommandText`, значит при переносе задето содержимое — откатить и перенести заново.

- [ ] **Step 7: Добавить пробу объявленного типа и перестройку**

Новые приватные методы рядом с `AddColumnIfMissingAsync`:

```csharp
    /// <summary>The declared type of one column, or "" when the table or column is
    /// absent. Declared type, not storage class: SQLite reports what CREATE TABLE said,
    /// which is exactly the thing this migration changes.</summary>
    private static async Task<string> DeclaredTypeAsync(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return (await cmd.ExecuteScalarAsync()) as string ?? string.Empty;
    }

    /// <summary>Rebuilds a table under a new column declaration, because SQLite has no
    /// ALTER COLUMN. Rows are copied, not dropped: a register that upgrades while
    /// offline would otherwise be left with no catalogue and nothing to sell until the
    /// next successful sync.
    ///
    /// <paramref name="indexes"/> is not optional housekeeping. Indices are created in
    /// the schema block that already ran earlier in this same InitializeAsync, and the
    /// DROP TABLE below takes them with the table.</summary>
    private static async Task RebuildTableAsync(
        SqliteConnection connection, string table, string createNewSql,
        string copiedColumns, params string[] indexes)
    {
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;

        foreach (var sql in new[]
        {
            createNewSql,
            $"INSERT INTO {table}_new ({copiedColumns}) SELECT {copiedColumns} FROM {table};",
            $"DROP TABLE {table};",
            $"ALTER TABLE {table}_new RENAME TO {table};",
        }.Concat(indexes))
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
```

Дописать `using System.Linq;` в шапку файла, если его там ещё нет — `Concat` из него.

- [ ] **Step 8: Позвать перестройку из `InitializeAsync`**

Вставить **после** блока `AddColumnIfMissingAsync` с колонками единиц измерения и **до** `await BackfillSearchTextAsync(connection);`. Порядок важен: к этому моменту старая таблица уже догнана по составу колонок, поэтому копировать есть что.

```csharp
        // Migration: money columns move from REAL to TEXT. Runs only on a register whose
        // Products was created before this landed — on a fresh database the schema block
        // above already declared TEXT and the probe below is a no-op.
        if (await DeclaredTypeAsync(connection, "Products", "Price") == "REAL")
        {
            await RebuildTableAsync(connection, "Products", @"
                CREATE TABLE Products_new (
                    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Sku TEXT, Category TEXT,
                    Price TEXT NOT NULL, OriginalPrice TEXT, DiscountPercent TEXT,
                    ImagePath TEXT, Barcode TEXT, Tags TEXT,
                    UnitId TEXT, UnitCode TEXT, UnitShortName TEXT, UnitFactor TEXT,
                    IsDivisible INTEGER, SellInSecondaryUnit INTEGER, SearchText TEXT,
                    StockQuantity TEXT
                );",
                // StockQuantity is deliberately absent from the copy list: the old table
                // has no such column, and NULL is the correct starting value anyway.
                "Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, "
                + "Barcode, Tags, UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, "
                + "SellInSecondaryUnit, SearchText",
                "CREATE INDEX IF NOT EXISTS IDX_Products_Category ON Products(Category);",
                "CREATE INDEX IF NOT EXISTS IDX_Products_Barcode ON Products(Barcode);");
        }

        if (await DeclaredTypeAsync(connection, "ParkedSales", "Total") == "REAL")
        {
            await RebuildTableAsync(connection, "ParkedSales", @"
                CREATE TABLE ParkedSales_new (
                    Id TEXT PRIMARY KEY, Label TEXT, CustomerName TEXT,
                    Total TEXT NOT NULL, ItemCount TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL
                );",
                "Id, Label, CustomerName, Total, ItemCount, CreatedAt, Payload");
        }

        if (await DeclaredTypeAsync(connection, "Sellers", "MaxDiscount") == "REAL")
        {
            await RebuildTableAsync(connection, "Sellers", @"
                CREATE TABLE Sellers_new (
                    Id TEXT PRIMARY KEY, FirstName TEXT NOT NULL, LastName TEXT,
                    PinHash TEXT, CanSell INTEGER NOT NULL DEFAULT 1,
                    CanRefund INTEGER NOT NULL DEFAULT 0,
                    CanCloseShift INTEGER NOT NULL DEFAULT 0,
                    MaxDiscount TEXT NOT NULL DEFAULT '0'
                );",
                "Id, FirstName, LastName, PinHash, CanSell, CanRefund, CanCloseShift, MaxDiscount");
        }

        // Belt and braces for a database that already has TEXT columns but predates
        // StockQuantity. Cannot happen from a released build — the two shipped together —
        // but a hand-migrated register is cheap to tolerate and expensive to debug.
        await AddColumnIfMissingAsync(command, "ALTER TABLE Products ADD COLUMN StockQuantity TEXT;");
```

- [ ] **Step 9: Прогнать тесты — оба обязаны позеленеть**

```bash
& ./run-tests.ps1
```

Ожидание: 759 passed (757 прежних плюс два новых).

- [ ] **Step 10: Мутация — проверить, что тесты не вакуумные**

Три отката, каждый по одному:

1. В блоке схемы вернуть `Price TEXT NOT NULL` → `Price REAL NOT NULL` и в перестройке тоже. Ожидание: **оба** новых теста красные. Вернуть.
2. Убрать из вызова `RebuildTableAsync` для `Products` две строки `CREATE INDEX`. Ожидание: тест перестройки красный на `Assert.Contains("IDX_Products_Category", found)`. Вернуть.
3. Заменить `== "REAL"` на `== "NOPE"` в пробе для `Products`. Ожидание: тест перестройки красный. Вернуть.

Если любая мутация оставила тесты зелёными — тест не сторожит то, ради чего написан. Чинить тест, а не идти дальше.

- [ ] **Step 11: Добавить тест на конкурентную инициализацию (долг Task 1)**

Теперь, когда перестройка внутри, гонка настоящая.

```csharp
    /// <summary>The lock Task 1 added earns its keep here: without it, two callers can
    /// both pass the _isInitialized check and both run the table rebuild, which means
    /// two DROP TABLE against the same rows.
    ///
    /// Probabilistic by nature — a race is not a deterministic failure. It fails often
    /// enough with the lock removed to be worth having, and it does not flake with the
    /// lock in place because the second caller simply waits.</summary>
    [Fact]
    public async Task InitializeAsync_CalledConcurrently_InitialisesOnceAndKeepsSchema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vvcash-concurrent-{Guid.NewGuid()}.db");
        try
        {
            var service = new OfflineStorageService(dbPath);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.InitializeAsync())));

            using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();
            Assert.Equal("TEXT", await DeclaredTypeAsync(check, "Products", "Price"));

            using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Products_new';";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }
```

Прогнать: `& ./run-tests.ps1`. Ожидание: 760 passed.

- [ ] **Step 12: Мутация замка**

Убрать `await _initLock.WaitAsync();` и `finally { _initLock.Release(); }` из Task 1, оставив тело как было. Прогнать тест 5 раз подряд:

```bash
& ./run-tests.ps1
```

Ожидание: тест конкурентности падает хотя бы в одном прогоне из пяти. Вернуть замок.

**Если он ни разу не упал** — тест не сторожит гонку. Тогда: либо поднять число параллельных вызовов с 8 до 32, либо признать честно, что теста нет, удалить его и записать это в раздел долга. Не оставлять зелёный тест, про который известно, что он ничего не проверяет.

- [ ] **Step 13: Коммит**

```bash
git add src/VvCash/Services/Data/OfflineStorageService.cs tests/VvCash.Tests/OfflineStorageServiceTest.cs
git commit -m "fix(storage): store money as TEXT so the column stops rounding it to a float"
```

---

## Task 3: Модель и хранилище узнают про остаток

Подготовка к сверке: колонка уже есть после Task 2, теперь её надо читать, писать и отдавать наружу.

**Files:**
- Modify: `src/VvCash/Models/Product.cs`
- Modify: `src/VvCash/Services/Data/OfflineStorageService.cs` — четыре SELECT (`:380`, `:398`, `:421`, `:442`), `ReadProduct`, новый метод
- Modify: `src/VvCash/Services/Data/IOfflineStorageService.cs`
- Test: `tests/VvCash.Tests/OfflineStorageServiceTest.cs`

- [ ] **Step 1: Написать падающий тест на применение сверки**

```csharp
    /// <summary>The reconciliation contract in one test: products the walk did not see
    /// at all are deleted, products it saw keep their row and gain a quantity, and a
    /// zero quantity is a value like any other — not a reason to delete.</summary>
    [Fact]
    public async Task ApplyRemainsAsync_DeletesUnseenProductsAndStampsQuantities()
    {
        await _service.InitializeAsync();
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "in-stock", Name = "Есть", Price = 10m },
            new Product { Id = "zero", Name = "Ноль", Price = 20m },
            new Product { Id = "withdrawn", Name = "Снят", Price = 30m },
        });

        await _service.ApplyRemainsAsync(new Dictionary<string, decimal>
        {
            ["in-stock"] = 7.5m,
            ["zero"] = 0m,
        });

        var all = (await _service.GetAllProductsAsync()).ToList();

        Assert.DoesNotContain(all, p => p.Id == "withdrawn");
        Assert.Equal(7.5m, all.Single(p => p.Id == "in-stock").StockQuantity);
        Assert.Equal(0m, all.Single(p => p.Id == "zero").StockQuantity);
        Assert.True(all.Single(p => p.Id == "zero").IsOutOfStock);
        Assert.False(all.Single(p => p.Id == "in-stock").IsOutOfStock);
    }
```

- [ ] **Step 2: Прогнать — обязан не собраться**

```bash
& ./run-tests.ps1
```

Ожидание: ошибка компиляции — нет `ApplyRemainsAsync`, нет `Product.StockQuantity`, нет `IsOutOfStock`.

Это красный от компиляции, он ничего не доказывает про сам тест. Настоящая проверка — мутация в Step 8.

- [ ] **Step 3: Добавить свойства в `Product`**

В `src/VvCash/Models/Product.cs`, рядом с остальными:

```csharp
    /// <summary>Stock for this register's warehouse as of the last complete
    /// reconciliation walk, or null when no walk has completed yet.
    ///
    /// Null and zero are different answers and must not be collapsed: null is "nobody
    /// has checked", zero is "checked, and there is none". Only the second one puts a
    /// badge on the tile.</summary>
    public decimal? StockQuantity { get; set; }

    /// <summary>Whether the register knows this product to be out of stock. Deliberately
    /// false for null — a register that has never reconciled must behave exactly as it
    /// did before reconciliation existed.</summary>
    public bool IsOutOfStock => StockQuantity == 0m;
```

- [ ] **Step 4: Вынести список колонок в константу и добавить `StockQuantity`**

Четыре SELECT в `OfflineStorageService.cs` (строки 380, 398, 421, 442) перечисляют один и тот же список колонок дословно. Добавлять шестнадцатую колонку в четыре копии — ровно тот способ, которым они разъедутся.

Завести константу рядом с `SearchTextOf`:

```csharp
    /// <summary>The column list every product SELECT shares, in the order ReadProduct
    /// reads by ordinal. One constant because four copies of the same list is how the
    /// fifth one ends up different.</summary>
    private const string ProductColumns =
        "Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode, Tags, "
        + "UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, SellInSecondaryUnit, StockQuantity";
```

Все четыре запроса переписать через неё:

```csharp
// :380
command.CommandText = $"SELECT {ProductColumns} FROM Products";
// :398
command.CommandText = $"SELECT {ProductColumns} FROM Products WHERE Category = $Category";
// :421
command.CommandText = $"SELECT {ProductColumns} FROM Products WHERE SearchText LIKE $Query ESCAPE '\\'";
// :442
command.CommandText = $"SELECT {ProductColumns} FROM Products WHERE Barcode = $Barcode LIMIT 1";
```

Осторожно с `:421`: там внутри строки уже есть `\\`, и при переходе на интерполяцию его надо сохранить как есть. Строка не `@`-литерал, поэтому `\\` остаётся `\\`.

- [ ] **Step 5: Прочитать новую колонку в `ReadProduct`**

В конец инициализатора, ординал 16:

```csharp
            SellInSecondaryUnit = !reader.IsDBNull(15) && reader.GetBoolean(15),
            // Ordinal 16, matching ProductColumns. NULL for a register that has never
            // completed a reconciliation walk.
            StockQuantity = reader.IsDBNull(16) ? null : reader.GetDecimal(16),
```

- [ ] **Step 6: Добавить метод применения сверки**

В `IOfflineStorageService.cs`, рядом с `ClearProductsAsync`:

```csharp
    /// <summary>Applies one complete reconciliation walk: products absent from
    /// <paramref name="remains"/> are deleted, the rest have their stock stamped.
    ///
    /// Only ever call this with the result of a walk that finished. A partial map means
    /// a partial delete, and a partial delete of the catalogue is worse than a stale
    /// one.</summary>
    Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains);
```

В `OfflineStorageService.cs`:

```csharp
    public async Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        // A temp table rather than a giant IN (...) list: the catalogue runs to
        // thousands of rows and SQLite caps host parameters well below that.
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS RemainSeen (Id TEXT PRIMARY KEY, Qty TEXT NOT NULL);"
                            + "DELETE FROM RemainSeen;";
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT OR REPLACE INTO RemainSeen (Id, Qty) VALUES ($Id, $Qty);";
            var idParam = cmd.Parameters.Add("$Id", SqliteType.Text);
            var qtyParam = cmd.Parameters.Add("$Qty", SqliteType.Text);
            foreach (var (id, qty) in remains)
            {
                idParam.Value = id;
                qtyParam.Value = qty;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                DELETE FROM Products WHERE Id NOT IN (SELECT Id FROM RemainSeen);
                UPDATE Products SET StockQuantity = (SELECT Qty FROM RemainSeen WHERE RemainSeen.Id = Products.Id);
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
```

- [ ] **Step 7: Догнать шесть тестовых заглушек интерфейса**

Интерфейс вырос, поэтому проект тестов перестанет собираться, пока каждая заглушка не получит новый метод. Реализующих ровно шесть (седьмой файл, `OfflineStorageServiceTest.cs`, работает с настоящим сервисом):

```bash
grep -rln "IOfflineStorageService" tests/VvCash.Tests/ --include="*.cs"
```

Ожидание: `CashFeatureServiceTest`, `ExpenseDocumentServiceTest`, `OfflineStorageServiceTest`, `PosViewModelSellerGateTest`, `SellerRosterServiceTest`, `SettingsViewModelTest`, `SyncServiceTest`.

В пять заглушек (`CashFeatureServiceTest`, `ExpenseDocumentServiceTest`, `PosViewModelSellerGateTest`, `SellerRosterServiceTest`, `SettingsViewModelTest`) добавить по одной строке:

```csharp
        public Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains) => Task.CompletedTask;
```

`SyncServiceTest.FakeStorage` получит записывающую версию в Task 5 — пока и туда положить эту же однострочную, чтобы сборка прошла.

Если в каком-то файле не хватает `using System.Collections.Generic;` — дописать.

- [ ] **Step 8: Прогнать — тест обязан позеленеть**

```bash
& ./run-tests.ps1
```

Ожидание: 761 passed.

Если посыпались другие тесты `OfflineStorageServiceTest` — почти наверняка разъехались ординалы: `ProductColumns` и `ReadProduct` должны перечислять колонки в одном порядке.

- [ ] **Step 9: Мутация**

1. Убрать `DELETE FROM Products WHERE Id NOT IN ...`. Ожидание: красный на `Assert.DoesNotContain`. Вернуть.
2. Заменить `IsOutOfStock => StockQuantity == 0m` на `=> false`. Ожидание: красный на `Assert.True(...IsOutOfStock)`. Вернуть.
3. Заменить `StockQuantity == 0m` на `StockQuantity <= 0m` — ожидание: **зелёный**, поведение то же. Это не дефект теста: отрицательного остатка бэкенд не отдаёт. Вернуть и идти дальше.

- [ ] **Step 10: Коммит**

```bash
git add src/VvCash/Models/Product.cs src/VvCash/Services/Data/OfflineStorageService.cs src/VvCash/Services/Data/IOfflineStorageService.cs \
        tests/VvCash.Tests/OfflineStorageServiceTest.cs \
        tests/VvCash.Tests/CashFeatureServiceTest.cs tests/VvCash.Tests/ExpenseDocumentServiceTest.cs \
        tests/VvCash.Tests/PosViewModelSellerGateTest.cs tests/VvCash.Tests/SellerRosterServiceTest.cs \
        tests/VvCash.Tests/SettingsViewModelTest.cs tests/VvCash.Tests/SyncServiceTest.cs
git commit -m "feat(storage): let the catalogue carry stock and drop what the warehouse no longer has"
```

---

## Task 4: Обходчик `/cashes/remain/`

Находка #6, первая половина. Обход отдельно от применения: половина ценности этой находки — в том, чтобы **не** применить неполный результат.

**Конверт эндпоинта — не такой, как у остальных кассовых:**

```json
{ "body": [ ... ], "page_count": 3, "total_items": 250, "item_per_page": 100 }
```

**Поля `status` нет.** Обходчик, написанный по образцу `SyncProductsAsync` с проверкой `status == 0`, молча не найдёт ничего. Терминатор цикла — `page_count`.

**Files:**
- Create: `src/VvCash/Models/Api/CashRemainItem.cs`
- Modify: `src/VvCash/Services/Data/SyncService.cs`
- Test: `tests/VvCash.Tests/SyncServiceTest.cs` (дописать в существующий класс)

- [ ] **Step 1: Создать DTO**

`src/VvCash/Models/Api/CashRemainItem.cs`:

```csharp
using System.Text.Json.Serialization;

namespace VvCash.Models.Api;

/// <summary>One row of GET /cashes/remain/ — a stock line for this register's
/// warehouse. Only the two fields reconciliation needs are mapped; the endpoint also
/// returns a name, barcode and article, but reconciliation never inserts products, so
/// there is nothing to build them from and no reason to carry them.</summary>
public class CashRemainItem
{
    [JsonPropertyName("product_id")] public string ProductId { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
}

/// <summary>The paginated envelope. Note the absence of a "status" field: this endpoint
/// answers with response.List, not the {status, body} shape the rest of the cash API
/// uses, so there is no status to check and page_count is what ends the walk.</summary>
public class CashRemainPage
{
    [JsonPropertyName("body")] public List<CashRemainItem>? Body { get; set; }
    [JsonPropertyName("page_count")] public int PageCount { get; set; }
    [JsonPropertyName("total_items")] public int TotalItems { get; set; }
}
```

Дописать `using System.Collections.Generic;` в шапку.

- [ ] **Step 2: Написать падающие тесты**

Дописать в существующий класс `SyncServiceTest` (файл `tests/VvCash.Tests/SyncServiceTest.cs`). Заглушка считает запросы прямо в лямбде — менять `StubHttpMessageHandler` не нужно.

Готовый хелпер `Build(handler, storage)` уже собирает `SyncService` со всеми четырьмя зависимостями, поэтому свои заглушки заводить не надо.

```csharp
    private const string Page1 =
        """{"body":[{"product_id":"a","quantity":5},{"product_id":"b","quantity":0}],"page_count":2,"total_items":3}""";
    private const string Page2 =
        """{"body":[{"product_id":"c","quantity":2.25}],"page_count":2,"total_items":3}""";

    /// <summary>The walk has to follow page_count, not stop at the first page. The
    /// request count assertion is not decoration: with a single-page stub the loop never
    /// iterates, and a partial-failure test written against it would be green without
    /// exercising anything.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_WalksEveryPage()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return (HttpStatusCode.OK, requests == 1 ? Page1 : Page2);
        });

        var result = await Build(handler, new FakeStorage()).FetchAllRemainsAsync();

        Assert.True(requests >= 2, $"expected the walk to request more than one page, saw {requests}");
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal(5m, result["a"]);
        Assert.Equal(0m, result["b"]);
        Assert.Equal(2.25m, result["c"]);
    }

    /// <summary>The most important test in the batch. A walk that breaks partway must
    /// return null, because the caller deletes everything the map does not mention — and
    /// half a map means half a catalogue deleted.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_SecondPageFails_ReturnsNull()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return requests == 1
                ? (HttpStatusCode.OK, Page1)
                : (HttpStatusCode.InternalServerError, "boom");
        });

        var result = await Build(handler, new FakeStorage()).FetchAllRemainsAsync();

        Assert.True(requests >= 2, $"expected the walk to reach the second page, saw {requests}");
        Assert.Null(result);
    }

    /// <summary>A transport failure is the offline case, and it is not an error worth
    /// throwing out of a background sync loop.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_TransportThrows_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("no network"));

        Assert.Null(await Build(handler, new FakeStorage()).FetchAllRemainsAsync());
    }

    private sealed class ThrowingHandler { }   // не нужен: см. строку выше
```

Последнюю строку в файл не переносить — она здесь только чтобы отметить, что отдельный обработчик не нужен: `StubHttpMessageHandler` принимает лямбду, и лямбда вправе бросить.

Проверить два предусловия до запуска:

- `FakeSettings.BackendUrl` в `SyncServiceTest.cs:18` должен возвращать непустой URL с завершающим слэшем. Пустой строкой `FetchAllRemainsAsync` вернёт `null` не дойдя до сети, и все три теста станут зелёными ни от чего — то есть вакуумными. Если там пусто, поправить на `"https://backend.test/"`.
- В шапке файла должны быть `using System.Net;` и `using System.Net.Http;`. Если нет — дописать.

- [ ] **Step 3: Прогнать — обязано не собраться**

```bash
& ./run-tests.ps1
```

Ожидание: нет метода `FetchAllRemainsAsync`.

- [ ] **Step 4: Реализовать обход**

В `ISyncService`:

```csharp
    /// <summary>Every stock line for this register's warehouse, or null when the walk
    /// did not complete. Null is not "empty": it means the caller must change nothing.</summary>
    Task<IReadOnlyDictionary<string, decimal>?> FetchAllRemainsAsync();
```

В `SyncService`:

```csharp
    /// <summary>Walks GET /cashes/remain/ page by page and returns product id to
    /// quantity for the whole warehouse.
    ///
    /// Returns null on any incomplete walk — a non-2xx, an unparseable page, a transport
    /// failure, being offline. The caller deletes every product this map does not
    /// mention, so a half-finished walk is not a smaller answer, it is a wrong one.
    ///
    /// This endpoint answers with response.List — {body, page_count, total_items,
    /// item_per_page} — and carries no "status" field, unlike the rest of the cash API.
    /// page_count is what ends the loop.</summary>
    public async Task<IReadOnlyDictionary<string, decimal>?> FetchAllRemainsAsync()
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrEmpty(baseUrl)) return null;

        var collected = new Dictionary<string, decimal>();

        try
        {
            var page = 1;
            var pageCount = 1;

            while (page <= pageCount)
            {
                var url = $"{baseUrl}cashes/remain/?page={page}&page_size=200";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[SyncService] remain page {page} -> {(int)response.StatusCode}; walk abandoned");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var parsed = JsonSerializer.Deserialize<Models.Api.CashRemainPage>(content);
                if (parsed == null)
                {
                    Console.WriteLine($"[SyncService] remain page {page} did not parse; walk abandoned");
                    return null;
                }

                foreach (var item in parsed.Body ?? new List<Models.Api.CashRemainItem>())
                    if (!string.IsNullOrEmpty(item.ProductId))
                        collected[item.ProductId] = item.Quantity;

                // Read on every page rather than once: a page count that shrinks mid-walk
                // still terminates, and one that grows is followed.
                pageCount = parsed.PageCount > 0 ? parsed.PageCount : 1;
                page++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] remain walk failed: {ex.Message}");
            return null;
        }

        return collected;
    }
```

- [ ] **Step 5: Прогнать — три теста обязаны позеленеть**

```bash
& ./run-tests.ps1
```

Ожидание: 764 passed.

- [ ] **Step 6: Мутация**

1. Заменить `while (page <= pageCount)` на `while (page <= 1)`. Ожидание: `FetchAllRemainsAsync_WalksEveryPage` красный на количестве. Вернуть.
2. Заменить `return null;` в ветке не-2xx на `break;`. Ожидание: `FetchAllRemainsAsync_SecondPageFails_ReturnsNull` красный — вернётся карта с одной страницей вместо null. Вернуть. **Это главная мутация батча.**
3. Заменить `return null` в `catch` на `return collected`. Ожидание: `FetchAllRemainsAsync_TransportThrows_ReturnsNull` красный. Вернуть.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Models/Api/CashRemainItem.cs src/VvCash/Services/Data/SyncService.cs tests/VvCash.Tests/SyncServiceTest.cs
git commit -m "feat(sync): walk the warehouse stock endpoint, and refuse a half-finished walk"
```

---

## Task 5: Сверка применяется

Соединение Task 3 и Task 4.

**Files:**
- Modify: `src/VvCash/Services/Data/SyncService.cs`
- Test: `tests/VvCash.Tests/SyncServiceTest.cs`

- [ ] **Step 1: Написать падающие тесты**

```csharp
    [Fact]
    public async Task ReconcileRemainsAsync_CompleteWalk_AppliesIt()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return (HttpStatusCode.OK, requests == 1 ? Page1 : Page2);
        });
        var storage = new FakeStorage();

        await Build(handler, storage).ReconcileRemainsAsync();

        Assert.NotNull(storage.AppliedRemains);
        Assert.Equal(3, storage.AppliedRemains!.Count);
    }

    /// <summary>Nothing is applied from a walk that did not finish. Without this the
    /// register deletes every product that happened to live on a page the walk never
    /// reached.</summary>
    [Fact]
    public async Task ReconcileRemainsAsync_IncompleteWalk_AppliesNothing()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return requests == 1
                ? (HttpStatusCode.OK, Page1)
                : (HttpStatusCode.InternalServerError, "boom");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).ReconcileRemainsAsync();

        Assert.Null(storage.AppliedRemains);
    }

    /// <summary>A walk that finished and found no stock lines is not an error, but it is
    /// still not a reason to empty the catalogue. ApplyRemainsAsync refuses an empty map
    /// outright, so reaching it here would surface as an exception on a background thread
    /// with nobody watching — this asserts the caller never gets that far.</summary>
    [Fact]
    public async Task ReconcileRemainsAsync_CompleteButEmptyWalk_AppliesNothing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"body":[],"page_count":1,"total_items":0}"""));
        var storage = new FakeStorage();

        await Build(handler, storage).ReconcileRemainsAsync();

        Assert.Null(storage.AppliedRemains);
    }
```

В `FakeStorage` (`SyncServiceTest.cs:39`) добавить:

```csharp
        public IReadOnlyDictionary<string, decimal>? AppliedRemains { get; private set; }

        public Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains)
        {
            AppliedRemains = remains;
            return Task.CompletedTask;
        }
```

Это замена однострочной заглушки, положенной в Task 3 Step 7. Остальные пять заглушек интерфейса остаются однострочными: смотрят на записанное только эти два теста.

- [ ] **Step 2: Прогнать — обязано не собраться**

Ожидание: одна ошибка компиляции — у `SyncService` нет метода `ReconcileRemainsAsync`. Заглушки интерфейса уже догнаны в Task 3, так что других ошибок быть не должно; если они есть, какая-то заглушка была пропущена.

- [ ] **Step 3: Реализовать**

В `ISyncService`:

```csharp
    Task ReconcileRemainsAsync();
```

В `SyncService`:

```csharp
    /// <summary>Brings the local catalogue in line with the warehouse: products the
    /// warehouse has no stock line for are dropped, the rest get their quantity stamped.
    ///
    /// Does nothing at all when the walk did not complete. That is the whole safety
    /// property of this feature — see FetchAllRemainsAsync.
    ///
    /// Never inserts. GET /cashes/remain/ carries no units, tags or images, so a product
    /// built from it would be a cripple; new products arrive through the version sync.</summary>
    public async Task ReconcileRemainsAsync()
    {
        var remains = await FetchAllRemainsAsync();

        // Two different nothings, and only one of them is an error. Null is "the walk did
        // not finish" — the safety property this whole feature turns on. An empty map is
        // "the walk finished and the warehouse has no stock lines at all", which is
        // legitimate but still must not empty the catalogue: ApplyRemainsAsync refuses it
        // outright, so the check has to happen here rather than being discovered as an
        // exception on a background thread nobody is watching.
        if (remains == null || remains.Count == 0)
        {
            Console.WriteLine(remains == null
                ? "[SyncService] remain walk incomplete; catalogue left untouched"
                : "[SyncService] remain walk returned no stock lines; catalogue left untouched");
            return;
        }

        await _storageService.ApplyRemainsAsync(remains);
        ProductsSynced?.Invoke(this, EventArgs.Empty);
    }
```

- [ ] **Step 4: Прогнать**

```bash
& ./run-tests.ps1
```

Ожидание: 766 passed.

- [ ] **Step 5: Мутация**

Две мутации, по одной за раз.

1. Заменить `if (remains == null || remains.Count == 0) { ...; return; }` на `if (remains == null) remains = new Dictionary<string, decimal>();`. Ожидание: **оба** теста красные — `ReconcileRemainsAsync_IncompleteWalk_AppliesNothing` и `ReconcileRemainsAsync_CompleteButEmptyWalk_AppliesNothing`. Вернуть.
2. Убрать из условия только `|| remains.Count == 0`. Ожидание: красный ровно один — `ReconcileRemainsAsync_CompleteButEmptyWalk_AppliesNothing`. Вернуть.

Вторая мутация нужна отдельно: без неё первая доказывает лишь то, что обе ветки существуют, но не то, что проверка на пустоту делает работу сама по себе.

Это ровно тот баг, который стёр бы каталог кассы при обрыве связи.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/Services/Data/SyncService.cs tests/VvCash.Tests/
git commit -m "feat(sync): reconcile the catalogue against warehouse stock"
```

---

## Task 6: Часовая каденция

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:925-975`

- [ ] **Step 1: Добавить отсчёт рядом с существующими**

Рядом с `lastUpdateCheck`:

```csharp
            DateTime lastUpdateCheck = DateTime.Now - TimeSpan.FromMinutes(59);

            // Same trick, one minute instead of fifty-nine: the first reconciliation runs
            // shortly after login rather than immediately, so it does not compete with the
            // first catalogue sync for the same connection, but the register does not wait
            // an hour to learn what is actually in stock.
            DateTime lastReconcile = DateTime.Now - TimeSpan.FromMinutes(59);
```

- [ ] **Step 2: Добавить ветку в цикл**

Сразу после существующей ветки проверки обновлений (`if (DateTime.Now - lastUpdateCheck >= TimeSpan.FromHours(1))`):

```csharp
                // Reconciliation walks the whole warehouse, so it runs on its own hourly
                // cadence rather than on SyncIntervalMinutes: at the default of ten
                // minutes that would be thousands of rows an hour over a shop's wifi for
                // an answer that changes slowly.
                if (DateTime.Now - lastReconcile >= TimeSpan.FromHours(1))
                {
                    lastReconcile = DateTime.Now;
                    await _syncService.ReconcileRemainsAsync();
                }
```

**Порядок присваивания важен:** `lastReconcile` ставится **до** вызова, а не после. Иначе долгий обход отодвигает следующий на «час плюс длительность обхода», и при обходе, который отваливается по таймауту, ветка начинает молотить чаще, чем задумано.

- [ ] **Step 3: Собрать и прогнать**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
& ./run-tests.ps1
```

Ожидание: одно предупреждение, 766 passed.

Тестом эта ветка не покрывается: цикл сидит внутри `PosViewModel` и завязан на `DateTime.Now`. Записано честно, а не изображено покрытием.

- [ ] **Step 4: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs
git commit -m "feat(sync): reconcile stock hourly, off the catalogue sync cadence"
```

---

## Task 7: Плашка «нет по учёту»

**Files:**
- Modify: `src/VvCash/Views/PosView.axaml:333-343` (плитка товара)
- Modify: `src/VvCash/Assets/i18n/{ru,en,tg,uz,kk}.json`

- [ ] **Step 1: Проверить байты локалей до правки**

```bash
for f in ru en tg uz kk; do python -c "
d=open(r'src/VvCash/Assets/i18n/$f.json','rb').read()
print('$f', 'BOM', d[:3]==b'\xef\xbb\xbf', 'CRLF', d.count(b'\r\n'))"; done
```

Ожидание: у всех пяти `BOM True`. Запомнить числа CRLF — после правки они должны вырасти ровно на число добавленных строк.

- [ ] **Step 2: Добавить ключ во все пять локалей**

Ключ `OutOfStock`. Значения:

| Файл | Значение |
|---|---|
| `ru.json` | `"OutOfStock": "Нет по учёту"` |
| `en.json` | `"OutOfStock": "Out of stock"` |
| `tg.json` | `"OutOfStock": "Дар ҳисоб нест"` |
| `uz.json` | `"OutOfStock": "Hisobda yo'q"` |
| `kk.json` | `"OutOfStock": "Есепте жоқ"` |

Вставлять рядом с другими товарными ключами. **Не пересохранять файл целиком редактором, который снимет BOM.**

- [ ] **Step 3: Проверить байты после правки**

Повторить команду из Step 1. Ожидание: `BOM True` у всех пяти, CRLF вырос на 1 в каждом.

Дополнительно — что JSON вообще парсится:

```bash
for f in ru en tg uz kk; do python -c "
import json,io
d=json.load(io.open(r'src/VvCash/Assets/i18n/$f.json',encoding='utf-8-sig'))
print('$f', 'OutOfStock' in d, d.get('OutOfStock'))"; done
```

- [ ] **Step 4: Добавить плашку на плитку**

В `PosView.axaml`, внутри `<Panel Grid.Row="0" ...>` плитки товара, сразу после существующего `<Border>` со скидкой:

```xml
                                                    <Border IsVisible="{Binding IsOutOfStock}"
                                                            Background="{StaticResource Slate900Brush}" CornerRadius="4" Padding="6,2"
                                                            HorizontalAlignment="Left" VerticalAlignment="Top" Margin="8" Opacity="0.85">
                                                        <TextBlock Text="{Binding [OutOfStock], Source={x:Static services:I18nService.Instance}}"
                                                                   Foreground="White" FontSize="11" FontWeight="Black"/>
                                                    </Border>
```

Слева вверху, чтобы не столкнуться со скидочной плашкой справа.

- [ ] **Step 5: Сверить привязку глазами**

Привязки здесь не компилируемые: неверный путь соберётся чисто и молча ничего не покажет.

Проверить по объявляющему типу:
- `IsOutOfStock` — есть ли в `src/VvCash/Models/Product.cs` как публичное свойство? (добавлено в Task 3, Step 3)
- `DataType` шаблона — `models:Product`? Да, `PosView.axaml:325`.
- Пространство `services:` объявлено в шапке `PosView.axaml`? Проверить `xmlns:services=`.

- [ ] **Step 6: Собрать, прогнать и посмотреть глазами**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
& ./run-tests.ps1
```

Ожидание: одно предупреждение, 766 passed.

Плашка тестами не покрыта — только глазами, при ручном проходе из раздела приёмки.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Views/PosView.axaml src/VvCash/Assets/i18n/
git commit -m "feat(pos): mark a product the warehouse says is out of stock"
```

---

## Task 8: Кредитный лимит во вью-модели

Находка #8.

**Правило:** продажа в долг разрешена, пока `currentBalance − долг ≥ −creditLimit`, где долг = `TotalAmount − (CashAmount + CardAmount)`. Баланс отрицательный означает долг: на бэкенде `current_balance = debit − credit`, а `credit` — это то, что клиент должен.

`null` читается как `0` — ровно как `COALESCE(c.credit_limit, 0)` в запросе бэкенда. Заказчик подтвердил: `0` означает «кредит запрещён», лимит проставляют осознанно.

**Files:**
- Modify: `src/VvCash/ViewModels/MixedPaymentViewModel.cs:79-180`
- Test: `tests/VvCash.Tests/MixedPaymentViewModelTest.cs`

- [ ] **Step 1: Написать падающие тесты**

```csharp
    private static MixedPaymentViewModel Credit(decimal total, decimal? limit, decimal? balance)
        => new(total, (_, _, _) => { }, allowMixed: true, hasCustomer: true,
               creditLimit: limit, currentBalance: balance);

    [Fact]
    public void SellOnCredit_ExactlyAtTheLimit_IsAllowed()
    {
        // Owes 400 already, limit 500, this sale adds 100 -> lands exactly on -500.
        var vm = Credit(100m, limit: 500m, balance: -400m);
        Assert.True(vm.SellOnCreditCommand.CanExecute(null));
    }

    [Fact]
    public void SellOnCredit_OneCentOverTheLimit_IsBlocked()
    {
        var vm = Credit(100.01m, limit: 500m, balance: -400m);
        Assert.False(vm.SellOnCreditCommand.CanExecute(null));
    }

    /// <summary>A null limit arrives as COALESCE(credit_limit, 0) does on the wire, and
    /// zero means credit is not allowed for this customer — not that it is unlimited.</summary>
    [Fact]
    public void SellOnCredit_NoLimitSet_BlocksAnyDebt()
    {
        var vm = Credit(1m, limit: null, balance: 0m);
        Assert.False(vm.SellOnCreditCommand.CanExecute(null));
    }

    /// <summary>Nothing is being lent, so the limit has nothing to say. Guards against
    /// deriving the debt from TotalAmount instead of from what is still owed.</summary>
    [Fact]
    public void SellOnCredit_FullyTendered_IsAllowedRegardlessOfLimit()
    {
        var vm = Credit(100m, limit: 0m, balance: -9999m);
        vm.CashAmount = 100m;
        Assert.True(vm.SellOnCreditCommand.CanExecute(null));
    }

    /// <summary>The button has to re-evaluate as the cashier types. Without
    /// NotifyCanExecuteChanged in NotifyDerived the rule is computed once, on a screen
    /// whose amounts change constantly, and the block works only some of the time.</summary>
    [Fact]
    public void SellOnCredit_ReevaluatesAsAmountsChange()
    {
        var vm = Credit(200m, limit: 100m, balance: 0m);
        Assert.False(vm.SellOnCreditCommand.CanExecute(null));

        vm.CashAmount = 150m;   // debt drops to 50, inside the limit

        Assert.True(vm.SellOnCreditCommand.CanExecute(null));
    }
```

- [ ] **Step 2: Прогнать — обязано не собраться**

Ожидание: у конструктора нет параметров `creditLimit`/`currentBalance`.

- [ ] **Step 3: Расширить конструктор и добавить поля**

```csharp
    /// <summary>The customer's credit ceiling as the server reports it, and their current
    /// balance. Passed as plain decimals rather than the CounterpartyResponse they came
    /// from: this view model knows nothing about API models and should not start.
    ///
    /// Null reads as zero, which is how the server sends an unset limit anyway —
    /// SearchCounterparties selects COALESCE(c.credit_limit, 0). Zero means credit is not
    /// allowed for this customer.</summary>
    private readonly decimal _creditLimit;
    private readonly decimal _currentBalance;

    public MixedPaymentViewModel(
        decimal totalAmount,
        Action<bool, decimal, decimal> onCompletion,
        bool allowMixed = true,
        bool hasCustomer = false,
        decimal? creditLimit = null,
        decimal? currentBalance = null)
    {
        TotalAmount = totalAmount;
        _onCompletion = onCompletion;
        _allowMixed = allowMixed;
        HasCustomer = hasCustomer;
        _creditLimit = creditLimit ?? 0m;
        _currentBalance = currentBalance ?? 0m;
        RecomputeQuickAmounts();
    }
```

- [ ] **Step 4: Добавить правило и выставить его наружу**

Рядом с `RemainingDue`:

```csharp
    /// <summary>What would be lent if the cashier hit "sell on credit" right now: what is
    /// still owed on this receipt. Derived the same way PosViewModel derives Remained, so
    /// the two cannot disagree about the size of the debt they are booking.</summary>
    public decimal CreditDebt => RemainingDue;

    /// <summary>Where the customer's balance lands if this sale goes on credit.</summary>
    public decimal ProjectedBalance => _currentBalance - CreditDebt;

    /// <summary>Whether the debt fits inside the ceiling. A balance is negative when the
    /// customer owes us, so the ceiling is -limit and the test is "not below it".
    ///
    /// Nothing on the server enforces this: credit_limit is stored and serialised and
    /// never compared, in documents/ or anywhere else. If this does not stop the sale,
    /// nothing does.</summary>
    public bool IsWithinCreditLimit => ProjectedBalance >= -_creditLimit;

    public decimal CreditLimitDisplay => _creditLimit;
    public decimal CurrentBalanceDisplay => _currentBalance;
    public bool IsCreditBlocked => HasCustomer && !IsWithinCreditLimit;
```

- [ ] **Step 5: Подключить к `CanSellOnCredit` и `NotifyDerived`**

```csharp
    private bool CanSellOnCredit() => HasCustomer && !_isSubmitting && IsWithinCreditLimit;
```

и в `NotifyDerived`, к существующим строкам:

```csharp
    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(PaidAmount));
        OnPropertyChanged(nameof(RemainingDue));
        OnPropertyChanged(nameof(ChangeAmount));
        OnPropertyChanged(nameof(IsFullyPaid));
        OnPropertyChanged(nameof(HasChange));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(CreditDebt));
        OnPropertyChanged(nameof(ProjectedBalance));
        OnPropertyChanged(nameof(IsWithinCreditLimit));
        OnPropertyChanged(nameof(IsCreditBlocked));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        // Not optional: the rule depends on the amounts, and this screen's amounts change
        // with every keypress. Without this line the block works only some of the time.
        SellOnCreditCommand.NotifyCanExecuteChanged();
        RecomputeQuickAmounts();
    }
```

И в `SellOnCredit` продублировать защиту, как это уже сделано для `ConfirmPayment`:

```csharp
    private void SellOnCredit()
    {
        if (!HasCustomer || _isSubmitting || !IsWithinCreditLimit) return;
        Submit();
    }
```

- [ ] **Step 6: Прогнать — пять тестов обязаны позеленеть**

```bash
& ./run-tests.ps1
```

Ожидание: 771 passed.

- [ ] **Step 7: Мутация**

1. `ProjectedBalance >= -_creditLimit` → `> -_creditLimit`. Ожидание: `SellOnCredit_ExactlyAtTheLimit_IsAllowed` красный. Вернуть.
2. `_creditLimit = creditLimit ?? 0m` → `?? decimal.MaxValue`. Ожидание: `SellOnCredit_NoLimitSet_BlocksAnyDebt` красный. Вернуть.
3. `CreditDebt => RemainingDue` → `=> TotalAmount`. Ожидание: `SellOnCredit_FullyTendered_IsAllowedRegardlessOfLimit` красный. Вернуть.
4. Убрать `SellOnCreditCommand.NotifyCanExecuteChanged();` из `NotifyDerived`. Ожидание: `SellOnCredit_ReevaluatesAsAmountsChange` красный. Вернуть.

Четвёртая — та самая строка, которая без своего теста ничем не защищена.

- [ ] **Step 8: Коммит**

```bash
git add src/VvCash/ViewModels/MixedPaymentViewModel.cs tests/VvCash.Tests/MixedPaymentViewModelTest.cs
git commit -m "fix(payment): stop lending past the customer's credit limit"
```

---

## Task 9: Кредитный лимит на экране

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:2361`
- Modify: `src/VvCash/Views/MixedPaymentView.axaml:293-302`
- Modify: `src/VvCash/Assets/i18n/{ru,en,tg,uz,kk}.json`

- [ ] **Step 1: Передать лимит и баланс на экран оплаты**

`PosViewModel.cs:2361`, было:

```csharp
            }, IsMixedPaymentEnabled, hasCustomer: SelectedCustomer != null);
```

стало:

```csharp
            }, IsMixedPaymentEnabled, hasCustomer: SelectedCustomer != null,
               creditLimit: SelectedCustomer?.CreditLimit,
               currentBalance: SelectedCustomer?.CurrentBalance);
```

- [ ] **Step 2: Добавить ключи во все пять локалей**

Сначала снять байтовый слепок, как в Task 7 Step 1.

| Ключ | ru | en |
|---|---|---|
| `CreditLimit` | `Кредитный лимит` | `Credit limit` |
| `CurrentBalance` | `Баланс` | `Balance` |
| `CreditLimitExceeded` | `Долг превысит лимит` | `Debt would exceed the limit` |

| Ключ | tg | uz | kk |
|---|---|---|---|
| `CreditLimit` | `Лимити қарз` | `Kredit limiti` | `Несие лимиті` |
| `CurrentBalance` | `Баланс` | `Balans` | `Баланс` |
| `CreditLimitExceeded` | `Қарз аз лимит зиёд мешавад` | `Qarz limitdan oshib ketadi` | `Қарыз лимиттен асады` |

Проверить байты и парсинг после правки, как в Task 7 Step 3.

- [ ] **Step 3: Показать цифры и причину отказа**

В `MixedPaymentView.axaml`, внутри `<StackPanel Grid.Row="7" Spacing="10">`, **перед** кнопкой «продать в долг»:

```xml
                        <StackPanel Spacing="4" IsVisible="{Binding HasCustomer}">
                            <Grid ColumnDefinitions="*,Auto">
                                <TextBlock Grid.Column="0" Text="{Binding [CurrentBalance], Source={x:Static services:I18nService.Instance}}"
                                           FontSize="13" Foreground="{StaticResource Slate500Brush}"/>
                                <TextBlock Grid.Column="1" Text="{Binding CurrentBalanceDisplay, StringFormat='{}{0:F2}'}"
                                           FontSize="13" FontWeight="SemiBold" Foreground="{StaticResource Slate700Brush}"/>
                            </Grid>
                            <Grid ColumnDefinitions="*,Auto">
                                <TextBlock Grid.Column="0" Text="{Binding [CreditLimit], Source={x:Static services:I18nService.Instance}}"
                                           FontSize="13" Foreground="{StaticResource Slate500Brush}"/>
                                <TextBlock Grid.Column="1" Text="{Binding CreditLimitDisplay, StringFormat='{}{0:F2}'}"
                                           FontSize="13" FontWeight="SemiBold" Foreground="{StaticResource Slate700Brush}"/>
                            </Grid>
                            <TextBlock IsVisible="{Binding IsCreditBlocked}"
                                       Text="{Binding [CreditLimitExceeded], Source={x:Static services:I18nService.Instance}}"
                                       FontSize="12" FontWeight="Bold" TextWrapping="Wrap"
                                       Foreground="{StaticResource Red600Brush}"/>
                        </StackPanel>
```

Кнопку не трогать: она гасится через `CanExecute`, который уже подключён в Task 8.

- [ ] **Step 4: Сверить пять привязок глазами**

Против `MixedPaymentViewModel`: `HasCustomer`, `CurrentBalanceDisplay`, `CreditLimitDisplay`, `IsCreditBlocked` — все четыре добавлены в Task 8 как публичные. Плюс три ключа локали — против `ru.json`.

Опечатка в любом из семи имён соберётся чисто и молча ничего не покажет.

- [ ] **Step 5: Собрать и прогнать**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
& ./run-tests.ps1
```

Ожидание: одно предупреждение, 771 passed.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs src/VvCash/Views/MixedPaymentView.axaml src/VvCash/Assets/i18n/
git commit -m "feat(payment): show the customer's balance and limit where the credit decision is made"
```

---

## Task 10: Построчная скидка доезжает до корзины

Находка #12.

**Главная ловушка:** `QuotedUnitDiscount` — **на единицу**, а `discount_amount` в ответе — **на строку**. `ExchangeViewModel.ApplyIssuedQuote` уже делит на количество с защитой от нуля; POS обязан повторить это ровно, иначе корзина и обмен покажут разные числа на одной скидке.

**Files:**
- Modify: `src/VvCash/Services/CartService.cs:290-294`
- Test: `tests/VvCash.Tests/CartServiceQuoteTest.cs`

- [ ] **Step 1: Написать падающие тесты**

Дописать в существующий класс `CartServiceQuoteTest`. В файле уже есть `CartWith(price, qty, promotions)` (`:11`), которая кладёт в корзину товар с id `"p1"`, и `QuoteWithLine(productId, unitPrice, discountTotal)` (`:224`). Вторая ставит только `ProductId` и `UnitPrice`, а этим тестам нужны ещё три поля, поэтому рядом заводится свой строитель.

`ApplyQuote` принимает **не**-nullable `QuoteResult`, так что `ApplyQuote(null)` не соберётся. Сброс идёт через публичный `ClearQuote()` (`CartService.cs:276`) — он и зовёт `ApplyQuotedPrices(null)`.

```csharp
    private static QuoteResult QuoteWithDiscountedLine(
        string productId, decimal quantity, decimal unitPrice,
        decimal discountAmount, decimal discountPercent) => new()
    {
        QuoteId = "q1",
        DiscountTotal = discountAmount,
        Lines =
        {
            new QuoteLineResult
            {
                ProductId = productId,
                Quantity = quantity,
                UnitPrice = unitPrice,
                DiscountAmount = discountAmount,
                DiscountPercent = discountPercent,
            },
        },
    };

    /// <summary>Per unit, not per line. The quote reports discount_amount for the whole
    /// line; CartItem.LineDiscount multiplies back up by quantity, so storing the line
    /// figure here would triple a three-unit discount on screen.</summary>
    [Fact]
    public void ApplyQuote_SplitsTheLineDiscountAcrossUnits()
    {
        var c = CartWith(100m, 3);

        c.ApplyQuote(QuoteWithDiscountedLine("p1", quantity: 3m, unitPrice: 100m,
                                             discountAmount: 30m, discountPercent: 10m));

        Assert.Equal(10m, c.Items[0].QuotedUnitDiscount);
        Assert.Equal(10m, c.Items[0].QuotedDiscountPercent);
        Assert.Equal(30m, c.Items[0].LineDiscount);
        Assert.True(c.Items[0].HasLineDiscount);
    }

    /// <summary>The same input has to produce the same number the exchange screen
    /// produces, because both render it as "what came off this line".</summary>
    [Fact]
    public void ApplyQuote_MatchesTheExchangeScreensArithmetic()
    {
        // Exactly what ExchangeViewModel.ApplyIssuedQuote computes for this line.
        var expected = 7m / 4m;

        var c = CartWith(25m, 4);
        c.ApplyQuote(QuoteWithDiscountedLine("p1", quantity: 4m, unitPrice: 25m,
                                             discountAmount: 7m, discountPercent: 7m));

        Assert.Equal(expected, c.Items[0].QuotedUnitDiscount);
    }

    /// <summary>Dropping the quote drops the cart back to cached prices. If the discount
    /// fields survive that, the badge outlives the promotion that justified it.</summary>
    [Fact]
    public void ClearQuote_ClearsTheDiscountFields()
    {
        var c = CartWith(100m, 2);
        c.ApplyQuote(QuoteWithDiscountedLine("p1", quantity: 2m, unitPrice: 90m,
                                             discountAmount: 20m, discountPercent: 10m));
        Assert.True(c.Items[0].HasLineDiscount);

        c.ClearQuote();

        Assert.Null(c.Items[0].QuotedUnitDiscount);
        Assert.Equal(0m, c.Items[0].QuotedDiscountPercent);
        Assert.False(c.Items[0].HasLineDiscount);
    }
```

- [ ] **Step 2: Прогнать — обязано покраснеть**

Ожидание: `QuotedUnitDiscount` равен `null` вместо ожидаемого числа.

- [ ] **Step 3: Заполнить поля**

`CartService.cs`, было:

```csharp
    private void ApplyQuotedPrices(QuoteResult? result)
    {
        foreach (var item in _items)
            item.QuotedUnitPrice = result?.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id)?.UnitPrice;
    }
```

стало:

```csharp
    private void ApplyQuotedPrices(QuoteResult? result)
    {
        foreach (var item in _items)
        {
            var line = result?.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
            item.QuotedUnitPrice = line?.UnitPrice;

            // Per unit, not per line — the same arithmetic ExchangeViewModel.ApplyIssuedQuote
            // does, deliberately kept identical: both screens render this as "what came off
            // this line", and two different answers to that is the defect, not a detail.
            // Null rather than zero when nothing priced the line, so the cart falls back to
            // "no discount" instead of "a discount of nothing".
            item.QuotedUnitDiscount = line != null && line.Quantity > 0
                ? line.DiscountAmount / line.Quantity
                : null;
            item.QuotedDiscountPercent = line?.DiscountPercent ?? 0m;
        }
    }
```

Сброс на провале котировки получается сам: `result` там `null`, значит `line` тоже `null`, значит оба поля обнуляются. Отдельной ветки не нужно — и третий тест это сторожит.

- [ ] **Step 4: Прогнать**

```bash
& ./run-tests.ps1
```

Ожидание: 774 passed.

- [ ] **Step 5: Мутация**

1. Убрать деление: `? line.DiscountAmount`. Ожидание: первые два теста красные. Вернуть.
2. `: null` → `: 0m` в тернарнике. Ожидание: `ClearQuote_ClearsTheDiscountFields` красный на `Assert.Null`. Вернуть.
3. Убрать защиту `line.Quantity > 0` — ожидание: **зелёный** (в тестах количество ненулевое). Это не дефект теста, а необходимая защита от деления на ноль. Вернуть и идти дальше.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/Services/CartService.cs tests/VvCash.Tests/CartServiceQuoteTest.cs
git commit -m "fix(cart): keep the per-line discount the quote already sent"
```

---

## Task 11: Построчная скидка на экране

**Files:**
- Modify: `src/VvCash/Views/PosView.axaml:443-481`
- Modify: `src/VvCash/Assets/i18n/{ru,en,tg,uz,kk}.json`

- [ ] **Step 1: Добавить ключ во все пять локалей**

Проверить, нет ли уже ключа `Discount` — им пользуется `ExchangeWindow.axaml`:

```bash
python -c "
import json,io
d=json.load(io.open(r'src/VvCash/Assets/i18n/ru.json',encoding='utf-8-sig'))
print('Discount' in d, d.get('Discount'))"
```

Если есть — переиспользовать, новых ключей не заводить. Если нет — добавить во все пять (`ru`: `Скидка`, `en`: `Discount`, `tg`: `Тахфиф`, `uz`: `Chegirma`, `kk`: `Жеңілдік`), с байтовой проверкой как в Task 7.

- [ ] **Step 2: Добавить строку скидки в шаблон корзины**

В `PosView.axaml`, в `<StackPanel Grid.Column="1" ...>` строки корзины, после `TextBlock` с `Product.Sku`:

```xml
                                                    <TextBlock FontSize="11" Foreground="{StaticResource Red500Brush}"
                                                               IsVisible="{Binding HasLineDiscount}">
                                                        <Run Text="{Binding [Discount], Source={x:Static services:I18nService.Instance}}"/><Run Text=": −"/><Run Text="{Binding LineDiscount, StringFormat='{}{0:N2}'}"/>
                                                    </TextBlock>
```

- [ ] **Step 3: Показать зачёркнутую сумму строки**

Колонку 3 — было:

```xml
                                                <TextBlock Grid.Column="3" Text="{Binding LineTotal, StringFormat='{}{0:F2}'}" FontSize="15" FontWeight="ExtraBold" Foreground="{StaticResource Slate900Brush}" TextAlignment="Right" VerticalAlignment="Center"/>
```

стало:

```xml
                                                <StackPanel Grid.Column="3" VerticalAlignment="Center">
                                                    <TextBlock Text="{Binding LineTotal, StringFormat='{}{0:F2}'}" FontSize="11"
                                                               Foreground="{StaticResource Slate400Brush}" TextDecorations="Strikethrough"
                                                               TextAlignment="Right" IsVisible="{Binding HasLineDiscount}"/>
                                                    <TextBlock Text="{Binding LineFinalTotal, StringFormat='{}{0:F2}'}" FontSize="15"
                                                               FontWeight="ExtraBold" Foreground="{StaticResource Slate900Brush}"
                                                               TextAlignment="Right"/>
                                                </StackPanel>
```

То же расположение, что в `ExchangeWindow.axaml:255-258`.

**Важно:** нижняя строка теперь `LineFinalTotal`, а не `LineTotal`. Без скидки они равны, так что для непроквотированной корзины ничего не меняется.

- [ ] **Step 4: Сверить привязки глазами**

Против `src/VvCash/Models/CartItem.cs`: `HasLineDiscount`, `LineDiscount`, `LineFinalTotal`, `LineTotal` — все четыре публичные производные свойства, все четыре объявлены. Плюс ключ `Discount` — против `ru.json`.

Заодно проверить, что `[NotifyPropertyChangedFor]` на `QuotedUnitDiscount` перечисляет `LineDiscount`, `LineFinalTotal` и `HasLineDiscount` — иначе строка не перерисуется, когда придёт котировка. В `CartItem.cs:41-43` они есть; убедиться, что не разъехались.

- [ ] **Step 5: Собрать и прогнать**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
& ./run-tests.ps1
```

Ожидание: одно предупреждение, 774 passed.

- [ ] **Step 6: Коммит**

```bash
git add src/VvCash/Views/PosView.axaml src/VvCash/Assets/i18n/
git commit -m "feat(pos): show the cart line what its discount was"
```

---

## Task 12: Кэш картинок получает границу

Находка #7.

**Files:**
- Create: `src/VvCash/Services/LruCache.cs`
- Modify: `src/VvCash/Services/ProductImageLoader.cs`
- Create: `tests/VvCash.Tests/ProductImageCacheTest.cs`

- [ ] **Step 1: Написать падающие тесты**

Кэш проверяется без сети и без Avalonia: вынести саму структуру в отдельный класс с явным потолком и тестировать её.

`tests/VvCash.Tests/ProductImageCacheTest.cs`:

```csharp
using System.Linq;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class ProductImageCacheTest
{
    [Fact]
    public void GetOrAdd_PastTheCap_EvictsTheLeastRecentlyUsed()
    {
        var cache = new LruCache<string, string>(capacity: 3);

        cache.GetOrAdd("a", _ => "A");
        cache.GetOrAdd("b", _ => "B");
        cache.GetOrAdd("c", _ => "C");
        cache.GetOrAdd("d", _ => "D");

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void GetOrAdd_TouchingAnEntry_MakesItTheNewest()
    {
        var cache = new LruCache<string, string>(capacity: 3);

        cache.GetOrAdd("a", _ => "A");
        cache.GetOrAdd("b", _ => "B");
        cache.GetOrAdd("c", _ => "C");
        cache.GetOrAdd("a", _ => "A2");   // touch, not replace
        cache.GetOrAdd("d", _ => "D");

        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("A", a);              // the factory did not run again
        Assert.False(cache.TryGet("b", out _));
    }

    /// <summary>Eviction must not dispose. The value a register evicts may be a Bitmap
    /// that a visible row is bound to right now; dropping the reference is the fix,
    /// disposing it is a blank tile or worse.</summary>
    [Fact]
    public void Eviction_HandsBackAValueThatIsStillUsable()
    {
        var cache = new LruCache<string, Probe>(capacity: 1);
        var first = cache.GetOrAdd("a", _ => new Probe());

        cache.GetOrAdd("b", _ => new Probe());

        Assert.False(cache.TryGet("a", out _));
        Assert.False(first.Disposed);
    }

    private sealed class Probe : System.IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
```

- [ ] **Step 2: Прогнать — обязано не собраться**

Ожидание: нет типа `LruCache`.

- [ ] **Step 3: Написать `LruCache`**

Новый файл `src/VvCash/Services/LruCache.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace VvCash.Services;

/// <summary>A bounded most-recently-used-wins map.
///
/// Dictionary plus LinkedList under a lock rather than ConcurrentDictionary: LRU needs
/// an order, and ConcurrentDictionary does not have one. The lock costs nothing here —
/// the only caller is image loading, which is already waiting on a socket.
///
/// Eviction drops the reference and nothing else. It deliberately does NOT dispose the
/// evicted value: for the image cache that value is a Bitmap which a visible row may
/// still be bound to, and disposing it under a live binding is a worse bug than the
/// unbounded growth this class exists to fix. Freeing is the GC's job, once nothing
/// holds it.</summary>
public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                Touch(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    /// <summary>The stored value for <paramref name="key"/>, calling
    /// <paramref name="factory"/> only when there is none. The factory runs under the
    /// lock, which is fine for what this cache holds: the image loader's factory starts
    /// a Task and returns it, it does not await one.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                Touch(existing);
                return existing.Value.Value;
            }

            var created = factory(key);
            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, created));
            _order.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }

            return created;
        }
    }

    /// <summary>Replaces the value for a key that is already present, leaving it newest.
    /// Adds it if it is absent.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _map.Remove(key);
            }
            var fresh = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, value));
            _order.AddFirst(fresh);
            _map[key] = fresh;

            while (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    private void Touch(LinkedListNode<KeyValuePair<TKey, TValue>> node)
    {
        _order.Remove(node);
        _order.AddFirst(node);
    }
}
```

- [ ] **Step 4: Прогнать — три теста обязаны позеленеть**

```bash
& ./run-tests.ps1
```

Ожидание: 777 passed.

- [ ] **Step 5: Мутация**

1. Убрать блок `while (_map.Count > _capacity) { ... }` из `GetOrAdd`. Ожидание: первый и третий тесты красные. Вернуть.
2. Убрать `Touch(existing)` из ветки попадания в `GetOrAdd`. Ожидание: `GetOrAdd_TouchingAnEntry_MakesItTheNewest` красный. Вернуть.
3. Добавить `(oldest.Value.Value as IDisposable)?.Dispose();` в вытеснение. Ожидание: `Eviction_HandsBackAValueThatIsStillUsable` красный. **Вернуть обязательно** — это и есть тот баг, от которого тест сторожит.

- [ ] **Step 6: Переключить `ProductImageLoader` на `LruCache`**

В `ProductImageLoader.cs`, было:

```csharp
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();
```

стало:

```csharp
    /// <summary>Three hundred thumbnails. A catalogue thumbnail around 200x200 costs
    /// roughly 160 KB decoded (width x height x 4), so the cap holds the cache near fifty
    /// megabytes — affordable on a register, and comfortably more than one screenful of
    /// the grid, so scrolling back and forth does not evict what was just shown.
    ///
    /// Bounded at all because a register runs for months without a restart and
    /// PosViewModel.Products is replaced wholesale on every category change: after that,
    /// the old Product objects are unreachable except through this cache, so an unbounded
    /// one pins every bitmap the shift ever displayed.</summary>
    private const int CacheCapacity = 300;

    private static readonly LruCache<string, Task<Bitmap?>> Cache = new(CacheCapacity);
```

И в `GetAsync`, было:

```csharp
        var task = Cache.GetOrAdd(url, u => FetchAsync(http, u));
        if (task.IsCompletedSuccessfully && task.Result == null)
        {
            task = FetchAsync(http, url);
            Cache[url] = task;
        }
        return task;
```

стало:

```csharp
        var task = Cache.GetOrAdd(url, u => FetchAsync(http, u));
        if (task.IsCompletedSuccessfully && task.Result == null)
        {
            // A cached null is a failed attempt, and the usual reason is the register
            // being briefly offline. Caching that permanently would leave the product
            // iconless for the rest of the shift, so a later ask tries again.
            task = FetchAsync(http, url);
            Cache.Set(url, task);
        }
        return task;
```

Убрать ставший ненужным `using System.Collections.Concurrent;`, если он больше нигде в файле не используется — иначе появится предупреждение, а базовая линия у нас одно.

- [ ] **Step 7: Собрать и прогнать**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
& ./run-tests.ps1
```

Ожидание: **одно** предупреждение, 777 passed.

- [ ] **Step 8: Коммит**

```bash
git add src/VvCash/Services/LruCache.cs src/VvCash/Services/ProductImageLoader.cs tests/VvCash.Tests/ProductImageCacheTest.cs
git commit -m "fix(images): bound the thumbnail cache a months-long shift never clears"
```

---

## Task 13: Финальная проверка батча

Батч B научил: самая дорогая находка нашлась не в задаче, а на финальном ревью всего батча. Откат USB-печати к заглушке оставлял все 756 тестов зелёными — у главной находки не было никакой защиты.

**Files:** правок нет, только проверки.

- [ ] **Step 1: Чистая полная сборка**

```bash
dotnet build src/VvCash/VvCash.csproj -o build/verify --no-incremental
```

Ожидание: **ровно одно** предупреждение — CS8601 в `PosViewModel.cs:2266`. Любое другое разобрать до конца, не списывать на «унаследованное».

- [ ] **Step 2: Полный прогон трижды**

```bash
& ./run-tests.ps1
```

Ожидание: 777 passed, три раза подряд.

Если упал случайный посторонний тест — посмотреть стек. Гонка Avalonia Dispatcher известна и к этому батчу отношения не имеет. Если упал новый тест конкурентной инициализации — это уже наш флак, и его надо либо укрепить, либо удалить с записью в долг.

- [ ] **Step 3: Мутационный обход всего батча**

Не повтор задачных мутаций, а проверка «есть ли у каждой находки хоть один сторож». По одной за раз, каждая возвращается перед следующей.

| Находка | Откат | Обязано покраснеть |
|---|---|---|
| #6 обход | `while (page <= pageCount)` → `while (page <= 1)` | `FetchAllRemainsAsync_WalksEveryPage` |
| #6 безопасность | неполный обход применяется вместо отказа | `ReconcileRemainsAsync_IncompleteWalk_AppliesNothing` |
| #9 типы | `Price TEXT` → `Price REAL` в схеме и перестройке | два теста `OfflineStorageServiceTest` |
| #9 индексы | убрать `CREATE INDEX` из перестройки | тест перестройки |
| #16 замок | убрать `_initLock` | тест конкурентности (до 5 прогонов) |
| #8 лимит | `>=` → `>` | `SellOnCredit_ExactlyAtTheLimit_IsAllowed` |
| #8 переоценка | убрать `SellOnCreditCommand.NotifyCanExecuteChanged()` | `SellOnCredit_ReevaluatesAsAmountsChange` |
| #12 деление | убрать `/ line.Quantity` | два теста `CartServiceQuoteTest` |
| #7 вытеснение | убрать блок `while (_map.Count > _capacity)` | два теста `ProductImageCacheTest` |

**Любая строка, где мутация оставила всё зелёным, означает находку без защиты.** Не идти дальше: либо дописать тест, либо записать отсутствие покрытия в раздел долга явным текстом.

- [ ] **Step 4: Проверить кодировки всех тронутых файлов**

```bash
for f in ru en tg uz kk; do python -c "
d=open(r'src/VvCash/Assets/i18n/$f.json','rb').read()
print('$f','BOM',d[:3]==b'\xef\xbb\xbf','LF',d.count(b'\n'),'CRLF',d.count(b'\r\n'))"; done
```

Ожидание: `BOM True` у всех пяти, LF равен CRLF (то есть одиночных LF нет).

```bash
for f in ru en tg uz kk; do python -c "
import json,io
d=json.load(io.open(r'src/VvCash/Assets/i18n/$f.json',encoding='utf-8-sig'))
for k in ['OutOfStock','CreditLimit','CurrentBalance','CreditLimitExceeded','Discount']:
    assert k in d, ('$f', k)
print('$f ok', len(d), 'keys')"; done
```

- [ ] **Step 5: Сверить каждую новую привязку против объявляющего типа**

Тестами это не ловится вообще. Одиннадцать привязок, добавленных батчем:

| Привязка | Объявлена в |
|---|---|
| `IsOutOfStock` | `Models/Product.cs` |
| `HasCustomer` | `ViewModels/MixedPaymentViewModel.cs` |
| `CurrentBalanceDisplay` | `ViewModels/MixedPaymentViewModel.cs` |
| `CreditLimitDisplay` | `ViewModels/MixedPaymentViewModel.cs` |
| `IsCreditBlocked` | `ViewModels/MixedPaymentViewModel.cs` |
| `HasLineDiscount` | `Models/CartItem.cs` |
| `LineDiscount` | `Models/CartItem.cs` |
| `LineFinalTotal` | `Models/CartItem.cs` |
| `[OutOfStock]` | `Assets/i18n/ru.json` |
| `[CreditLimit]`, `[CurrentBalance]`, `[CreditLimitExceeded]` | `Assets/i18n/ru.json` |
| `[Discount]` | `Assets/i18n/ru.json` |

Открыть объявляющий файл и убедиться глазами. Грепом по имени — не проверка: греп найдёт и то же слово в комментарии.

- [ ] **Step 6: Проверить, что в коммиты не заехало чужое**

```bash
git status --short
git log --oneline main..HEAD
git diff --stat main..HEAD
```

Ожидание: `build_deploy.ps1` по-прежнему `??` и ни в одном коммите не участвует. В диапазоне — двенадцать коммитов задач плюс коммит спеки.

- [ ] **Step 7: Ручной проход на приложении**

Собрать и запустить. Пройти:

1. Продажа в долг клиенту с достаточным лимитом — кнопка активна, баланс и лимит видны.
2. Тому же клиенту сумма больше лимита — кнопка гаснет, красным написана причина.
3. Клиент без лимита — любая продажа в долг заблокирована.
4. Строка корзины со скидкой от акции — видно «Скидка: −X» и зачёркнутую сумму; числа сходятся с экраном обмена на том же товаре.
5. Товар с нулевым остатком — на плитке плашка, товар при этом продаётся.
6. **Первый запуск на копии боевой БД.** Скопировать `%LOCALAPPDATA%\VvCash\offline_data.db` с работающей кассы, подложить, запустить. Каталог на месте, отложенные продажи на месте, `pragma_table_info` показывает TEXT. Без этого пункта находка #9 не считается закрытой.

- [ ] **Step 8: Сверить конверт живого эндпоинта**

Единственное, что не проверяется локально. Против дев-стенда:

```bash
curl -s -H "Authorization: Bearer $TOKEN" "$BACKEND/cashes/remain/?page=1&page_size=5" | head -c 400
```

Ожидание: `{"body":[...],"page_count":N,"total_items":M,"item_per_page":5}` и **отсутствие** поля `status`. Если `status` вдруг есть — обходчик из Task 4 его игнорирует, это безопасно; а вот если нет `page_count`, цикл сделает ровно одну страницу, и это надо чинить до выката.

---

## Что батч оставляет незакрытым

Не забытое, а решённое. Записано, чтобы не искать заново.

- **Находка #6 закрыта частично.** Снятый с продажи товар с уцелевшей строкой `remains` и ненулевым остатком не будет ни удалён, ни помечен. Причина — `GetStockRemains` не фильтрует `deleted_at`, а боевой код строки `remains` не удаляет. Закрывается одной строкой на бэкенде, отклонённой как правка второго репозитория. Подробности — в спеке.
- **Часовая ветка сверки в `PosViewModel` тестами не покрыта.** Цикл внутри вью-модели и завязан на `DateTime.Now`.
- **Плашка остатка, блок кредита и строка скидки не покрыты тестами на уровне разметки.** Привязки не компилируемые; проверка — только глазами, Step 5 задачи 13.
- **Идемпотентность перестройки утверждается как «второй прогон не портит данные»**, а не как «второй прогон ничего не делает». Второе требует шпиона, которого в тесте нет.
- **Замок инициализации (#16) не покрыт тестом вообще.** План предлагал вероятностный тест на N параллельных `InitializeAsync`; он был написан в Task 2 и **удалён там же**, потому что оказался вакуумным. Разобрано при исполнении, а не предположено:

  1. Тест открывал несуществующий файл БД. `InitializeCoreAsync` создаёт `Products` сразу с `Price TEXT`, поэтому проба `DeclaredTypeAsync(...) == "REAL"` ложна и `RebuildTableAsync` **не вызывается ни разу**. Комментарий теста обещал «два `DROP TABLE` по одним строкам» — перестройки там не было вовсе.
  2. Запасной вариант из плана (поднять параллелизм с 8 до 32) ничего не меняет: он повышает конкуренцию на пути, в который не заходят.
  3. Проверено сверх плана: с БД, засеянной в старой форме (перестройка реально идёт) и с барьером, удерживающим все вызовы в полёте, — пять прогонов со снятым замком, все зелёные.
  4. Причина, по которой упасть не может: `RebuildTableAsync` идемпотентна, а писательский лок SQLite сериализует транзакции вместо чередования. Второй вызов пересоздаёт `Products_new` из уже-TEXT `Products` и получает ту же таблицу с теми же строками. Выбранные утверждения (`Price == "TEXT"`, нет остатка `Products_new`) этой гонкой **неопровержимы в принципе**.

  Тест, который бы работал, должен утверждать не форму результата, а **сколько раз выполнилось тело** — то есть требует шпиона внутри `InitializeCoreAsync`. Это решение по дизайну тестов, план его не предусматривал, и наспех оно не принимается. Находка #16 закрыта кодом и не закрыта тестом; замок держится на рассуждении и на ревью, а не на прогоне.

- **Отложено ревью качества Task 2, три пункта.** Ни один не влияет на поведение; все три — про то, как код читается и как падает.
  - Три `Assert.True(await rd.ReadAsync())` в миграционных тестах без сообщения. Падение печатает `Assert.True() Failure` и ничего про то, какая строка не нашлась.
  - Схема продублирована: объявления колонок живут и в блоке `CREATE TABLE IF NOT EXISTS`, и в DDL перестройки. Разойтись они могут молча — сегодня их держит вместе только тест сравнения `pragma_table_info`.
  - Boilerplate временной БД в тестах повторён трижды. Общий `SeedPreMigrationDatabaseAsync` появился, но создание и уборка файла — нет.
- **`SaveParkedSaleAsync` связывает `decimal` через `AddWithValue`,** тогда как `Products` и `Sellers` перешли на явный `SqliteType.Text`. Работает одинаково — измерено дважды, — но расхождение стилей в одном файле читается как недоделка. Не трогалось намеренно: колонка уже `TEXT`, менять связывание без нужды значит рисковать ради симметрии. Сторожит `SaveAndGetParkedSale_RoundTripsEveryFieldExactly`.
- **Ветки перестройки `ParkedSales` и `Sellers` едва не уехали без покрытия.** На БД, засеянной только `Products`, `CREATE TABLE IF NOT EXISTS` создаёт остальные две сразу в TEXT, их пробы читают TEXT, и обе ветки — мёртвый код под тестом. Закрыто в Task 2 общим сидом всех трёх таблиц в старой форме плюс двумя тестами на сохранность строк. Оставлено здесь как предупреждение: **«тест засеял одну таблицу» и «тест прошёл» вместе не значат, что миграция проверена.**
