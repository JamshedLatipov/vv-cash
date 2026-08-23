# Код-ревью vv-cash — исходный документ

**Дата ревью:** 2026-08-22
**Сессия:** claude-opus-5, effort xhigh, сессия «Код ревью»
**Восстановлено в репозиторий:** 2026-08-23

## Происхождение

Ревью проведено в сессии Claude Code и **никогда не коммитилось**. Спеки батчей A и B
ссылаются на находки по номерам, но самого списка в репозитории не было — номера
#3, #4, #10, #13, #15 в спеке батча B не разрешались ни во что. Документ восстановлён
дословно из транскрипта сессии.

Ниже — текст ревью без правок. Добавлены только карта батчей в конце и `../../../` в путях ссылок —
ревью писалось от корня репозитория, а лежит документ на три уровня глубже.

### Реплика ревьюера перед списком

> Прочитал весь код: 15k строк C#, 3.5k XAML, csproj, CI, release.ps1. Тесты прогнал — 677 passed / 1 failed (`UpdateViewModelTest`, известная гонка Avalonia Dispatcher, не логика).

---

# Код-ревью vv-cash

## Критично

**1. `ShiftService` не распознаёт 403 → авто-восстановление мёртвой сессии не работает никогда**

[ShiftService.cs:109](../../../src/VvCash/Services/Api/ShiftService.cs:109) и [:216](../../../src/VvCash/Services/Api/ShiftService.cs:216) проверяют только `HttpStatusCode.Unauthorized`. Бэкенд на протухший Bearer отдаёт **403**:

```
middlewares/utils.go:56
c.AbortWithStatusJSON(http.StatusForbidden, gin.H{"status": "error", "message": "forbidden"})
```

401 приходит только с login/refresh. `ExpenseDocumentService.IsSessionRejected` ([:204](../../../src/VvCash/Services/Api/ExpenseDocumentService.cs:204)) это уже знает — `Unauthorized or Forbidden` — и подробно документирует. ShiftService из той же пары не обновили.

Итог: `IShiftService.SessionRevoked` не срабатывает никогда, `PosViewModel.OnShiftSessionRevoked` — мёртвый код, а кассир с протухшим токеном упирается в модалку смены, которая не может открыться. Спасает только ручной SignOut. Фикс — одна строка в обоих местах.

**2. `CustomerDisplayWindow` копится и игнорирует фичефлаг**

[App.axaml.cs:151](../../../src/VvCash/App.axaml.cs:151): `NavigateToPos` создаёт `new CustomerDisplayWindow(...).Show()`, ссылку не хранит, предыдущее окно не закрывает. Каждый цикл logout→login кладёт ещё одно окно на второй экран. Плюс `CashFeatureCodes.CustomerDisplay` здесь не проверяется — флаг гасит только push данных в `OnCartChanged`, само окно открывается всегда.

**3. ESC/POS печатает UTF-8 без выбора кодовой страницы**

[EscPosPrinterService.cs:150](../../../src/VvCash/Services/Hardware/EscPosPrinterService.cs:150) — `Encoding.UTF8.GetBytes`. Инициализация только `ESC @`; команды `ESC t n` нет нигде. Термопринтеры ESC/POS работают на однобайтовых кодовых страницах (CP866 для кириллицы) — русские названия товаров печатаются мусором. Тесты не ловят: `EscPosUnitTest` сравнивает с тем же UTF-8. То же в `VfdDisplayService` — `Encoding.ASCII`, кириллица → `?`.

**4. USB-печать — заглушка, которая рапортует «успех»**

`SendViaUsb` пишет в Console и возвращает completed task → `PrintReceiptAsync` возвращает `true` → `StatusMessage = "Receipt printed."`. Касса, настроенная на USB-принтер, чеков не печатает и молчит об этом.

**5. Настройки открыты без аутентификации и содержат необратимое действие без подтверждения**

Вход только через `LoginViewModel.SettingsRequested`, т.е. с экрана логина, до всякой авторизации. Там [SettingsView.axaml:299](../../../src/VvCash/Views/SettingsView.axaml:299) — `ClearUnsyncedDocumentsCommand`: одно нажатие стирает очередь непроведённых продаж. Это деньги, которые уже взяли, а сервер не видел, и восстановить их нечем. Ни диалога, ни PIN. `ClearProducts`/`ClearCategories` рядом — то же, но лечатся синком.

## Важно

**6. Товары никогда не удаляются при синхронизации.** `SyncProductsAsync` только upsert-ит по версиям; в `SaveCategoriesInternalAsync` про это даже есть комментарий. Снятый с продажи товар остаётся продаваемым бессрочно.

**7. `ProductImageLoader.Cache` — статический `ConcurrentDictionary` без лимита и вытеснения.** Касса живёт весь день; каталог на тысячи позиций → декодированные `Bitmap` копятся до конца процесса.

**8. Продажа в кредит не проверяет лимит.** `MixedPaymentViewModel.SellOnCredit` требует только `HasCustomer`. `CounterpartyResponse.CreditLimit` и `CurrentBalance` в коде не читаются вообще (единственное употребление `CurrentBalance` — вывод баланса в окне поиска). Документ уходит с `Remained > 0` без клиентской проверки.

**9. Деньги в SQLite лежат как REAL.** `Products.Price/OriginalPrice`, `ParkedSales.Total`, `Sellers.MaxDiscount` — `SqliteType.Real`, читаются `GetDecimal` (double→decimal). Для типичных цен round-trip чистый, но в кассовом ПО это латентная потеря точности. `Microsoft.Data.Sqlite` по умолчанию кладёт decimal в TEXT — точно.

**10. `CompositePrinterService.InitializePrinters` подменяет `_printers` по `SettingsChanged` без синхронизации,** прямо посреди возможного `PrintReceiptAsync`. Не фатально (каждый `EscPosPrinterService` держит соединение per-call и глотает свои исключения), но состояние гонки настоящее.

## Мелочи

11. **`RequoteAsync` — гонка на диспозе CTS.** `ReplaceQuoteCts` может задиспозить cts между завершением `Task.Delay` и `var ct = cts.Token;` → `ObjectDisposedException`. Ловится `RequoteSafeAsync`, самолечится следующим requote, но шумит в логе. Читать токен один раз до передачи.

12. **`CartService.ApplyQuotedPrices` ставит только `QuotedUnitPrice`,** без `QuotedUnitDiscount`/`QuotedDiscountPercent`. Поэтому `CartItem.HasLineDiscount`/`LineDiscount`/`LineFinalTotal` в POS всегда «нет скидки» — они живут только в `ExchangeWindow`, где `ExchangeViewModel.ApplyIssuedQuote` их проставляет. Либо строки корзины теряют пометку скидки, либо три свойства `CartItem` — мёртвый вес.

13. **`ExchangeViewModel`: `(int)l.Quantity`** в `issuedReceiptLines` — весовая позиция 1.4 кг печатается на чеке обмена как «x1».

14. **`CounterpartyService.CreateCounterpartyAsync`: `PropertyNamingPolicy = CamelCase`** при явных `[JsonPropertyName]` на всей модели — политика ни на что не влияет, только вводит в заблуждение.

15. **Мёртвый и подменный код в дереве.** `MockCustomerDisplayService` зарегистрирован как боевой `ICustomerDisplayService` — покупательский дисплей по факту пишет в Console; `VfdDisplayService` не создаётся нигде. `MockProductService` и `MockPrinterService` (с хардкодным `$`) не зарегистрированы вообще.

16. `OfflineStorageService._isInitialized` без синхронизации — сейчас безопасно (вызывает только `PosViewModel`), но не по контракту.

17. `SellerSession` lockout (`_failures`/`_lockedUntil`) только в памяти — рестарт снимает блокировку. Согласуется с заявленным «PIN — атрибуция, не граница безопасности», но стоит зафиксировать явно.

## Что сделано хорошо

- `UpdateService` — pin хоста загрузки к хосту манифеста, https-only, SHA-256 до запуска, случайное имя файла против подмены между проверкой и `Process.Start`, раздельные манифесты x86/x64.
- `ExpenseDocumentService.IsFinalRefusal` — аккуратное различение «сервер отказал по существу» и «сервер недоступен», включая числовой vs строковый `status` как границу приложение/middleware.
- `_returnBooked`/`_payoutBooked` в `ExchangeViewModel` и `ReturnsViewModel` — идемпотентность необратимых шагов там, где у сервера нет отмены.
- `PromotionCalculator`, `MoneyPolicy`, `UnitConverter` — зеркала серверной арифметики с объяснением, почему округление именно такое.
- `release.ps1` — проверяет то, что реально опубликовано, включая ловушку «SPA отдаёт index.html под 200».
- i18n: все 174 ключа из XAML и 23 из C# есть во всех пяти локалях. Все binding-пути резолвятся.

---

## Карта батчей

Разрезано на четыре батча по характеру верификации (таблица — из
[спеки батча A](../specs/2026-08-22-session-and-data-safety-design.md)):

| Батч | Содержание | Почему отдельно |
|---|---|---|
| **A** | сессия и сохранность данных | проверяется юнит-тестами, чинит потерю выручки и тупик кассира |
| **B** | печать и железо | не верифицируется без принтера |
| **C** | синк и хранилище | требует продуктовых решений и миграции схемы |
| **D** | мелочи и чистота | не срочно, не блокирует |

| # | Находка | Батч | Статус |
|---|---|---|---|
| 1 | `ShiftService` не распознаёт 403 | A | закрыта, PR #44 |
| 2 | `CustomerDisplayWindow` копится и игнорирует фичефлаг | A | закрыта, PR #44 |
| 3 | ESC/POS без выбора кодовой страницы | B | код закрыт, PR #45; приёмка — только на точке с принтером |
| 4 | USB-печать — заглушка, рапортующая успех | B | код закрыт, PR #45; приёмка — только на точке с принтером |
| 5 | Настройки: необратимая очистка очереди без подтверждения | A | закрыта, PR #44 |
| 6 | Товары не удаляются при синхронизации | **C** | открыта |
| 7 | `ProductImageLoader.Cache` без лимита и вытеснения | **C** | открыта |
| 8 | Продажа в кредит не проверяет лимит | **C** | открыта |
| 9 | Деньги в SQLite лежат как `REAL` | **C** | открыта |
| 10 | `CompositePrinterService` пересобирает список во время печати | B | закрыта, PR #45 (перенесена из D) |
| 11 | `RequoteAsync` — гонка на диспозе CTS | **D** | открыта |
| 12 | `CartService.ApplyQuotedPrices` не ставит скидочные поля | **C** | открыта |
| 13 | `ExchangeViewModel`: `(int)l.Quantity` на чеке обмена | B | закрыта, PR #45 |
| 14 | `CounterpartyService`: бесполезный `PropertyNamingPolicy` | **D** | открыта |
| 15 | `MockCustomerDisplayService` зарегистрирован как боевой | B | закрыта, PR #45 |
| 16 | `OfflineStorageService._isInitialized` без синхронизации | **C** | открыта |
| 17 | `SellerSession` lockout только в памяти | **D** | открыта |

**Находок семнадцать, не восемнадцать.** Спеки батчей A и B говорят «18 находок» и
«остальные 15 находок» — арифметическая ошибка исходной сессии, попавшая в оба
документа. Список выше пронумерован так же, как ревью, и заканчивается на 17.

Все девять открытых находок перепроверены против `main` на 2026-08-23 — ни одна
не была закрыта попутно батчами A и B.

### Почему #12 в батче C

Ревью положило находку в «мелочи», но при проверке против `main` формулировка не
подтвердилась. `CartService.cs:293` ставит только `QuotedUnitPrice`; `CartItem.LineDiscount`
считается от `QuotedUnitDiscount`, который в POS не ставит никто. При этом
`HasLineDiscount`/`LineDiscount`/`LineFinalTotal` привязаны **только** в
`ExchangeWindow.axaml` (строки 233–258) — в `PosView.axaml` их нет вообще.

То есть строки корзины не «теряют пометку скидки»: терять нечего, разметки для неё в POS
не существует. Настоящая развилка — либо POS должен показывать построчную скидку (новая
разметка плюс заполнение полей в `CartService`), либо три свойства `CartItem` по смыслу
принадлежат обмену и в POS им делать нечего. Это продуктовое решение того же класса, что
#6 и #8, поэтому находка идёт в батч C, а не в D.

### Что в батче D сверх исходного ревью

- **Долговременный лог.** Найдено при реализации батча B, в исходном ревью его нет.
  `OutputType` — `WinExe`, логирования нет, пятнадцать файлов пишут в `Console`.
  См. «Вне скоупа» в [спеке батча B](../specs/2026-08-22-printing-and-hardware-design.md).
- **Двенадцать пунктов сознательно отложенного долга** из раздела «Долг, отложенный
  сознательно» в [плане батча B](../plans/2026-08-22-printing-and-hardware.md).
