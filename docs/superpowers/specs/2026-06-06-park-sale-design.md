# Отложенный чек (Park Sale) — дизайн

**Дата:** 2026-06-06
**Статус:** утверждён к реализации

## Проблема

В шапке POS есть мёртвая кнопка «Заказы» ([PosView.axaml:39](../../../src/VvCash/Views/PosView.axaml)) — без команды, ни на что не реагирует. Кассиру нужна возможность **отложить** текущий чек (приостановить продажу), очистить экран для следующего клиента, а позже **вернуть** отложенный чек и завершить оплату.

Классический POS-сценарий: клиент забыл карту/передумал/ушёл за товаром — кассир откладывает его корзину и обслуживает очередь, не теряя набранное.

## Решения (зафиксированы при брейншторме)

| Вопрос | Решение |
|---|---|
| Хранение | Локально в SQLite (`offline_data.db`), переживает перезапуск/краш. Бэкенд не трогаем. |
| Количество | Много отложенных одновременно, список. |
| Закрытие смены при наличии отложенных | Предупредить, **не блокировать**. |
| Идентификация в списке | Авто (время, сумма, кол-во, имя клиента) + **опциональная** метка-примечание. |
| Возврат при непустой корзине | **Авто-отложить** текущую корзину, затем загрузить выбранную. |
| Подпись кнопки | «Отложенные» (новый ключ `ParkedSales`). |
| Тесты | Ручная проверка (тест-проекта в репо нет); сервис пишем тестопригодным. |

## Архитектура

Зеркалим существующий offline-паттерн: `ExpenseDocumentService.SaveOfflineAsync` сериализует объект в JSON и кладёт в таблицу `UnsyncedDocuments` через `OfflineStorageService`. Так же поступаем с отложенными чеками — новая таблица + сервис.

Слои:
- **Модели** — `ParkedSale` (строка списка) + `ParkedSaleSnapshot` (полезная нагрузка).
- **Хранилище** — `OfflineStorageService` получает таблицу `ParkedSales` и CRUD (он владеет SQLite-схемой).
- **Сервис** — новый `IParkedSaleService` / `ParkedSaleService` (singleton): сериализация, список, возврат, удаление, событие счётчика.
- **Корзина** — `ICartService` получает `LoadSnapshot(...)` для регидрации.
- **VM** — `PosViewModel` получает команды park/resume/open/delete + счётчик + предупреждение при закрытии смены.
- **UI** — оживить кнопку «Заказы»→«Отложенные», добавить кнопку «Отложить», окно `ParkedSalesWindow`, мини-модалку метки.

## 1. Модели данных

### `Models/ParkedSale.cs` — строка списка (денормализовано для UI)
```
Id          string   (guid)
Label       string?  (опц. примечание кассира)
CustomerName string? (имя клиента, если был выбран)
Total       decimal
ItemCount   int
CreatedAt   DateTime
Payload     string   (JSON ParkedSaleSnapshot)
```

### `Models/ParkedSaleSnapshot.cs` — полный снимок (внутри Payload)
```
Items                   List<ParkedCartItem>   // { Product, Quantity }
ManualDiscountPercent   decimal
ManualDiscountAmount    decimal
CustomerDiscountPercent decimal
AppliedCoupons          List<Coupon>
Customer                CounterpartyResponse?
Label                   string?
```

`ParkedCartItem` хранит **весь** `Product` (POCO), а не только Id — возврат работает офлайн и даже если товар позже убран из каталога. Цены фиксируются на момент отложения.

## 2. Хранилище — `OfflineStorageService`

Таблица создаётся в `InitializeAsync` рядом с остальными:
```sql
CREATE TABLE IF NOT EXISTS ParkedSales (
    Id TEXT PRIMARY KEY,
    Label TEXT,
    CustomerName TEXT,
    Total REAL NOT NULL,
    ItemCount INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    Payload TEXT NOT NULL
);
```
`CREATE TABLE IF NOT EXISTS` → безопасная миграция для существующих БД (как у других таблиц).

Новые методы в `IOfflineStorageService` / `OfflineStorageService` (зеркало методов `UnsyncedDocuments`):
- `Task SaveParkedSaleAsync(ParkedSale sale)` — upsert по `Id`.
- `Task<IEnumerable<ParkedSale>> GetParkedSalesAsync()` — все, сортировка по `CreatedAt DESC`.
- `Task<ParkedSale?> GetParkedSaleAsync(string id)`.
- `Task DeleteParkedSaleAsync(string id)`.

## 3. Сервис — `IParkedSaleService` / `ParkedSaleService`

Singleton, зависит от `IOfflineStorageService`. Сериализация через `System.Text.Json` (как `ExpenseDocumentService`).

```
Task<ParkedSale> ParkAsync(ParkedSaleSnapshot snapshot)
    // вычисляет Total/ItemCount/CustomerName, генерит Id+CreatedAt,
    // сериализует snapshot в Payload, сохраняет, поднимает CountChanged

Task<IReadOnlyList<ParkedSale>> GetAllAsync()

Task<ParkedSaleSnapshot?> ResumeAsync(string id)
    // грузит запись, десериализует Payload, УДАЛЯЕТ запись, поднимает CountChanged, возвращает snapshot

Task DeleteAsync(string id)        // + CountChanged
Task<int> GetCountAsync()
event EventHandler<int>? CountChanged
```

Возврат удаляет запись из отложенных (чек «вынут» в активную корзину).

DI: `services.AddSingleton<IParkedSaleService, ParkedSaleService>();` в `App.ConfigureServices`.

## 4. Корзина — `ICartService` / `CartService`

Добавить:
```
void LoadSnapshot(
    IEnumerable<CartItem> items,
    decimal manualDiscountPercent, decimal manualDiscountAmount,
    decimal customerDiscountPercent,
    IEnumerable<Coupon> coupons)
```
Очищает текущее содержимое, заливает переданное, поднимает `CartChanged` один раз. Клиент (`SelectedCustomer`) живёт в `PosViewModel` — его VM выставляет отдельно.

## 5. ViewModel — `PosViewModel`

Новое состояние:
- `[ObservableProperty] int _parkedSalesCount;` + `bool HasParkedSales => ParkedSalesCount > 0;` (бейдж).
- Подписка на `IParkedSaleService.CountChanged` (через `Dispatcher.UIThread.Post`), инициализация в `InitializeAsync`.
- Состояние мини-модалки метки: `bool IsParkLabelModalVisible`, `string ParkLabelInput`.

Хелпер сборки снимка из текущего состояния:
```
private ParkedSaleSnapshot BuildSnapshot(string? label) =>
    new() {
        Items = _cartService.Items.Select(i => new ParkedCartItem { Product = i.Product, Quantity = i.Quantity }).ToList(),
        ManualDiscountPercent = _cartService.ManualDiscountPercent,
        ManualDiscountAmount = _cartService.ManualDiscountAmount,
        CustomerDiscountPercent = _cartService.CustomerDiscountPercent,
        AppliedCoupons = _cartService.AppliedCoupons.ToList(),
        Customer = SelectedCustomer,
        Label = label
    };
```

Команды:
- **`OpenParkLabelModal`** — если корзина пуста → no-op; иначе очистить `ParkLabelInput`, показать модалку.
- **`ConfirmParkSale`** (из модалки, метка опциональна) — `await _parkedSaleService.ParkAsync(BuildSnapshot(label))`; затем очистить корзину/клиента (как `ClearCart`); закрыть модалку. (Кнопка «Пропустить» = подтвердить с пустой меткой.)
- **`OpenParkedSales`** — открыть `ParkedSalesWindow` (диалог по образцу `OpenCustomerSearch`); диалог сам обрабатывает «Вернуть»/«Удалить» и возвращает id для возврата (или null).
- **`ResumeParkedSale(string id)`**:
  1. Если `_cartService.Items.Any()` → `await _parkedSaleService.ParkAsync(BuildSnapshot(null))` (авто-отложить текущую), очистить.
  2. `var snap = await _parkedSaleService.ResumeAsync(id);` если null → выход.
  3. `_cartService.LoadSnapshot(snap.Items..., snap.ManualDiscount..., snap.CustomerDiscountPercent, snap.AppliedCoupons);`
  4. `SelectedCustomer = snap.Customer;` если у клиента есть карта-скидка → `_cartService.SetCustomerDiscount(...)`.

Закрытие смены — правка `CloseShiftAsync`: если `ParkedSalesCount > 0`, перед закрытием показать предупреждение «Есть N отложенных чеков. Закрыть смену?» (модалка подтверждения, **не блокирует**). Использовать существующий механизм `AlertMessage`/`IsAlertModalVisible` с расширением до confirm, либо отдельный флаг подтверждения.

## 6. UI

### `Views/PosView.axaml`
- **Кнопка «Заказы» → «Отложенные»** (line 39): `Command="{Binding OpenParkedSalesCommand}"`, текст из нового ключа `ParkedSales`. Добавить бейдж-счётчик (виден при `HasParkedSales`) — по образцу индикатора `HasUnsyncedDocuments`.
- **Кнопка «Отложить»** в зоне действий корзины (рядом с «Оплатить»/«Очистить»): `Command="{Binding OpenParkLabelModalCommand}"`, текст из ключа `Park`.
- **Мини-модалка метки** — оверлей по образцу discount-модалки (`IsDiscountModalVisible`): `TextBox` для `ParkLabelInput`, кнопки «Отложить» (`ConfirmParkSaleCommand`) и «Пропустить».

### `Views/ParkedSalesWindow.axaml` (+ `.axaml.cs`) и `ViewModels/ParkedSalesViewModel.cs`
По образцу `CustomerSearchWindow` / `CustomerSearchViewModel`:
- Список строк: метка **или** имя клиента (если метки нет), кол-во товаров, сумма, время (`CreatedAt`, формат `dd MMM HH:mm`).
- Кнопки на строке: **«Вернуть»** (закрывает диалог с id), **«Удалить»** (`DeleteAsync`, обновляет список).
- Пусто → текст `ParkedSalesEmpty`.
- VM зависит от `IParkedSaleService`.

## 7. i18n — новые ключи (ru/en/kk/uz/tg)

| Ключ | ru | en |
|---|---|---|
| `ParkedSales` | Отложенные | Parked |
| `Park` | Отложить | Hold |
| `Resume` | Вернуть | Resume |
| `Delete` | Удалить | Delete |
| `Skip` | Пропустить | Skip |
| `ParkLabelHint` | Примечание (необязательно) | Note (optional) |
| `ParkedSalesEmpty` | Нет отложенных чеков | No parked sales |
| `ShiftCloseParkedWarning` | Есть отложенные чеки. Закрыть смену? | Parked sales exist. Close shift? |

kk/uz/tg — перевести при реализации. Существующий ключ `Orders` («Заказы») остаётся, но кнопка переключается на `ParkedSales`.

## 8. Краевые случаи

- **Цена изменилась** после отложения → возврат с ценами на момент отложения (снимок их фиксирует). Документируем как ожидаемое поведение.
- **Купоны** восстанавливаются как есть, без ревалидации (были валидны при отложении).
- **Скидка по карте клиента** восстанавливается из снимка.
- **Пустая корзина** + «Отложить» → команда игнорируется.
- **Один терминал** → блокировки/конкуренция не требуются.
- **Большой каталог** — снимок хранит копии `Product`, объём на чек мал; не проблема.

## 9. Тесты / верификация

Тест-проекта в репозитории нет. MVP — ручная верификация:
1. Набрать корзину → «Отложить» (с меткой и без) → корзина очищается, счётчик «Отложенные» растёт.
2. Перезапустить приложение → отложенные на месте (SQLite).
3. «Отложенные» → «Вернуть» → корзина/скидки/клиент восстановлены, запись исчезла из списка.
4. С непустой корзиной вернуть отложенный → текущая авто-отложилась, выбранная загрузилась.
5. «Удалить» в списке → запись пропала.
6. Закрыть смену при наличии отложенных → предупреждение, после подтверждения смена закрывается, отложенные остаются.

`ParkedSaleService` и сериализация изолированы — при заведении тест-проекта легко покрыть round-trip и CRUD.

## Файлы

**Новые:** `Models/ParkedSale.cs`, `Models/ParkedSaleSnapshot.cs`, `Services/IParkedSaleService.cs`, `Services/ParkedSaleService.cs`, `ViewModels/ParkedSalesViewModel.cs`, `Views/ParkedSalesWindow.axaml` (+`.axaml.cs`).

**Правки:** `Services/Data/IOfflineStorageService.cs`, `Services/Data/OfflineStorageService.cs`, `Services/ICartService.cs`, `Services/CartService.cs`, `ViewModels/PosViewModel.cs`, `Views/PosView.axaml`, `App.axaml.cs` (DI), 5×`Assets/i18n/*.json`.
