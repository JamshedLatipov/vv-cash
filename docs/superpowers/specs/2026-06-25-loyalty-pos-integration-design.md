# Интеграция новой системы лояльности в кассу (POS)

**Дата:** 2026-06-25
**Источник API:** https://market.proffi.io/swagger/doc.json (тег `discounts`)
**Статус:** дизайн утверждён, ждёт ревью спеки

## Контекст и проблема

Серверная система скидок/лояльности переработана. Скидка теперь резолвится сервером
по всей корзине (best-deal whole-cart winner) с учётом нескольких стратегий карты и
промокодов. Касса же считает скидку локально и плоско:

- Клиент выбирается в [`PosViewModel`](../../../src/VvCash/ViewModels/PosViewModel.cs),
  берётся единственное число `DiscountCard.Discount` (плоский %).
- [`CartService`](../../../src/VvCash/Services/CartService.cs) складывает локально:
  купон% + купон-сумма + ручная% + ручная-сумма + клиент%.
- Купоны — мок с хардкодом (`SAVE10`, `WELCOME5`, `FLAT20`) в
  [`DiscountService`](../../../src/VvCash/Services/DiscountService.cs).
- Ни тиров лояльности, ни per-line, ни best-deal, ни реального промо-движка.

**Цель:** касса использует серверный движок скидок как источник истины через
`POST /discounts/quote/`, сохраняя работоспособность офлайн.

## Серверная модель (из swagger)

**Карта скидки** имеет `calculator_type` + `strategies[]` из набора:
`progressive | static | loyalty | referral | volume | seasonal`.

- `loyalty` — тиры по баллам (`min_points` → discount %), накопление `points_per_unit`
- `volume` — тиры по количеству (`min_quantity` → discount %)
- `seasonal` — discount % внутри окна дат (`starts_at`/`ends_at`)
- `referral` — реферальный код → discount %

**Промокоды** — `standard` или `bxgy` (buy X get Y), reward `percent`/`amount`,
таргеты `product`/`category`/`tag`, скоупы `cart`/`lines`.

**POS-эндпоинт — `POST /discounts/quote/`**
Описание сервера: *"Resolves per-line discounts via best-deal (whole-cart winner).
Preview; writes a price-locked snapshot."*

Запрос `discounts.quoteInput`:
```json
{
  "warehouse_id": "string (required)",
  "lines": [ { "product_id": "string", "quantity": 0, "unit_price": 0 } ],
  "card_identifier": "string (optional)",
  "code": "string (optional)"
}
```

Ответ `discounts.QuoteResult`:
```json
{
  "quote_id": "string",
  "subtotal": 0,
  "discount_total": 0,
  "total": 0,
  "lines": [ {
    "product_id": "string", "quantity": 0, "unit_price": 0,
    "line_subtotal": 0, "discount_amount": 0, "discount_percent": 0,
    "final_line_total": 0,
    "source": { "kind": "card|code", "ref": "string" }
  } ],
  "applied":  [ { "kind": "string", "amount": 0, "ref": "string" } ],
  "rejected": [ { "reason": "string", "ref": "string" } ]
}
```

## Утверждённые решения

1. **Полная интеграция** через `/discounts/quote/` (сервер — источник истины онлайн).
2. **Офлайн:** fallback на кэшированный плоский % карты (текущее поведение); без промо и
   без тиров. Касса не простаивает.
3. **Ручная скидка кассира:** применяется **стеком сверху** результата quote, итог
   клампится `≤ subtotal` (как сейчас аддитивно).
4. **Промокоды:** мок-купоны убираются; введённый код уходит в `quote.code`, сервер валидирует.

## Архитектура (вариант A: QuoteService + снапшот в CartService)

Скидочная математика остаётся централизованной в `CartService`; новый типизированный
клиент инкапсулирует эндпоинт; `PosViewModel` оркеструет перезапрос.

### Компоненты

**1. Модели — `src/VvCash/Models/Api/Quote*.cs`**
DTO `QuoteRequest`, `QuoteLineInput`, `QuoteResult`, `QuoteLineResult`, `QuoteSource`,
`QuoteApplied`, `QuoteRejected` с `JsonPropertyName` по swagger (snake_case).

**2. `IQuoteService` / `QuoteService` — `src/VvCash/Services/Api/`**
- `Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct)`
- POST на `{BackendUrl}discounts/quote/`, тот же `HttpClient` + `AuthHeaderHandler`.
- Сетевой фейл / не-200 → `null` (вызывающий уходит в fallback).
- Регистрируется в DI рядом с прочими `*Service`.

**3. Источник `warehouse_id`**
Тянем из `/cashes/config/get/` (или `/cashes/shift/state/`) при открытии смены,
кэшируем в сессии (`ISettingsService` или новый держатель `CurrentWarehouseId`).
⚠️ **Открытый пункт планирования:** тело cash-эндпоинтов нетипизировано
(`response.Response`) — точное имя поля warehouse подтвердить по живому ответу.

**4. `CartService` ([cs](../../../src/VvCash/Services/CartService.cs))**
- Новое состояние: `QuoteResult? Quote`, `string? QuoteId`.
- Методы: `ApplyQuote(QuoteResult result)`, `ClearQuote()`.
- `TotalDiscount`:
  - онлайн с применённым quote → `Quote.discount_total`;
  - офлайн / нет карты и кода → текущий плоский `CustomerDiscountPercent` (как сейчас);
  - **плюс ручная скидка кассира сверху**; итог клампится `≤ Subtotal`.
- Per-line скидки из `Quote.lines` доступны для построения чека.
- `ClearCart`/`ClearCustomerDiscount` дополнительно зовут `ClearQuote()`.
- `LoadSnapshot` (parked sales) — снапшот не несёт quote; после восстановления
  перезапрашиваем quote если онлайн, иначе плоский %.

**5. `PosViewModel` ([cs](../../../src/VvCash/ViewModels/PosViewModel.cs))**
- Триггеры перезапроса: изменение корзины, выбор клиента, ввод/снятие промокода.
- Если **онлайн** и есть (карта `DiscountCard.Identifier`) или (введённый код):
  дебаунс (≈300 мс) → собрать `QuoteRequest` из корзины → `QuoteService.QuoteAsync`
  → `CartService.ApplyQuote`. Прошлый запрос отменяем по `CancellationToken`.
- Иначе (офлайн / нет карты и кода): `CartService.ClearQuote()` + плоский fallback.
- `applied` / `rejected` → в `StatusMessage` (отклонённый промокод с причиной).
- `card_identifier` = `SelectedCustomer.DiscountCard.Identifier`.

**6. Построение чека — `DocumentRequest` ([cs](../../../src/VvCash/Models/Api/DocumentRequest.cs))**
- Маппинг `QuoteResult.lines[i]` → `DocumentProduct.discount_percent` +
  `price_before_discount` (поля уже есть).
- Офлайн без quote — текущий путь (плоский % в `discount_percent`).
- ⚠️ **Открытый пункт планирования:** нужен ли серверу `quote_id` в документе для
  honor price-locked снапшота. Если да — добавить поле `quote_id` в `DocumentRequest`.

**7. Промо UI**
- Текущий ввод купона переключается на отправку `code` в `QuoteRequest.code`.
- Мок-купоны и хардкод из `DiscountService` удаляются; `IDiscountService`/`Coupon`
  переоформляются под «введённый код» (или удаляются, если избыточны).

## Обработка ошибок

| Ситуация | Поведение |
|---|---|
| Офлайн / сеть-фейл при quote | Fallback на плоский %, чек **не** блокируется, статус «офлайн-скидка» |
| Промокод отклонён (`rejected`) | Показать причину в StatusMessage, корзину сохранить |
| Quote вернул `total`/`discount_total` онлайн | Доверяем серверным числам |
| Ручная + серверная скидка > subtotal | Кламп `≤ Subtotal` |
| Гонка перезапросов quote | Старый запрос отменяется (CancellationToken), берём последний |

## Тестирование

- **CartService (юнит):** математика `TotalDiscount` — онлайн-снапшот + ручная сверху;
  офлайн-плоский %; кламп; `ClearQuote` сбрасывает состояние.
- **Маппинг (юнит):** корзина → `QuoteRequest`; `QuoteResult.lines` → `DocumentProduct`.
- **QuoteService (юнит):** десериализация образца `QuoteResult` JSON; не-200/исключение → `null`.
- **Оффлайн-ветка:** при `IsSystemOnline == false` quote не зовётся, используется плоский %.
- **Промо:** accepted код отражается в `applied`; rejected — причина в статусе.

## Вне объёма (YAGNI)

- Админ-сторона лояльности (создание карт, тиров, seasonal/volume/referral конфигов) —
  это бэкофис, не касса.
- Начисление/отображение баллов лояльности в кассе (если не вернётся в `QuoteResult`).
- Реферальные/сезонные настройки UI.

## Открытые пункты для фазы планирования

1. **warehouse_id** — точное поле в ответе `/cashes/config/get/` (живой ответ).
2. **quote_id в документе** — требует ли сервер привязку price-locked снапшота к чеку.
