# Сессия и сохранность данных (батч A код-ревью)

**Дата:** 2026-08-22
**Репозитории:** `vv-cash` (клиент). Бэкенд `cloudmarket-server` — только чтение, правок нет.
**Статус:** дизайн утверждён, ждёт ревью спеки

## Контекст

Код-ревью всей кассы выявило 18 находок. По риску они разрезаны на четыре
независимых батча, каждый со своей спекой и своим планом:

| Батч | Содержание | Почему отдельно |
|---|---|---|
| **A** (эта спека) | сессия и сохранность данных | проверяется юнит-тестами, чинит потерю выручки и тупик кассира |
| B | печать и железо | не верифицируется без принтера |
| C | синк и хранилище | требует продуктовых решений и миграции схемы |
| D | мелочи и чистота | не срочно, не блокирует |

Батч A закрывает три находки:

1. `ShiftService` не распознаёт 403 — авто-восстановление мёртвой сессии не работает никогда.
2. `CustomerDisplayWindow` накапливается при каждом входе и игнорирует фичефлаг.
3. Экран настроек одним нажатием безвозвратно стирает очередь непроведённых продаж.

---

## Проблема 1 — 403 на операциях смены

[`ShiftService`](../../../src/VvCash/Services/Api/ShiftService.cs) поднимает
`SessionRevoked` только на `HttpStatusCode.Unauthorized` (строки 109 и 216).
Бэкенд на протухший Bearer отдаёт **403**, а не 401:

```go
// cloudmarket-server/middlewares/utils.go:56
func redirectToAccessDenied(c *gin.Context) {
	c.AbortWithStatusJSON(http.StatusForbidden, gin.H{"status": "error", "message": "forbidden"})
}
```

401 приходит только с `login`/`refresh`.
[`ExpenseDocumentService.IsSessionRejected`](../../../src/VvCash/Services/Api/ExpenseDocumentService.cs)
это уже знает — `Unauthorized or Forbidden` — и документирует. `ShiftService`
из той же пары не обновили.

Последствие: `IShiftService.SessionRevoked` не срабатывает никогда,
`PosViewModel.OnShiftSessionRevoked` — мёртвый код, а кассир с протухшим токеном
упирается в модалку смены, которая не может открыться, и не понимает почему.

### Почему нельзя просто дописать `or Forbidden`

Все 403 на проводе неразличимы — один и тот же
`{"status":"error","message":"forbidden"}`. Источников как минимум шесть — маршруты
смены висят за цепочкой `SiteAuthentication → EnforceActiveTenant →
EnforceBillingAccess → CashAuthentication → CashAuthorization` (`routes/routes.go:194`),
и `redirectToAccessDenied` зовёт почти каждое звено:

| Источник | Причина | Характер |
|---|---|---|
| `site_authentication.go` | протухший/битый JWT | сессия мертва |
| `cash_authentication.go` | битый `Cash-Authorization` | конфигурация кассы |
| `tenantdb.GetPgxPool` вернул ошибку | падение пула tenant-БД | **транзиентная** |
| `active_tenant.go` | тенант заблокирован/удалён, либо сбой проверки статуса | биллинг или **транзиентная** |
| `cash_authorization.go` → `checkAccess` | нет `is_seller` на эту кассу | конфигурация прав |
| `authz` → `DenyAccess` | нет permission | конфигурация прав |

Точное число здесь неважно и намеренно не фиксируется: важно, что «сессия мертва» —
меньшинство случаев, а отличить их на клиенте нечем.

Слепой авто-разлогин по 403 выбросит кассира при моргании БД, а при
неправильных правах загонит в петлю: после релогина повторится то же самое.

### Решение

**Ассиметрия 401/403 сохраняется намеренно.**

- **401** однозначен. Поведение не трогаем — авто-выход, как сейчас. Сегодня
  ветка не срабатывает никогда, но останется верной, если бэкенд когда-нибудь
  разведут коды.
- **403** неоднозначен. Не разлогинивать; объяснить и дать решить кассиру.

`IShiftService` получает второе событие вместо аргумента-энума — обработчики
остаются в одну строку, `switch` не нужен:

```csharp
event EventHandler? SessionRevoked;   // 401 — токен мёртв, авто-выход
event EventHandler? AccessDenied;     // 403 — причина неизвестна, объяснить
```

`OpenShiftAsync` и `GetShiftStateAsync` поднимают `AccessDenied` на `Forbidden`,
обе через тот же `Dispatcher.UIThread.Post`, что и существующий
`NotifySessionRevoked`. Сетевое исключение по-прежнему не поднимает **ничего** —
офлайн-работа неприкосновенна, и касса без связи не должна считаться разлогиненной.

`PosViewModel`:

- `OnShiftSessionRevoked` — без изменений.
- Новый `OnShiftAccessDenied` → выставляет `IsShiftAccessDenied`.
- **Флаг снимается, в отличие от `IsSessionRevoked`.** `IsSessionRevoked`
  намеренно вечен, потому что мёртвый токен не воскресает. 403 от моргнувшей
  tenant-БД проходит сам, поэтому `IsShiftAccessDenied` гасится, как только
  `GetShiftStateAsync`/`OpenShiftAsync` вернут непустой id смены.

### Где это видно кассиру

Верхний баннер не годится: модалка смены — `Grid.RowSpan="3"` с `ZIndex="1000"`
([`PosView.axaml:987`](../../../src/VvCash/Views/PosView.axaml)) — перекрывает
его целиком. Текст идёт **внутрь модалки**: строка `Pleasestartyourshift`
подменяется красным объяснением при `IsShiftAccessDenied`.

Новых контролов не добавляем: кнопка `SignOutCommand` в этой модалке уже есть.
Правка лишь даёт тексту сказать, на что жать.

---

## Проблема 2 — окно покупательского дисплея

[`App.axaml.cs`](../../../src/VvCash/App.axaml.cs) в `NavigateToPos` создаёт
`new CustomerDisplayWindow(...).Show()`, ссылку не хранит и предыдущее окно не
закрывает. Каждый цикл logout→login кладёт на второй экран ещё одно окно.

Второе: `CashFeatureCodes.CustomerDisplay` здесь не проверяется вообще. Флаг
гасит только отправку данных в `PosViewModel.OnCartChanged`, поэтому магазин,
выключивший функцию, всё равно получает окно с «Welcome!».

### Решение — одно окно на весь запуск

Буквально «создать при старте» нельзя: `Screens.All` требует уже *открытого*
окна, о чём в коде есть прямой комментарий у
`desktop.MainWindow.Opened += (s, e) => NavigateToPos()` — «Defer until the
window is open so multi-monitor detection works». Поэтому окно создаётся
**лениво на первом `NavigateToPos` и переиспользуется дальше**:

```
CustomerDisplayWindow? customerWindow = null;   // рядом с activePosVm, живёт весь запуск

NavigateToPos():
    screenBounds = desktop.MainWindow?.Screens.All.Select(s => s.Bounds) ?? []
    placement = CustomerDisplayPlacementSelector.Select(
        Environment.GetEnvironmentVariable(CustomerDisplayPlacementSelector.OverrideVariable),
        screenBounds)
    если placement != null:
        customerWindow ??= new CustomerDisplayWindow { Position = placement.Position, ... }
        customerVm = resolve CustomerDisplayViewModel
        posVm.CustomerDisplayViewModel = customerVm
        customerWindow.DataContext = customerVm            // перевешиваем, окно то же
        posVm.SubscribeCustomerDisplayVisibility((_, v) => v ? Show() : Hide())
```

**Нужно и событие, и применение текущего значения.** Сгенерированный
`OnIsCustomerDisplayEnabledChanged` срабатывает только на *изменение*.
`CashFeatureService` — синглтон и переживает logout→login, поэтому на момент
`NavigateToPos` флаг уже может быть в финальном значении и события не будет
никогда. Оба шага упакованы в `SubscribeCustomerDisplayVisibility`: он подписывает
и тут же зовёт обработчик с текущим значением. Правило, оставленное комментарием,
однажды забудут — здесь его нельзя обойти. Все четыре комбинации
(было/стало × вкл/выкл) сходятся.

**Скрытие при выходе.** В существующий обработчик `posVm.LogoutRequested`
добавляется `customerWindow?.Hide()` — иначе экран покупателя продолжает светить
последнюю корзину, пока следующий кассир вводит пароль. Та же поломка времени
жизни, чинится в том же месте.

**Про утечку подписки — исходное утверждение спеки было неверным.** Здесь стояло
«издатель транзиентный, уходит `posVm` — уходит и лямбда». Это опровергается
комментариями самого `App.axaml.cs`: `PosViewModel` резолвится через
`GetRequiredService` из корневого провайдера, а контейнер удерживает каждый
созданный им `IDisposable` до конца жизни процесса (ровно поэтому
`SellerSwitchViewModel` строится через `ActivatorUtilities.CreateInstance`).
Экземпляры не собираются, и `InitializeAsync`, запущенный fire-and-forget без
токена отмены, может дописать флаг уже после того, как экран сменился — и дёрнуть
общее долгоживущее окно текущей сессии. Поэтому `PosViewModel.Dispose` обнуляет
`CustomerDisplayVisibilityChanged`, а подписка идёт через
`SubscribeCustomerDisplayVisibility`, который сам применяет текущее значение —
правило, которое вызывающий не может забыть.

### Тестовый обход двух экранов

На машине разработки второго монитора нет, а при `Screens.Count == 1` окно не
создаётся вовсе — вся секция была бы неверифицируемой. Обход делается по образцу
[`RenderingSelector`](../../../src/VvCash/Services/Rendering/RenderingSelector.cs):
чистая функция «env-переменная + факты об окружении → решение», тестируемая без
запуска Avalonia.

```csharp
namespace VvCash.Services;   // рядом с прочими решателями, не в Services.Rendering — это не рендеринг

public sealed record CustomerDisplayPlacement(PixelPoint Position, bool ForcedForTesting);

public static class CustomerDisplayPlacementSelector
{
    public const string OverrideVariable = "VVCASH_CUSTOMER_DISPLAY";

    // null => окно не создавать вообще
    public static CustomerDisplayPlacement? Select(string? overrideValue, IReadOnlyList<PixelRect> screens);
}
```

| `VVCASH_CUSTOMER_DISPLAY` | экранов >1 | ровно один экран | экранов ноль |
|---|---|---|---|
| не задана / неизвестное значение | позиция `screens[1]`, `Forced = false` | `null` | `null` |
| `force` | позиция `screens[1]`, `Forced = false` — то же, что автоматика | позиция `screens[0]`, `Forced = true` | `null` |
| `off` | `null` | `null` | `null` |

`force` на двухэкранной кассе намеренно **не** включает `Forced`: там окно и так
попадает на свой экран, и делать его `Topmost` поверх POS было бы регрессией.
Переменная форсирует только само создание окна, а не тестовый режим показа.
Пустой список экранов не бывает на живой системе, но `Select` обязан отвечать и
на него — иначе индексация упала бы до появления UI.

Неизвестное значение проваливается в автоматику, не бросает — та же дисциплина,
что у `RenderingSelector`: это выполняется до появления UI, и опечатка в
переменной не должна ронять кассу без окна, в котором можно сообщить причину.

`ForcedForTesting` — единственное, что читает хост, чтобы включить `Topmost` и
скромный размер. Это обязательно: `MainWindow` — `WindowState="FullScreen"` +
`Topmost="True"`, поэтому на одном мониторе окно покупателя, просто сдвинутое
рядом, окажется под основным и будет невидимо. В продакшене флаг всегда `false`,
и ветка построения окна остаётся ровно такой, как сейчас.

---

## Проблема 3 — разрушающие кнопки в настройках

Экран настроек открывается из `LoginViewModel.SettingsRequested`, то есть **до
всякой аутентификации**. Там [`SettingsView.axaml:299`](../../../src/VvCash/Views/SettingsView.axaml)
— `ClearUnsyncedDocumentsCommand`: одно нажатие безвозвратно стирает очередь
непроведённых продаж. Это деньги, которые уже взяли, а сервер не видел.
Восстановить нечем. Ни диалога, ни PIN.

Запереть сам экран нечем: настройки **обязаны** быть доступны до входа, иначе на
свежей кассе не задать `BackendUrl`. Значит вся защита ложится на подтверждение —
и на удаление того, чему подтверждение не поможет.

### Решение

**`ClearUnsyncedDocuments` удаляется целиком.** Зачем кнопка была нужна —
расчистить навечно застрявшую очередь — уже решено правильным способом, через
`MarkDocumentRejectedAsync`: документ, который сервер отверг по существу,
выходит из ротации сам и остаётся на диске для бэк-офиса. Остался только способ
потерять выручку в одно нажатие.

Четыре точки:

- `SettingsView.axaml` — блок с кнопкой;
- [`SettingsViewModel`](../../../src/VvCash/ViewModels/SettingsViewModel.cs) — команда;
- [`IOfflineStorageService`](../../../src/VvCash/Services/Data/IOfflineStorageService.cs) / `OfflineStorageService` — метод;
- шесть фейков `IOfflineStorageService` в тестах — по строке из каждого
  (`CashFeatureServiceTest`, `ExpenseDocumentServiceTest`, `PosViewModelSellerGateTest`,
  `SellerRosterServiceTest`, `SettingsViewModelTest`, `SyncServiceTest`).

Ни один тест на поведение этой команды не завязан — только заглушки интерфейса.

**Подтверждение на `ClearProducts` / `ClearCategories`.** Обе восстанавливаются
синком, но на офлайн-кассе стирают возможность продавать до следующей связи.
Модалок в `SettingsView` нет, но корень — `Grid`, так что оверлей ложится
сиблингом с `ZIndex`, ровно как в `PosView`. Состояние копирует уже существующий
в `PosViewModel` паттерн `IsShiftCloseConfirmVisible` + Confirm/Cancel:

```csharp
[ObservableProperty] private bool _isConfirmVisible;
[ObservableProperty] private string _confirmMessage = string.Empty;
private Func<Task>? _pendingAction;          // что выполнить по «Да»
// ConfirmCommand / CancelConfirmCommand
```

Кнопки больше не делают работу сами — взводят `_pendingAction` и поднимают
оверлей. Текст объясняет последствие для офлайн-кассы.

---

## i18n

Новые ключи заводятся сразу в пяти локалях `ru/en/tg/uz/kk`:

| Ключ | Где |
|---|---|
| `ShiftAccessDenied` | объяснение 403 в модалке смены, вместо `Pleasestartyourshift` |
| `ConfirmClearProducts` | текст подтверждения очистки каталога |
| `ConfirmClearCategories` | текст подтверждения очистки категорий |
| `ConfirmDelete` | подтверждающая кнопка оверлея |

Отменяющая кнопка переиспользует существующий ключ `Cancel`; заводить второй с
тем же смыслом незачем.

Недостающий ключ [`I18nService`](../../../src/VvCash/Services/I18nService.cs)
рендерит на экран как `[Ключ]`, то есть промах виден кассиру, а не в логе.

## Тестирование

**Базовая линия до правок: 677 passed / 1 failed** —
`UpdateViewModelTest.AvailableVersionTextCarriesTheReleaseVersion`, падение внутри
`Avalonia.Threading.DispatcherPriorityQueue.RemoveItemFromPriorityChain`. Это
известная гонка диспетчера, не логика. Правило на весь батч: сверяем не «всё
зелёное», а **дельту к этой линии**, и на любом падении сначала смотрим стек —
Avalonia-гонка или наш диф.

| Правка | Файл | Чем |
|---|---|---|
| 403 → `AccessDenied`, не `SessionRevoked` | `ShiftServiceTest` | `StubHttpMessageHandler`, без Avalonia — зеркалит существующие 401-тесты |
| 401 по-прежнему → `SessionRevoked` | `ShiftServiceTest` | регрессия на ассиметрию |
| Сетевой сбой → ни одно событие | `ShiftServiceTest` | расширение существующего теста на второе событие |
| `IsShiftAccessDenied` взводится и гасится | `PosViewModelSellerGateTest` | рядом с готовым регионом на 401-восстановление, с `Dispatcher.UIThread.RunJobs()` |
| `CustomerDisplayVisibilityChanged` на смену флага | `PosViewModelSellerGateTest` | чистое событие VM, диспетчер не нужен |
| `CustomerDisplayPlacementSelector` | `CustomerDisplayPlacementSelectorTest` (новый) | зеркало `RenderingSelectorTest`; `PixelRect(int,int,int,int)` публичен |
| Подтверждение не трогает хранилище до «Да» | `SettingsViewModelTest` | фейк `IOfflineStorageService` считает вызовы |
| Удаление `ClearUnsyncedDocuments` | компилятор | шесть фейков + команда |

### Чего юнит-тесты не покрывают

Прямо сказано в шапке `PosViewModelSellerGateTest`: проводка в `App.axaml.cs`,
XAML-привязки и всё, что требует живого `Avalonia.Window`. Сюда попадают
Show/Hide окна покупателя, подмена его `DataContext`, новый текст в модалке
смены и оверлей подтверждения.

Привязки в проекте **рефлективные** (`AvaloniaUseCompiledBindingsByDefault=false`)
— опечатка в пути компилируется молча и отваливается в рантайме. Значит
XAML-правки верифицируются запуском приложения, глазами, по чек-листу:

1. Модалка смены при 403 показывает объяснение, а не немой тупик; кнопка выхода работает.
2. `VVCASH_CUSTOMER_DISPLAY=force` — окно появляется поверх POS на одном мониторе.
3. logout→login не плодит второе окно; на экране логина окно скрыто.
4. Выключенный `cash_customer_display_enabled` прячет окно.
5. Настройки: кнопки очистки поднимают подтверждение; «Отмена» ничего не делает; кнопки очистки очереди больше нет.

### Сборка

Сборка при запущенном приложении упирается в файловую блокировку — собираем в
`build/verify`, тесты гоняем через `& ./run-tests.ps1` (не `pwsh`, его на машине нет).

## Вне скоупа

- Разведение кодов 401/403/503 на бэкенде. По существу правильнее, но это второй
  репозиторий и слом контракта для остальных клиентов; рассматривалось и отклонено.
- Запирание экрана настроек за паролем — невозможно, пока на нём задаётся `BackendUrl`.
- Остальные 15 находок ревью — батчи B, C, D.
