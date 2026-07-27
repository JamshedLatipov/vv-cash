# Учёт акций на кассе

Дата: 2026-07-27
Репозитории: `cloudmarket-server` (бэкенд), `vv-cash` (касса)

## Контекст

Бэкенд получил механику акций (`discounts/promotion.go`): целевой набор
(product / category / tag) плюс упорядоченная лестница количественных правил
(`percent`, `amount`, `cheapest_free`). Активные акции уже участвуют в
best-deal внутри `POST /discounts/quote/` (`discounts/quote.go:146`).

Касса эти акции не видит по четырём причинам:

1. Котировка запрашивается только при наличии карты или промокода
   (`PosViewModel.RequoteAsync`, гейт `hasInput`). Акции применяются
   автоматически, входных данных у них нет — котировка не уходит никогда.
2. `quote_id` не отправляется в документ расхода, поэтому
   `documents.DocumentExpense.Process` не вызывает `discounts.FinalizeForSale`:
   нет проверки дрейфа цен, нет списания использования.
3. Ответ котировки не содержит названия акции — показать кассиру и напечатать
   в чеке нечего.
4. Офлайн акций нет вообще: они не кэшируются и локального расчёта не
   существует.

Дополнительно на бэкенде `promotions.used_count` не инкрементится нигде
(`finalize.go` списывает только промокод), а снапшот котировки не хранит id
победившей акции — значит `max_uses` у акции не работает.

## Решения

- Касса считает акции **онлайн через сервер** (истина — сервер) и
  **офлайн локальным движком** (порт `promotionSource` на C#).
- Офлайн-расчёт помечается как локальный: `quote_id` не отправляется,
  документ уходит с посчитанными процентами скидки по строкам.
- Матчинг целей офлайн требует у товара `category_id` и списка `tag_id`.
  `category` в синхронизации кассы уже отдаёт **id категории**
  (`cashes.CashProductItem.CategoryID` c тегом `json:"category"`),
  теги нужно добавить.

## Часть 1. Бэкенд (`cloudmarket-server`)

### 1.1 Хранение победившей акции в снапшоте котировки

- Миграция `20260728000200_quote_promotion_id`:
  `ALTER TABLE discount_quotes ADD COLUMN promotion_id uuid REFERENCES promotions(id)`.
- `DiscountRepo.SaveQuote`: писать `promotion_id`, когда
  `res.Applied[0].Kind == "promotion"` (ref = id акции).
- `QuoteSnapshot`: поле `PromotionID *string`; `GetQuoteSnapshot` его читает.

### 1.2 Списание использования акции

- `DiscountRepo.ConsumePromotion(ctx, db, id) (bool, error)` —
  `UPDATE promotions SET used_count = used_count + 1 ... WHERE id = $1
   AND deleted_at IS NULL AND (max_uses = 0 OR used_count < max_uses)`.
- Добавить в `DiscountRepoInterface`.
- `DiscountService.finalizeTx`: при `winning_source == "promotion"`
  вызывать `ConsumePromotion`, выставлять `out.Consumed`.

### 1.3 Название источника скидки в ответе котировки

- `SourceResult.Name`, заполняется в `promotionSource` (имя акции),
  `codeSource` (сам код), `cardSource` (идентификатор карты).
- `QuoteSource.Name`, `QuoteApplied.Name` в JSON.
- `buildResult` прокидывает имя победителя.

### 1.4 Эндпоинт активных акций для офлайн-кэша кассы

- `cashes/routes.go`: `cash.GET("/promotion/", ctrl.PromotionList)` —
  группа с `CashAuthentication` + `CashAuthorization`, отдельный permcode
  не нужен (как у остальных `/cashes/*`).
- Отдельный запрос `ListPromotionsForCash` вместо `ListActivePromotions`:
  без фильтра по окну и лимиту использований. Касса может простоять офлайн
  через момент старта или конца акции, поэтому окно она проверяет сама по
  `starts_at` / `ends_at` / `max_uses` / `used_count` из ответа.
- Экспорт `discounts.PromotionsForCash` — по образцу `FinalizeForSale`,
  чтобы `cashes` не лез в репозиторий скидок напрямую.
- Циклов импорта нет: `discounts` не импортирует `cashes`.

### 1.5 Теги товара в синхронизации кассы

- `CashProductItem.TagIDs []string` c `json:"tags"`.
- `GetProductsForVersion`: подзапрос
  `(SELECT COALESCE(array_agg(ppt.tag_id::text), '{}') FROM product_product_tags ppt WHERE ppt.product_id = p.id)`.
- То же для `GetProductFromBarcode` (`CashProductResult`), иначе
  отсканированный товар теряет теги.

## Часть 2. Касса (`vv-cash`)

### 2.1 Котировка без карты и промокода

`PosViewModel.RequoteAsync`: убрать гейт `hasInput`. Условие запроса —
онлайн, непустая корзина, известен `WarehouseId`.

### 2.2 Отправка `quote_id`

- `DocumentRequest.QuoteId` (`json:"quote_id"`, опускается при null).
- В `Pay()` подставлять `_cartService.QuoteId`.
- Офлайн-документ сохраняется без `quote_id`.

### 2.3 Модель акции на кассе

`Models/Promotion.cs`: `Promotion`, `PromotionRule`, `PromotionTarget`
(зеркало `PromotionView`).

### 2.4 Локальный расчёт

`Services/Discounts/PromotionCalculator.cs` — порт серверной логики:

- `Eligible(promotion, now)` — enabled / auto_apply / окно / max_uses;
- `PickRule(qty)` — минимальная `Position` среди подошедших правил;
- `MatchesLine` — scope `cart` матчит всё, иначе product / category / tag;
- эффекты `percent`, `amount` (пропорциональное распределение с осадком на
  последней строке), `cheapest_free` (группы по всему набору, освобождаются
  самые дешёвые единицы);
- `Resolve` — best-deal по максимальной сумме.

Округление: `decimal.Round(x, 2, MidpointRounding.AwayFromZero)` —
эквивалент серверной политики по умолчанию. Расхождение с нестандартной
per-store политикой допустимо: офлайн-цена уточняется при синхронизации.

### 2.5 Хранение и синхронизация акций

- `IOfflineStorageService.SavePromotionsAsync` / `GetPromotionsAsync`,
  таблица `Promotions(Id TEXT PRIMARY KEY, Payload TEXT)` — JSON целиком,
  чтобы не размазывать вложенные правила по таблицам.
- `SyncService.SyncProductsAsync` в конце тянет `cashes/promotion/`
  и перезаписывает кэш.

### 2.6 Товар: теги

- `Product.TagIds` (`List<string>`), колонка `Tags TEXT` в SQLite (JSON),
  ALTER при инициализации, парсинг в `SyncService`.
- `Product.Category` уже содержит id категории — переименования не делаем,
  добавляем комментарий.

### 2.7 Сборка скидки в корзине

`CartService.TotalDiscount`: при `Quote == null` вместо плоского пути
считать локальные акции, брать максимум из (акция, купон/клиентский процент).
`CartService` получает `IPromotionProvider` (кэш акций) через DI.

### 2.8 UI и чек

- `PosViewModel.AppliedDiscountName` — имя победившего источника
  (из `Quote.Applied[0].Name` онлайн, из локальной акции офлайн).
- Плашка в панели итогов рядом с суммой скидки.
- `IPrinterService.PrintReceiptAsync` — печатать строку источника скидки.

## Порядок работ

1. Бэкенд: 1.1 → 1.2 → 1.3 → 1.4 → 1.5, тесты `go test ./discounts/... ./cashes/...`.
2. Касса: 2.1 → 2.2 (быстрый онлайн-эффект), сборка.
3. Касса: 2.3 → 2.4 + юнит-тесты калькулятора против серверных кейсов.
4. Касса: 2.5 → 2.6 → 2.7.
5. Касса: 2.8.

## Часть 3. Доработки после первого прохода

### 3.1 Дробное количество (было `int`)

Сервер всегда принимал `quantity` как `float64`, обрезал только клиент:
1.4 кг продавалось и списывалось как 1. Тип `decimal` протянут по цепочке
`CartItem` → `ParkedCartItem` / `ParkedSale.ItemCount` → `DocumentProduct`.

- `CartItem.QuantityDisplay` — без хвостовых нулей, чтобы штучная позиция
  осталась «2», а не «2.000» на экране и в чеке.
- `ParkedSales.ItemCount` в SQLite переведён в `REAL`; динамическая
  типизация SQLite оставляет старые строки читаемыми.
- `CartService.SetQuantity` — точка входа для весового товара
  (количество приходит с весов, а не с кнопок ±). Неположительное
  количество удаляет строку.

Ввода веса в UI нет — это отдельная задача под интеграцию с весами.

### 3.2 Списание акции по офлайн-чеку

Офлайн-чек не имеет котировки, поэтому `FinalizeForSale` по нему не
запускается и `used_count` акции не рос — `max_uses` игнорировался для
всех продаж, сделанных без связи.

- Касса шлёт `offline_promotion_id` вместо `quote_id`.
- Миграция `20260728000300_expense_offline_promotion`:
  `document_expenses.offline_promotion_id`.
- `DocumentExpense.Process` при отсутствии `quote_id` вызывает
  `discounts.ConsumePromotionForSale`. Идемпотентность — на том же
  claim-гварде, что и у `FinalizeForSale`.
- Сервер игнорирует поле, если пришёл `quote_id`: победителя определил
  сервер, повторно списывать нельзя.
- `CartService.OfflinePromotion` отдаёт акцию только когда она **выиграла**
  у плоской скидки. Проигравшая акция не должна тратить использование.

Аудита дрейфа цен по офлайн-чекам по-прежнему нет — цены не были
зафиксированы на сервере, фиксировать нечего.

### 3.3 Округление по политике магазина

- `GET /cashes/money/` → `{scale, mode}`, резолв cash → warehouse → store →
  `stores.GetRoundingPolicy`.
- `stores.RoundingModeName` — обратная к `parseRoundingMode`, чтобы клиент
  получал режим в том же словаре, что и настройка.
- `MoneyPolicy.Round` на кассе повторяет `base.Quantize`: HALF_UP, BANK, UP,
  DOWN, CEIL, FLOOR. Неизвестный режим падает на HALF_UP, а не бросает
  исключение — кривая настройка не должна останавливать продажу.
- Политика кэшируется строкой в `Settings`, синкается вместе с акциями,
  раздаётся через `IPromotionProvider` и доходит до `PromotionCalculator`
  и `QuoteLineResolver`.

## Риски

- **Расхождение расчёта офлайн/онлайн.** Митигация: тесты калькулятора
  повторяют кейсы `discounts/source_promotion_test.go`, тесты `MoneyPolicy`
  повторяют режимы `base.Quantize`.
- **Дрейф цен по офлайн-чекам.** Неустраним: серверной фиксации цен по таким
  чекам не существует.
- **`cheapest_free` на весовом товаре** даёт 0 бесплатных единиц — так же,
  как на сервере (усечение до целых единиц), поведение зафиксировано тестом.
