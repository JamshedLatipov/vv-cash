# Формат телефона из настроек кассы — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Кассир выбирает страну на экране настроек, и от неё зависят маска ввода телефона, предел нумпада, код страны при отправке и распознавание телефона в строке поиска клиента.

**Architecture:** Каталог из трёх записей `PhoneFormat` в коде, `Id` выбранной записи лежит в локальном файле настроек, разрешение `Id → PhoneFormat` — чистая функция с падением на `RU`. Формат снимается окном регистрации один раз при открытии, как `PosViewModel` снимает фича-флаги.

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm 8.3.2, xUnit 2.9.2 без mock-библиотеки (фейки пишутся руками).

**Спека:** [2026-07-31-configurable-phone-format-design.md](../specs/2026-07-31-configurable-phone-format-design.md)

**Ветка:** работа идёт прямо в `main` — так решил владелец репозитория.

---

## Как собирать и запускать тесты

Из корня репозитория, инструментом PowerShell:

```bash
& ./run-tests.ps1
```

`pwsh ./run-tests.ps1` **не сработает** — `pwsh` на машине нет, несмотря на
shebang. Один класс:

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PhoneFormatTest"
```

Сборка приложения (нужна только там, где менялась разметка):

```bash
dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify --no-incremental
```

`-o build/verify` обязателен: запущенное приложение держит лок на обычной
выходной папке. `--no-incremental` обязателен при проверке предупреждений —
инкрементальная сборка пропускает `CoreCompile` и молча не переиздаёт их.

Вывод русский: `Пройден!` — прошло, `не пройдено N` — упало N,
`Сборка успешно завершена` — собралось.

**Разметку сборка не проверяет.** `src/VvCash/VvCash.csproj` ставит
`AvaloniaUseCompiledBindingsByDefault=false`, привязки рефлексивные: путь к
несуществующему свойству собирается чисто и молча ничего не рисует. Проверять
привязки чтением view model.

Предсуществующий флейк: `ExpenseDocumentServiceTest.SyncOfflineDocumentsAsync_401OnSecondDocument_...`
падает примерно раз на четыре прогона. Если упал — перезапустить, не чинить.

---

## Структура файлов

**Создаются:**

| Файл | Ответственность |
|---|---|
| `src/VvCash/Models/PhoneFormat.cs` | Тип формата, каталог из трёх записей, разрешение `Id → PhoneFormat`. Без зависимостей. |
| `tests/VvCash.Tests/PhoneFormatTest.cs` | Тесты маски, каталога и разрешения. |

**Меняются:**

| Файл | Что |
|---|---|
| `src/VvCash/Services/SettingsService.cs` | `SettingsData.PhoneFormatId` + свойство на сервисе. |
| `src/VvCash/Services/ISettingsService.cs` | `PhoneFormatId`. |
| 13 файлов тестов | Ручные фейки `ISettingsService` — по одной строке в каждый. |
| `tests/VvCash.Tests/SettingsDefaultsTest.cs` | Дефолт `PhoneFormatId`. |
| `src/VvCash/ViewModels/SettingsViewModel.cs` | Список форматов, выбранный формат, загрузка и сохранение. |
| `src/VvCash/Views/SettingsView.axaml` | `ComboBox` рядом с языком. |
| `src/VvCash/Assets/i18n/{ru,en,kk,tg,uz}.json` | Ключи `PhoneFormat` и `PhoneIncomplete`. |
| `src/VvCash/Models/CustomerPrefill.cs` | `FromSearchQuery` принимает `digitCount`. |
| `tests/VvCash.Tests/CustomerPrefillTest.cs` | Существующие случаи параметризуются, добавляются девятизначные. |
| `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs` | Потребление формата, отказ на неполном номере. |
| `src/VvCash/ViewModels/PosViewModel.cs` | Передача формата в оба места. |

---

## Task 1: `PhoneFormat` — тип, каталог, разрешение

**Files:**
- Create: `src/VvCash/Models/PhoneFormat.cs`
- Test: `tests/VvCash.Tests/PhoneFormatTest.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/VvCash.Tests/PhoneFormatTest.cs`:

```csharp
using System.Linq;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Маска телефона и каталог форматов. Всё здесь чистое — ни Avalonia,
/// ни настроек, ни сети.</summary>
public class PhoneFormatTest
{
    [Theory]
    [InlineData("RU", 10)]
    [InlineData("TJ", 9)]
    [InlineData("UZ", 9)]
    public void DigitCount_CountsPlaceholdersInMask(string id, int expected)
    {
        var format = PhoneFormats.Resolve(id);

        Assert.Equal(expected, format.DigitCount);
        Assert.Equal(expected, format.Mask.Count(c => c == '#'));
    }

    [Fact]
    public void Format_OnEmptyInput_EqualsPlaceholder()
    {
        var format = PhoneFormats.Resolve("TJ");

        Assert.Equal(format.Placeholder, format.Format(string.Empty));
        Assert.Equal("+992 (__) ___-__-__", format.Placeholder);
    }

    [Fact]
    public void Format_OnNull_EqualsPlaceholder()
    {
        var format = PhoneFormats.Resolve("RU");

        Assert.Equal(format.Placeholder, format.Format(null));
    }

    /// <summary>Цифры слева направо, литералы маски на местах, хвост —
    /// подчёркивания: кассир видит, сколько ещё набирать.</summary>
    [Fact]
    public void Format_OnPartialInput_FillsLeftToRight()
    {
        var format = PhoneFormats.Resolve("TJ");

        Assert.Equal("+992 (90) 12_-__-__", format.Format("9012"));
    }

    [Fact]
    public void Format_OnFullInput_LeavesNoPlaceholders()
    {
        var format = PhoneFormats.Resolve("RU");

        var result = format.Format("9001234567");

        Assert.Equal("+7 (900) 123-45-67", result);
        Assert.DoesNotContain('_', result);
    }

    /// <summary>Нумпад и так не даёт набрать лишнее, но формат не должен от
    /// этого зависеть: строка приходит и из префилла поиска.</summary>
    [Fact]
    public void Format_IgnoresDigitsBeyondTheMask()
    {
        var format = PhoneFormats.Resolve("TJ");

        Assert.Equal("+992 (90) 123-45-67", format.Format("901234567999"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XX")]
    public void Resolve_OnUnusableId_FallsBackToRu(string? id)
    {
        Assert.Same(PhoneFormats.Default, PhoneFormats.Resolve(id));
        Assert.Equal("RU", PhoneFormats.Resolve(id).Id);
    }

    /// <summary>Регистр не должен решать судьбу настройки, отредактированной
    /// руками в файле.</summary>
    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal("TJ", PhoneFormats.Resolve("tj").Id);
    }

    [Fact]
    public void Catalogue_HasUniqueIdsAndNoBlankFields()
    {
        Assert.Equal(3, PhoneFormats.All.Count);
        Assert.Equal(PhoneFormats.All.Count, PhoneFormats.All.Select(f => f.Id).Distinct().Count());

        foreach (var f in PhoneFormats.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Id));
            Assert.False(string.IsNullOrWhiteSpace(f.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(f.CountryCode));
            Assert.True(f.DigitCount > 0);
        }
    }

    [Fact]
    public void Default_IsPartOfTheCatalogue()
    {
        Assert.Contains(PhoneFormats.Default, PhoneFormats.All);
    }
}
```

- [ ] **Step 2: Убедиться, что тест не компилируется**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PhoneFormatTest"
```

Ожидается: ошибка сборки — типов `PhoneFormat` и `PhoneFormats` нет.

- [ ] **Step 3: Написать реализацию**

Создать `src/VvCash/Models/PhoneFormat.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace VvCash.Models;

/// <summary>Как на этой кассе выглядит телефон клиента: сколько в нём цифр, как
/// они группируются на экране и какой код страны приклеивается перед отправкой.
///
/// Конструктор, а не init-свойства как у CustomerPrefill: DigitCount считается
/// из Mask один раз, а объект с init-свойствами вычислить его при создании не
/// может. Записи каталога строятся только в коде и никогда не десериализуются,
/// так что терять поддержку object initializer здесь нечего.</summary>
public sealed class PhoneFormat
{
    /// <summary>В Mask символ '#' — место под цифру, всё остальное литерал.</summary>
    private const char DigitSlot = '#';

    public PhoneFormat(string id, string displayName, string countryCode, string mask)
    {
        Id = id;
        DisplayName = displayName;
        CountryCode = countryCode;
        Mask = mask;

        var count = 0;
        foreach (var c in mask)
        {
            if (c == DigitSlot) count++;
        }
        DigitCount = count;
    }

    /// <summary>То, что ложится в настройки. Хранится он, а не DisplayName:
    /// переименование страны в интерфейсе не должно ломать настроенную кассу.</summary>
    public string Id { get; }

    /// <summary>Не переводится и лежит в коде: это названия стран с кодами, они
    /// одинаково читаются на всех пяти языках, а пятнадцать ключей i18n ради
    /// строки «Таджикистан (+992)» — цена без выгоды.</summary>
    public string DisplayName { get; }

    /// <summary>Без плюса: приклеивается к цифрам перед отправкой на сервер.</summary>
    public string CountryCode { get; }

    /// <summary>Только национальная часть, без кода страны.</summary>
    public string Mask { get; }

    public int DigitCount { get; }

    /// <summary>Как выглядит пустое поле. Оно же — то, что видит кассир до
    /// первого нажатия на нумпад.</summary>
    public string Placeholder => Format(string.Empty);

    /// <summary>Раскладывает набранные цифры по маске слева направо; на
    /// незанятые места ставит подчёркивания, чтобы было видно, сколько ещё
    /// набирать. Лишние цифры сверх DigitCount отбрасываются — строка приходит
    /// не только с нумпада, но и из префилла строки поиска.</summary>
    public string Format(string? digits)
    {
        var entered = digits ?? string.Empty;
        var result = new StringBuilder("+").Append(CountryCode).Append(' ');

        var next = 0;
        foreach (var c in Mask)
        {
            if (c == DigitSlot)
            {
                result.Append(next < entered.Length ? entered[next] : '_');
                next++;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}

/// <summary>Каталог форматов. Не редактируется из интерфейса сознательно:
/// кассир не должен иметь возможности задать маску, которой не бывает. Новая
/// страна — правка этого файла и релиз.</summary>
public static class PhoneFormats
{
    /// <summary>Казахстан не отдельной записью: там тот же +7 и те же десять
    /// цифр, отдельный пункт делал бы вид, что выбор на что-то влияет.</summary>
    public static readonly PhoneFormat Russia =
        new("RU", "Россия / Казахстан (+7)", "7", "(###) ###-##-##");

    public static readonly PhoneFormat Tajikistan =
        new("TJ", "Таджикистан (+992)", "992", "(##) ###-##-##");

    public static readonly PhoneFormat Uzbekistan =
        new("UZ", "Узбекистан (+998)", "998", "(##) ###-##-##");

    public static IReadOnlyList<PhoneFormat> All { get; } =
        new[] { Russia, Tajikistan, Uzbekistan };

    /// <summary>Чем становится касса, где настройка не задана. Он же ответ на
    /// настройку, оставшуюся от удалённой записи каталога.</summary>
    public static PhoneFormat Default => Russia;

    /// <summary>Единственное место, где Id превращается в формат. Функцией, а не
    /// веткой на месте использования: правило «пусто или незнакомо — значит RU»
    /// должно быть одно и проверяться тестом без файловой системы.</summary>
    public static PhoneFormat Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var format in All)
            {
                if (string.Equals(format.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }
        }

        return Default;
    }
}
```

- [ ] **Step 4: Прогнать тесты**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~PhoneFormatTest"
```

Ожидается: `не пройдено 0, пройдено 15` (3 + 4 случая из двух `[Theory]` плюс 8 `[Fact]`).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/PhoneFormat.cs tests/VvCash.Tests/PhoneFormatTest.cs
git commit -m "feat(phone): add a catalogue of phone formats with a mask"
```

---

## Task 2: Настройка в файле настроек

**Files:**
- Modify: `src/VvCash/Services/SettingsService.cs`
- Modify: `src/VvCash/Services/ISettingsService.cs`
- Modify: 13 файлов тестов с ручными фейками
- Test: `tests/VvCash.Tests/SettingsDefaultsTest.cs`

Свойство добавляется в интерфейс, а не только в `SettingsService`: `PosViewModel`
и `SettingsViewModel` работают через интерфейс. Это ломает четырнадцать ручных
фейков — так же, как их ломало добавление `ExchangePayoutCategoryId`, которое в
них уже есть. Правка в каждом одна строка.

- [ ] **Step 1: `SettingsData`**

В `src/VvCash/Services/SettingsService.cs` после
`public string ExchangePayoutCategoryId { get; set; } = string.Empty;` добавить:

```csharp
    public string PhoneFormatId { get; set; } = string.Empty;
```

- [ ] **Step 2: Свойство на сервисе**

В том же файле после свойства `ExchangePayoutCategoryId` класса `SettingsService`
добавить:

```csharp
    public string PhoneFormatId
    {
        get => _data.PhoneFormatId;
        set => _data.PhoneFormatId = value;
    }
```

- [ ] **Step 3: Интерфейс**

В `src/VvCash/Services/ISettingsService.cs` после свойства
`ExchangePayoutCategoryId` добавить:

```csharp
    /// <summary>Id записи из PhoneFormats — какой формат телефона у клиентов
    /// этой кассы. Пусто на кассе, где настройку не трогали; PhoneFormats.Resolve
    /// читает пустое и незнакомое как Россию, поэтому обновление существующей
    /// кассы ничего не меняет.</summary>
    string PhoneFormatId { get; set; }
```

- [ ] **Step 4: Починить четырнадцать фейков**

В каждый из перечисленных классов добавить строку рядом с остальными
свойствами-заглушками:

```csharp
        public string PhoneFormatId { get; set; } = string.Empty;
```

Файлы и классы:

| Файл | Класс |
|---|---|
| `tests/VvCash.Tests/AuthServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/CashOperationServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/CounterpartyServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/ExchangeViewModelTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/ExpenseDocumentServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/PaymentCategoryServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/PosViewModelSellerGateTest.cs` | `FakeSettingsService` |
| `tests/VvCash.Tests/QuoteServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/ReturnServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/ReturnsViewModelTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/SellerRosterServiceTest.cs` | `FakeSettings` **и** `ThrowingBackendUrlSettings` |
| `tests/VvCash.Tests/ShiftServiceTest.cs` | `FakeSettings` |
| `tests/VvCash.Tests/SyncServiceTest.cs` | `FakeSettings` |

Внимание: в `SellerRosterServiceTest.cs` **два** класса. `ThrowingBackendUrlSettings`
бросает из `BackendUrl` намеренно — новое свойство должно быть обычной
автосвойством, не бросать.

- [ ] **Step 5: Тест дефолта**

В `tests/VvCash.Tests/SettingsDefaultsTest.cs` добавить:

```csharp
    /// <summary>Пусто, а не "RU": дефолт живёт в PhoneFormats.Resolve, и второй
    /// его экземпляр здесь разъехался бы с первым при первой же правке.</summary>
    [Fact]
    public void PhoneFormat_DefaultsToUnset()
    {
        Assert.Equal(string.Empty, new SettingsData().PhoneFormatId);
    }
```

- [ ] **Step 6: Прогнать весь набор**

```bash
& ./run-tests.ps1
```

Ожидается: собирается, `не пройдено 0`. До этой задачи набор был на 464 теста;
теперь 464 + 15 из Task 1 + 1 = 480.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Services tests/VvCash.Tests
git commit -m "feat(phone): store the chosen phone format in the register settings"
```

---

## Task 3: Строки локализации

**Files:**
- Modify: `src/VvCash/Assets/i18n/ru.json`
- Modify: `src/VvCash/Assets/i18n/en.json`
- Modify: `src/VvCash/Assets/i18n/kk.json`
- Modify: `src/VvCash/Assets/i18n/tg.json`
- Modify: `src/VvCash/Assets/i18n/uz.json`

Два ключа: подпись выпадающего списка на экране настроек и сообщение о неполном
номере. Названия стран в список **не** попадают — они в `PhoneFormat.DisplayName`.

Отсутствующий ключ `I18nService` рисует как литерал `[Ключ]` — пропуск файла
будет виден на экране, но только в этой локали.

- [ ] **Step 1: `ru.json`**

Найти `"AddCustomer": "Добавить клиента",` и вставить после блока клиентских
ключей, сразу за `"CustomerCreateFailed": ...`:

```json
  "PhoneFormat": "Формат телефона",
  "PhoneIncomplete": "Номер телефона введён не полностью",
```

- [ ] **Step 2: `en.json`**

После `"CustomerCreateFailed": ...`:

```json
  "PhoneFormat": "Phone format",
  "PhoneIncomplete": "The phone number is incomplete",
```

- [ ] **Step 3: `kk.json`**

После `"CustomerCreateFailed": ...`:

```json
  "PhoneFormat": "Телефон пішімі",
  "PhoneIncomplete": "Телефон нөмірі толық енгізілмеген",
```

- [ ] **Step 4: `tg.json`**

После `"CustomerCreateFailed": ...`:

```json
  "PhoneFormat": "Формати телефон",
  "PhoneIncomplete": "Рақами телефон пурра ворид нашудааст",
```

- [ ] **Step 5: `uz.json`**

После `"CustomerCreateFailed": ...`:

```json
  "PhoneFormat": "Telefon formati",
  "PhoneIncomplete": "Telefon raqami to'liq kiritilmagan",
```

- [ ] **Step 6: Проверить пять файлов**

Инструментом PowerShell:

```bash
foreach ($l in 'ru','en','kk','tg','uz') { $j = Get-Content "src/VvCash/Assets/i18n/$l.json" -Raw -Encoding utf8 | ConvertFrom-Json; foreach ($k in 'PhoneFormat','PhoneIncomplete') { if (-not $j.$k) { throw "$l missing $k" } }; "$l ok" }
```

Ожидается: пять строк `ru ok` … `uz ok`.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Assets/i18n
git commit -m "i18n(phone): add the format label and the incomplete-number message"
```

---

## Task 4: Выбор формата на экране настроек

**Files:**
- Modify: `src/VvCash/ViewModels/SettingsViewModel.cs`
- Modify: `src/VvCash/Views/SettingsView.axaml`

- [ ] **Step 1: Свойства view model**

В `src/VvCash/ViewModels/SettingsViewModel.cs` после
`public ObservableCollection<string> AvailableLanguages { get; } = new() { "ru", "en", "tg", "uz", "kk" };`
и следующего за ним `SelectedLanguage` добавить:

```csharp
    /// <summary>Каталог целиком: он неизменен и не зависит от сети, поэтому
    /// подгружать его нечем и незачем.</summary>
    public IReadOnlyList<PhoneFormat> AvailablePhoneFormats { get; } = PhoneFormats.All;

    [ObservableProperty]
    private PhoneFormat _selectedPhoneFormat = PhoneFormats.Default;
```

Добавить в начало файла `using VvCash.Models;` и `using System.Collections.Generic;`,
если их там нет — `PhoneFormat`/`PhoneFormats` из первого, `IReadOnlyList` из второго.

- [ ] **Step 2: Загрузка**

Рядом со строкой
`SelectedLanguage = string.IsNullOrEmpty(_settingsService.Language) ? "ru" : _settingsService.Language;`
добавить:

```csharp
        SelectedPhoneFormat = PhoneFormats.Resolve(_settingsService.PhoneFormatId);
```

Без собственной ветки на пустоту: `Resolve` для того и существует.

- [ ] **Step 3: Сохранение**

Рядом со строкой `_settingsService.Language = SelectedLanguage;` в методе `Save`
добавить:

```csharp
        _settingsService.PhoneFormatId = SelectedPhoneFormat.Id;
```

Сохраняется `Id`, а не объект: в файле настроек лежит строка.

- [ ] **Step 4: Разметка**

В `src/VvCash/Views/SettingsView.axaml` после блока выбора языка (`<ComboBox
ItemsSource="{Binding AvailableLanguages}" …/>`) и перед комментарием
`<!-- Sync Interval Input -->` вставить:

```xml
                        <!-- Формат телефона: от него зависят маска ввода, предел нумпада
                             и код страны, который уходит на сервер. -->
                        <TextBlock Text="{Binding [PhoneFormat], Source={x:Static services:I18nService.Instance}}" FontSize="14" FontWeight="SemiBold" Foreground="{StaticResource Slate700Brush}"/>
                        <ComboBox ItemsSource="{Binding AvailablePhoneFormats}" SelectedItem="{Binding SelectedPhoneFormat, Mode=TwoWay}" Classes="SettingsCombo">
                            <ComboBox.ItemTemplate>
                                <DataTemplate>
                                    <TextBlock Text="{Binding DisplayName}"/>
                                </DataTemplate>
                            </ComboBox.ItemTemplate>
                        </ComboBox>
```

`ItemTemplate`, а не привязка к строке как у языков: в списке лежат объекты, и
показать надо `DisplayName`, а сохранить `Id`.

- [ ] **Step 5: Собрать**

```bash
dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify --no-incremental
```

Ожидается: `Сборка успешно завершена`, `Ошибок: 0`.

Сборка проверит только ресурсы и синтаксис. Привязки `AvailablePhoneFormats`,
`SelectedPhoneFormat` и `DisplayName` сверить чтением: первые две — на
`SettingsViewModel` из Step 1, третья — на `PhoneFormat` из Task 1.

- [ ] **Step 6: Прогнать весь набор**

```bash
& ./run-tests.ps1
```

Ожидается: `не пройдено 0`, 480 тестов.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/ViewModels/SettingsViewModel.cs src/VvCash/Views/SettingsView.axaml
git commit -m "feat(phone): let the register pick its phone format in settings"
```

---

## Task 5: Формат доходит до ввода телефона

**Files:**
- Modify: `src/VvCash/Models/CustomerPrefill.cs`
- Modify: `tests/VvCash.Tests/CustomerPrefillTest.cs`
- Modify: `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs`
- Modify: `src/VvCash/ViewModels/PosViewModel.cs`

Три файла кода в одной задаче потому, что сигнатуры ломают друг друга:
`CustomerPrefill.FromSearchQuery` и конструктор `CustomerRegistrationViewModel`
оба вызываются из одного метода `PosViewModel.ShowCustomerRegistrationAsync`.
Разнести их — значит оставить дерево несобираемым между задачами.

- [ ] **Step 1: Переписать тесты префилла**

В `tests/VvCash.Tests/CustomerPrefillTest.cs` заменить два теста, завязанных на
десять цифр, и добавить девятизначные. Заменить целиком:

```csharp
    [Theory]
    [InlineData("7 900 123 45 67")]   // как показывает FormattedPhoneNumber
    [InlineData("+7 (900) 123-45-67")]
    [InlineData("89001234567")]        // с восьмёркой
    [InlineData("9001234567")]         // ровно десять
    public void TenOrMoreDigits_GoToPhone(string query)
    {
        var prefill = CustomerPrefill.FromSearchQuery(query);

        Assert.Equal("9001234567", prefill.PhoneNumber);
        Assert.Equal(string.Empty, prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
    }
```

на:

```csharp
    [Theory]
    [InlineData("7 900 123 45 67")]   // как показывает FormattedPhoneNumber
    [InlineData("+7 (900) 123-45-67")]
    [InlineData("89001234567")]        // с восьмёркой
    [InlineData("9001234567")]         // ровно десять
    public void EnoughDigits_GoToPhone(string query)
    {
        var prefill = CustomerPrefill.FromSearchQuery(query, 10);

        Assert.Equal("9001234567", prefill.PhoneNumber);
        Assert.Equal(string.Empty, prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
    }

    /// <summary>Ради чего задача и делается: на таджикской кассе девять цифр —
    /// это полный номер, а не обрывок, и в имя он уезжать не должен.</summary>
    [Theory]
    [InlineData("901234567", "901234567")]
    [InlineData("+992 (90) 123-45-67", "901234567")]
    [InlineData("992901234567", "901234567")]
    public void NineDigitFormat_TakesNineDigits(string query, string expected)
    {
        var prefill = CustomerPrefill.FromSearchQuery(query, 9);

        Assert.Equal(expected, prefill.PhoneNumber);
        Assert.Equal(string.Empty, prefill.FirstName);
    }

    /// <summary>Тот же ввод при другом формате читается иначе — десятизначная
    /// касса видит в девяти цифрах не номер.</summary>
    [Fact]
    public void SameQuery_ReadsDifferentlyPerFormat()
    {
        Assert.Equal("901234567", CustomerPrefill.FromSearchQuery("901234567", 9).PhoneNumber);
        Assert.Equal(string.Empty, CustomerPrefill.FromSearchQuery("901234567", 10).PhoneNumber);
    }
```

Затем заменить:

```csharp
    [Fact]
    public void LongDigitString_TakesLastTenDigits()
    {
        var prefill = CustomerPrefill.FromSearchQuery("11111111112222222222");

        Assert.Equal("2222222222", prefill.PhoneNumber);
    }
```

на:

```csharp
    [Fact]
    public void LongDigitString_TakesTheLastDigits()
    {
        var prefill = CustomerPrefill.FromSearchQuery("11111111112222222222", 10);

        Assert.Equal("2222222222", prefill.PhoneNumber);
    }
```

и:

```csharp
    [Fact]
    public void FewerThanTenDigits_NotTreatedAsPhone()
    {
        var prefill = CustomerPrefill.FromSearchQuery("12345");

        Assert.Equal(string.Empty, prefill.PhoneNumber);
        Assert.Equal("12345", prefill.FirstName);
    }
```

на:

```csharp
    [Fact]
    public void FewerDigitsThanTheFormat_NotTreatedAsPhone()
    {
        var prefill = CustomerPrefill.FromSearchQuery("12345", 10);

        Assert.Equal(string.Empty, prefill.PhoneNumber);
        Assert.Equal("12345", prefill.FirstName);
    }
```

Во **всех** остальных вызовах `CustomerPrefill.FromSearchQuery(...)` в этом файле
дописать вторым аргументом `10` — поведение этих случаев от формата не зависит,
и десятка там просто сохраняет прежний смысл теста.

- [ ] **Step 2: Убедиться, что тесты не компилируются**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerPrefillTest"
```

Ожидается: ошибка сборки — `FromSearchQuery` принимает один аргумент.

- [ ] **Step 3: Переписать `CustomerPrefill.FromSearchQuery`**

В `src/VvCash/Models/CustomerPrefill.cs` заменить сигнатуру и две строки, где
зашита десятка:

```csharp
    /// <param name="digitCount">Сколько цифр в полном национальном номере на
    /// этой кассе — из PhoneFormat. Порог «это телефон» и длина среза берутся
    /// отсюда: на девятизначной кассе десятка отправляла бы полный местный номер
    /// в имя.</param>
    public static CustomerPrefill FromSearchQuery(string? query, int digitCount)
    {
        if (string.IsNullOrWhiteSpace(query)) return Empty;

        // Порог по числу цифр, а не «строка состоит только из цифр»: кассир
        // набирает телефон и как «+7 (900) 123-45-67». Берутся последние
        // digitCount, чтобы код страны в начале не сдвигал номер.
        var digits = new string(query.Where(char.IsDigit).ToArray());
        if (digits.Length >= digitCount)
        {
            return new CustomerPrefill { PhoneNumber = digits[^digitCount..] };
        }

        // null как разделитель — это split по любому пробельному символу.
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return new CustomerPrefill
        {
            FirstName = words[0],
            LastName = string.Join(' ', words.Skip(1)),
        };
    }
```

- [ ] **Step 4: Формат в окне регистрации**

В `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs`:

Добавить поле рядом с `_counterpartyService`:

```csharp
    /// <summary>Снимок формата на момент открытия окна — как PosViewModel
    /// снимает фича-флаги. Формат не может измениться, пока окно открыто:
    /// меняют его на экране настроек, а он модальный.</summary>
    private readonly PhoneFormat _phoneFormat;
```

Заменить конструктор:

```csharp
    public CustomerRegistrationViewModel(Window window, ICounterpartyService counterpartyService, ISettingsService settingsService)
    {
        _window = window;
        _counterpartyService = counterpartyService;
        _phoneFormat = PhoneFormats.Resolve(settingsService.PhoneFormatId);
    }
```

Заменить весь блок `FormattedPhoneNumber` (тридцать строк ручной сборки) на:

```csharp
    public string FormattedPhoneNumber => _phoneFormat.Format(PhoneNumber);
```

Заменить тело `Numpad`:

```csharp
    [RelayCommand]
    private void Numpad(string digit)
    {
        if (PhoneNumber.Length < _phoneFormat.DigitCount)
        {
            PhoneNumber += digit;
        }
    }
```

В `SubmitAsync` сразу после `ErrorMessage = null;` добавить отказ на неполном
номере:

```csharp
        // Пустой телефон законен — клиент без телефона нормальная запись. А вот
        // начатый и не дописанный раньше молча превращался в Phone = null:
        // кассир набирал восемь цифр из девяти, жал «Сохранить» и получал
        // клиента без телефона, ничего об этом не узнав.
        if (PhoneNumber.Length > 0 && PhoneNumber.Length != _phoneFormat.DigitCount)
        {
            ErrorMessage = I18nService.Instance["PhoneIncomplete"];
            return;
        }
```

И заменить строку сборки телефона в `request`:

```csharp
            Phone = PhoneNumber.Length == _phoneFormat.DigitCount ? _phoneFormat.CountryCode + PhoneNumber : null,
```

- [ ] **Step 5: Передать формат из `PosViewModel`**

В `src/VvCash/ViewModels/PosViewModel.cs` заменить тело
`ShowCustomerRegistrationAsync`:

```csharp
    private async Task<CounterpartyResponse?> ShowCustomerRegistrationAsync(Avalonia.Controls.Window owner, string searchQuery)
    {
        var phoneFormat = PhoneFormats.Resolve(_settingsService.PhoneFormatId);

        var dialog = new VvCash.Views.CustomerRegistrationWindow();
        var vm = new CustomerRegistrationViewModel(dialog, _counterpartyService, _settingsService);
        vm.ApplyPrefill(CustomerPrefill.FromSearchQuery(searchQuery, phoneFormat.DigitCount));
        dialog.DataContext = vm;

        // as, а не каст: окно закрывается либо созданным клиентом, либо null, но
        // ошибиться здесь означало бы уронить кассу на InvalidCastException.
        // Тот же приём уже применён в OpenParkedSales.
        return await dialog.ShowDialog<object>(owner) as CounterpartyResponse;
    }
```

`_settingsService` в `PosViewModel` уже есть — это поле из конструктора.

- [ ] **Step 6: Прогнать весь набор**

```bash
& ./run-tests.ps1
```

Ожидается: собирается, `не пройдено 0`. Тестов 480 + 4 новых из Step 1 = 484.

- [ ] **Step 7: Чистая сборка на предупреждения**

```bash
dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify --no-incremental
```

Ожидается: `Ошибок: 0`, три предсуществующих предупреждения (NU1903 ×2 и CS0067
в `MockPrinterService`), ни одного на изменённых файлах.

- [ ] **Step 8: Коммит**

```bash
git add src/VvCash/Models/CustomerPrefill.cs src/VvCash/ViewModels/CustomerRegistrationViewModel.cs src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/CustomerPrefillTest.cs
git commit -m "feat(phone): drive the number field from the register's chosen format"
```

---

## Task 6: Ручная проверка на запущенной кассе

**Files:** нет

`CustomerRegistrationViewModel` автотестом не покрывается — его конструктор
требует непустой `Avalonia.Controls.Window`. Всё, что ниже, проверяется только
руками.

- [ ] **Step 1: Запустить приложение**

```bash
dotnet run --project src/VvCash/VvCash.csproj
```

- [ ] **Step 2: Пройти сценарии**

1. Настройки → выбрать Таджикистан → сохранить → открыть регистрацию клиента:
   пустое поле показывает `+992 (__) ___-__-__`, нумпад останавливается на
   девятой цифре.
2. Ввести полный номер, сохранить, проверить в базе, что телефон записан с
   префиксом `992`.
3. Ввести восемь цифр, нажать «Сохранить»: окно осталось открытым, показано
   «Номер телефона введён не полностью», введённое не потеряно.
4. Оставить телефон пустым, сохранить: клиент создаётся, отказа нет.
5. Поиск клиента по девятизначному номеру → «Создать клиента»: телефон
   подставился в форму, имя пустое.
6. Вернуть Россию в настройках: плейсхолдер и предел вернулись к десяти цифрам.
7. Взять кассу, где настройка не задана (или очистить `PhoneFormatId` в файле
   настроек вручную): формат `RU`, поведение как до задачи.

- [ ] **Step 3: Отметить результат**

Сценарий, который не удалось проверить, записать явно в описании изменения — не
пропускать молча.

---

## Self-review

**Покрытие спеки:**

| Раздел спеки | Задача |
|---|---|
| Тип `PhoneFormat` с конструктором и `DigitCount` | Task 1 |
| Каталог из трёх записей, `Default`, `Resolve` | Task 1 |
| `Format` и `Placeholder` | Task 1 |
| `SettingsData.PhoneFormatId`, интерфейс, сервис | Task 2 |
| Дефолт пустой, разрешается в `RU` | Task 1 (`Resolve`), Task 2 (`SettingsDefaultsTest`) |
| `ComboBox` на экране настроек с `ItemTemplate` | Task 4 |
| Потребление в `FormattedPhoneNumber`, `Numpad`, `SubmitAsync` | Task 5 |
| `CustomerPrefill.FromSearchQuery(query, digitCount)` | Task 5 |
| Отказ на неполном номере, ключ `PhoneIncomplete` | Task 3, Task 5 |
| Ручная проверка вместо теста регистрации | Task 6 |

**Согласованность имён:** `PhoneFormats.Resolve` (Task 1) зовётся из
`SettingsViewModel` (Task 4), `CustomerRegistrationViewModel` (Task 5) и
`PosViewModel` (Task 5). `PhoneFormat.DigitCount` читается в `Numpad`,
`SubmitAsync` и при вызове `FromSearchQuery`. `PhoneFormat.CountryCode` — только
в `SubmitAsync`. `SettingsData.PhoneFormatId` и `ISettingsService.PhoneFormatId`
названы одинаково. Ключи i18n `PhoneFormat` и `PhoneIncomplete` из Task 3
используются в Task 4 и Task 5 соответственно.

**Счётчики тестов:** 464 до начала → +15 (Task 1) → +1 (Task 2) → +4 (Task 5) =
484. Если реальный набор разойдётся, верить прогону, а не этой строке.

## Правки по ходу исполнения

**Task 1 — `DisplayName` был нечитаем в латинских локалях.** План (и спека)
обосновывали отсутствие перевода тем, что названия стран одинаково читаются на
всех пяти языках. Неверно: локали `uz` и `en` латинские, и список целиком
кириллицей их кассир не прочтёт. Строки перестроены — впереди код набора,
который от письменности не зависит, латинские ISO-коды в скобках: `+7 — Россия /
Казахстан (RU / KZ)`. Ключей i18n по-прежнему ноль. Заодно поле `PhoneFormats.Russia`
переименовано в `RussiaKazakhstan` — старое имя обещало меньше, чем запись
покрывает (`009ebf8`).

**Счётчики в шагах устарели.** Пока шла работа, в `main` влилась чужая ветка
(`feat/exchange-seller-gate-and-search`) и принесла 24 теста. База сдвинулась
464 → 504, итог по фиче — **521**, а не 484. Верить прогону, а не шагам плана.

**Финальное ревью, семь правок.** Главная: сообщение «номер введён не полностью»
гасло только на следующем `SubmitAsync`, поэтому кассир, дописавший девятую
цифру, продолжал видеть его над уже полным номером — и читал это как «форма
застряла». Снимается теперь в `OnPhoneNumberChanged` (`e68511c`).

`CustomerRegistrationViewModel` развязан от `Window` на делегат `_close` — тот же
приём, что у соседнего `CustomerSearchViewModel`, где он появился фичей раньше и
сюда не был перенесён. Это открыло для тестов всё, что задача добавила:
предел нумпада, отказ на неполном номере, приём пустого телефона, сборку
`CountryCode + цифры`. **13 новых тестов** (`1c75b51`). Гейт проверен мутацией:
без правки `e68511c` падает `EditingTheNumberAfterRefusal_ClearsTheMessage`.

Плюс четыре мелочи (`c656636`): формат разрешался дважды на одном пути — теперь
один раз, в конструкторе, а `PosViewModel` читает `vm.PhoneDigitCount`;
`SelectedPhoneFormat` объявлен nullable и запись в настройки защищена, как у
соседнего `SelectedPaymentCategory` — Avalonia сбрасывает `SelectedItem` в null
и пишет его обратно, если значение не найдено в `ItemsSource`; `Load()` чинит
`PhoneFormatId`, как уже чинил трёх соседей; `ItemTemplate` в разметке заменён
на `DisplayMemberBinding` — одна строка вместо шести и на одну рефлексивную
привязку меньше.

**Task 1 — три мелочи от ревью, каждая проверена мутацией.** `Assert.Equal(3, All.Count)`
убран: пропажу записи и так ловит теория по `DigitCount`, дубликаты — соседняя
строка, а единственный оставшийся сценарий даёт сообщение, которое подталкивает
поправить тройку вместо того, чтобы дописать теорию. `All` завёрнут в
`Array.AsReadOnly` — до этого `IReadOnlyList` поверх голого массива приводился
обратно и переписывался, хотя комментарий типа обещал обратное. `Placeholder`
переехал в конструктор к `DigitCount`: оба выводятся из `Mask`, и объяснение
«вывод принадлежит конструктору» в доке уже стояло, но относилось только к
одному из двух.
