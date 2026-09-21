# Продажа на сумму (ввод суммы вместо количества)

**Дата:** 2026-09-21
**Репозиторий:** `vv-cash` (касса)
**Статус:** дизайн утверждён, план не написан

## Контекст

Покупатель говорит «дайте наггетсов на 50 сомони». Кассир сегодня считает
`50 / 30` в голове, набирает `1.66` в паде количества и надеется, что не ошибся.

Пад количества уже существует —
[`QuantityPadViewModel`](../../../src/VvCash/ViewModels/QuantityPadViewModel.cs) и
модалка `Quantity Pad Modal` в
[`PosView.axaml`](../../../src/VvCash/Views/PosView.axaml). У него два режима ввода,
переключаемые `ToggleSwitch`: **штуки** и **вторая единица** (м², кг). Живое превью
показывает, во что превратится ввод, до нажатия «Применить» — ради округления вверх на
неделимых товарах, потому что это деньги покупателя.

Ввод суммы — третий режим того же пада. Всё остальное (превью, нумпад, коммит в
корзину) уже есть.

## Что не меняется

- `CartService`, `CartItem`, `ParkedSaleSnapshot`, серверный документ. Строка после
  подтверждения выглядит как обычная «1.666 кг» — корзина не знает, что количество
  пришло из суммы.
- Итог строки по-прежнему `pieces × UnitPrice`. Покупатель просил 50, платит 49.98.
- `UnitConverter` и его инвариант `quantity × factor ≈ quantity_in_unit`: в режиме суммы
  сначала выводится количество, а дальше оно идёт через существующие
  `SetQuantity` / `SetQuantityInUnit`.

## Решения

### Только делимые товары

Режим суммы доступен, когда `Product.IsDivisible && item.UnitPrice > 0`.

Штучный товар (`IsDivisible == false`) сегмента не видит: `50 / 3 = 16.67 шт` не
существует, а округление до целого — отдельный разговор, которого пока никто не
просил. Бесплатный товар (`UnitPrice == 0`) — деление на ноль, тоже нет.

### Округление вниз до грамма

Выведенное количество усекается до трёх знаков: `decimal.Truncate(x * 1000) / 1000`.
Шаг `WeightStep = 0.001` — весы показывают граммы, и кассир взвешивает ровно ту
цифру, что на экране.

Вниз, а не к ближайшему: `50 / 30 = 1.6666…` → `1.666 кг` → `49.98`. Покупатель
назвал сумму — платит не больше неё. К ближайшему дало бы `1.667 × 30 = 50.01`.

Шесть знаков отвергнуты: на экране было бы `50.00`, а чек печатает количество как
`0.###` — `1.667 кг × 30 = 50.01`. Бумага не сходилась бы с экраном.

Если после усечения получился ноль (сумма меньше цены одного грамма) — ввод
невалиден, «Применить» неактивна.

### В какой единице выводить количество

В «родной» единице товара:

| Товар | Формула | Коммит |
|---|---|---|
| есть вторая единица (`HasSecondaryUnit`) | `amountInUnit = floor3(money × UnitFactor / UnitPrice)` | `cart.SetQuantityInUnit(item, amountInUnit)`, `item.EnteredInUnit = true` |
| нет | `quantity = floor3(money / UnitPrice)` | `cart.SetQuantity(item, quantity)`, `item.EnteredInUnit = false` |

Одно деление, а не два: `PriceInUnit = UnitPrice / UnitFactor` — уже округлённое
частное (`10 / 0.6 = 16.666…67`), и повторное деление на него сажает
`50 × 0.6 / 10 = 3` на `2.999…`, что пол срезает до `2.999`. Умножение
`money × UnitFactor` точное, так что единственное округление — само деление.

Покупатель, просящий «на 50 сомони», думает в килограммах, а не в упаковках — поэтому
для товара со второй единицей выводим в ней, независимо от `SellInSecondaryUnit`.
`SetQuantityInUnit` сам получит штуки через `UnitConverter.ToBase` (шесть знаков,
`AwayFromZero`) — это уже согласовано с сервером и здесь не трогается.

### Режим суммы не запоминается

Пад открывается в `Pieces` или `Unit` по `item.EnteredInUnit`, как сейчас. В `Money` —
никогда: это жест на один ввод. Если кассир откроет пад на той же строке снова, он
увидит `1.666 кг`, а не `50 сум`; строка хранит количество, не сумму.

### Переключатель — сегменты, не тумблер

`ToggleSwitch` заменяется тремя `RadioButton Classes="Segmented"` —
`шт | {UnitShortName} | сум` — по образцу модалки скидки (`IsDiscountPercentMode` /
`IsDiscountAmountMode`). Второй сегмент скрыт при `!CanSwitchUnit`, третий при
`!CanEnterMoney`. Если оба скрыты — вся полоса сегментов скрыта, как сегодня тумблер
у штучного товара: инертный контрол читается как сломанный.

## Изменения в `QuantityPadViewModel`

```csharp
public enum PadMode { Pieces, Unit, Money }
```

| Член | Было | Стало |
|---|---|---|
| `bool EnteredInUnit` | `[ObservableProperty]` | заменяется на `PadMode Mode` (`[ObservableProperty]`, уведомляет те же превью-свойства плюс три bool ниже) |
| `IsPiecesMode` / `IsUnitMode` / `IsMoneyMode` | — | `bool` get/set над `Mode`; сеттер с `true` переводит режим, с `false` — ничего (так работает `RadioButton`) |
| `CanSwitchUnit` | есть | без изменений |
| `CanEnterMoney` | — | `Product.IsDivisible && _item.UnitPrice > 0m` |
| `HasModeSelector` | — | `CanSwitchUnit \|\| CanEnterMoney` — видимость полосы сегментов |
| `UnitLabel` | `"шт"` / `UnitShortName` | плюс `"сум"` в `Money` (хардкод, как `"шт"` сегодня). Подпись у поля ввода: `50 сум` |
| `PriceUnitLabel` | — | единица, в которой выведется количество: `UnitShortName` при `HasSecondaryUnit`, иначе `"шт"`. В `Pieces`/`Unit` совпадает с `UnitLabel`. Нужна, потому что заголовок сегодня биндится на `UnitLabel` и в `Money` прочитался бы как `30.00 / сум` |
| `PriceInSelectedUnit` | по `EnteredInUnit` | в `Money` — цена в единице вывода: `UnitPrice / UnitFactor` при `HasSecondaryUnit`, иначе `UnitPrice`. Заголовок `30.00 / кг` — кассир видит, на что делит |
| `IsValid` | парсинг + дробные штуки на неделимом | плюс в `Money`: выведенное количество `> 0` |
| `PreviewQuantity` / `PreviewQuantityInUnit` | по `EnteredInUnit` | в `Money` — через конвертацию суммы, дальше как в `Unit` / `Pieces` |
| `PreviewTotal` | `PreviewQuantity × UnitPrice` | без изменений — итог считается от штук, как в корзине |
| `PreviewText` | есть | без изменений формата: `→ 3.332 шт = 1.666 кг · 49.98` |
| `IsRounded` | округление вверх в `Unit` | без изменений; в `Money` всегда `false` |
| `IsRoundedDown` | — | `Mode == Money && amount × UnitPrice < money × factor` (точные произведения, не через `PreviewTotal` — тот идёт через 6-значные штуки) → подпись «Округлено вниз до грамма» |
| `Commit` | ветка по `EnteredInUnit` | третья ветка по таблице выше |

Конвертация `money → количество` — один приватный статический метод, чтобы превью и
`Commit` не разошлись:

```csharp
/// <summary>Where a money-derived amount is cut: scales weigh in grams, and
/// the cashier weighs out exactly the figure on screen.</summary>
private const decimal WeightStep = 0.001m;

private static decimal FloorToWeight(decimal value)
    => decimal.Truncate(value / WeightStep) * WeightStep;
```

## Изменения в `PosView.axaml`

В `Quantity Pad Modal` блок `<!-- Unit toggle -->`:

```xml
<Grid ColumnDefinitions="*, *, *" IsVisible="{Binding QuantityPad.HasModeSelector}">
    <RadioButton Grid.Column="0" Content="шт"
                 IsChecked="{Binding QuantityPad.IsPiecesMode}" Classes="Segmented"/>
    <RadioButton Grid.Column="1" Content="{Binding QuantityPad.Item.Product.UnitShortName}"
                 IsChecked="{Binding QuantityPad.IsUnitMode}" Classes="Segmented"
                 IsVisible="{Binding QuantityPad.CanSwitchUnit}"/>
    <RadioButton Grid.Column="2" Content="сум"
                 IsChecked="{Binding QuantityPad.IsMoneyMode}" Classes="Segmented"
                 IsVisible="{Binding QuantityPad.CanEnterMoney}"/>
</Grid>
```

Под превью — вторая подпись рядом с существующей `IsRounded`:

```xml
<TextBlock IsVisible="{Binding QuantityPad.IsRoundedDown}"
           Text="Округлено вниз до грамма" .../>
```

Заголовок `{PriceInSelectedUnit} / {UnitLabel}` → `{PriceInSelectedUnit} / {PriceUnitLabel}`.
Поле ввода (`{Input} {UnitLabel}`), нумпад, кнопки — без изменений.

Бинды в проекте рефлективные — неправильный путь собирается и молча не работает.
Каждое новое свойство проверяется в живом приложении, не только сборкой.

## Тесты

`tests/VvCash.Tests/QuantityPadTest.cs`, в стиле существующих (`PadFor`, `Tile`):

| Тест | Ожидание |
|---|---|
| деньги → вторая единица | товар 30/кг (цена за штуку 15, фактор 0.5, делимый), `Input = "50"`, `Mode = Money` → `PreviewQuantityInUnit == 1.666`, `PreviewTotal == 49.98`, `IsRoundedDown` |
| деньги → штуки без второй единицы | делимый товар по 30, без `UnitId`, `"50"` → `PreviewQuantity == 1.666`, `PreviewTotal == 49.98` |
| точное деление | `"60"` при 30/кг → `2`, `IsRoundedDown == false` |
| сумма меньше грамма | `"0.01"` при 30/кг → `IsValid == false` |
| неделимый товар | `CanEnterMoney == false` |
| бесплатный товар | `Price = 0`, делимый → `CanEnterMoney == false` |
| `Commit` со второй единицей | вызывает `SetQuantityInUnit(item, 1.666)`, `item.EnteredInUnit == true` |
| `Commit` без второй единицы | вызывает `SetQuantity(item, 1.666)`, `item.EnteredInUnit == false` |
| открытие пада | `Mode` никогда не `Money` при конструировании |

Существующие тесты на `EnteredInUnit` переписываются на `Mode`/`IsUnitMode` — поведение
`Pieces`/`Unit` не меняется.

## Вне рамок

- Округление штучных товаров (`50 / 3 → 16 шт`) — не просили.
- Хранение введённой суммы в строке / чеке.
- Подгонка итога строки под названную сумму (итог всегда `pieces × UnitPrice`).
- Локализация `"сум"` / «Округлено вниз до грамма» — по образцу существующих
  `"шт"` и «Округлено вверх до целой штуки», которые тоже не локализованы.
