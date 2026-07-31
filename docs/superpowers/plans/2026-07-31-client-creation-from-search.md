# Создание клиента из окна поиска — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Кассир может создать клиента прямо из окна поиска — из пустого состояния и из постоянной кнопки в футере, — и созданный клиент сразу попадает в чек по обоим путям регистрации.

**Architecture:** `CustomerSearchViewModel` развязывается от `Window` на два делегата (`close`, `createCustomer`), получает `HasSearched`/`HasNoResults` и команду создания. Окно регистрации открывает `PosViewModel` поверх окна поиска, предзаполняя форму разбором строки поиска (чистый `CustomerPrefill`). Подстановка клиента в чек сводится в один метод `PosViewModel.ApplySelectedCustomer`, который теперь зовут оба пути.

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm 8.3.2 (source generators: `[ObservableProperty]`, `[RelayCommand]`), xUnit 2.9.2 без mock-библиотеки (фейки пишутся руками).

**Спека:** [2026-07-31-client-creation-from-search-design.md](../specs/2026-07-31-client-creation-from-search-design.md)

---

## Как запускать тесты

Из корня репозитория, инструментом PowerShell:

```bash
& ./run-tests.ps1
```

Скрипт собирает в `build/verify-tests`, чтобы запущенное приложение не держало
лок на выходной папке. Запуск `pwsh ./run-tests.ps1` **не сработает** — `pwsh`
на машине нет, несмотря на shebang в файле. Один класс тестов:

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerPrefillTest"
```

---

## Структура файлов

**Создаются:**

| Файл | Ответственность |
|---|---|
| `src/VvCash/Models/CustomerPrefill.cs` | Чистый разбор строки поиска в поля формы регистрации. Без зависимостей. |
| `tests/VvCash.Tests/CustomerPrefillTest.cs` | Тесты разбора. |
| `tests/VvCash.Tests/CustomerSearchViewModelTest.cs` | Тесты пустого состояния и потока создания. |

**Меняются:**

| Файл | Что |
|---|---|
| `src/VvCash/ViewModels/CustomerSearchViewModel.cs` | Конструктор на делегатах, `HasSearched`/`HasNoResults`/`IsCreateEnabled`, `CreateCustomerCommand`. |
| `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs` | Метод `ApplyPrefill`. |
| `src/VvCash/ViewModels/PosViewModel.cs` | `ApplySelectedCustomer`, `ShowCustomerRegistrationAsync`, переписанные `OpenCustomerSearch` и `OpenCustomerRegistration`. |
| `src/VvCash/Views/CustomerSearchWindow.axaml` | Пустое состояние, кнопка в футере. |
| `src/VvCash/Assets/i18n/{ru,en,kk,tg,uz}.json` | Три новых ключа. |

---

## Task 1: `CustomerPrefill` — разбор строки поиска

**Files:**
- Create: `src/VvCash/Models/CustomerPrefill.cs`
- Test: `tests/VvCash.Tests/CustomerPrefillTest.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/VvCash.Tests/CustomerPrefillTest.cs`:

```csharp
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

/// <summary>Разбор строки поиска в поля формы регистрации. Кассир уже набрал
/// запрос — телефон или имя, — и второй раз набирать его не должен.</summary>
public class CustomerPrefillTest
{
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

    [Fact]
    public void SingleWord_GoesToFirstName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
        Assert.Equal(string.Empty, prefill.PhoneNumber);
    }

    [Fact]
    public void TwoWords_SplitIntoFirstAndLastName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван Петров");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петров", prefill.LastName);
    }

    /// <summary>Отчество остаётся в фамилии, а не теряется: форма регистрации
    /// поля для отчества не имеет, и потерять введённое кассиром хуже, чем
    /// склеить.</summary>
    [Fact]
    public void ThreeWords_TailGoesToLastName()
    {
        var prefill = CustomerPrefill.FromSearchQuery("Иван Петрович Петров");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петрович Петров", prefill.LastName);
    }

    [Fact]
    public void ExtraWhitespace_Ignored()
    {
        var prefill = CustomerPrefill.FromSearchQuery("  Иван   Петров  ");

        Assert.Equal("Иван", prefill.FirstName);
        Assert.Equal("Петров", prefill.LastName);
    }

    /// <summary>Меньше десяти цифр — это не телефон, а, например, номер карты
    /// или обрывок ввода. Уходит в имя, где кассир его увидит и поправит.</summary>
    [Fact]
    public void FewerThanTenDigits_NotTreatedAsPhone()
    {
        var prefill = CustomerPrefill.FromSearchQuery("12345");

        Assert.Equal(string.Empty, prefill.PhoneNumber);
        Assert.Equal("12345", prefill.FirstName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_GivesEmptyPrefill(string? query)
    {
        var prefill = CustomerPrefill.FromSearchQuery(query);

        Assert.Equal(string.Empty, prefill.FirstName);
        Assert.Equal(string.Empty, prefill.LastName);
        Assert.Equal(string.Empty, prefill.PhoneNumber);
    }
}
```

- [ ] **Step 2: Убедиться, что тест не компилируется**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerPrefillTest"
```

Ожидается: ошибка сборки `error CS0246: The type or namespace name 'CustomerPrefill' could not be found`.

- [ ] **Step 3: Написать реализацию**

Создать `src/VvCash/Models/CustomerPrefill.cs`:

```csharp
using System;
using System.Linq;

namespace VvCash.Models;

/// <summary>Что строка поиска отдаёт пустой форме регистрации, когда кассир
/// искал клиента, не нашёл и жмёт «Создать». Тип намеренно ничего не знает ни
/// о view model, ни о формате API: единственное здесь решение — что считать
/// телефоном, а что именем, — должно проверяться без Avalonia и без сети.</summary>
public sealed class CustomerPrefill
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;

    /// <summary>Ровно десять цифр или пусто. CustomerRegistrationViewModel.SubmitAsync
    /// отправляет телефон только при длине 10 и сам приклеивает код страны,
    /// поэтому хранить здесь что-то другое бессмысленно.</summary>
    public string PhoneNumber { get; init; } = string.Empty;

    public static readonly CustomerPrefill Empty = new();

    public static CustomerPrefill FromSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Empty;

        // Порог по числу цифр, а не «строка состоит только из цифр»: кассир
        // набирает телефон и как «+7 (900) 123-45-67». Берутся последние десять,
        // чтобы ведущие 7/8 не сдвигали номер.
        var digits = new string(query.Where(char.IsDigit).ToArray());
        if (digits.Length >= 10)
        {
            return new CustomerPrefill { PhoneNumber = digits[^10..] };
        }

        // null как разделитель — это split по любому пробельному символу.
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Empty;

        return new CustomerPrefill
        {
            FirstName = words[0],
            LastName = string.Join(' ', words.Skip(1)),
        };
    }
}
```

- [ ] **Step 4: Прогнать тесты**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerPrefillTest"
```

Ожидается: `Passed! - Failed: 0, Passed: 12` (4 + 3 случая из двух `[Theory]`
плюс 5 `[Fact]`).

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/Models/CustomerPrefill.cs tests/VvCash.Tests/CustomerPrefillTest.cs
git commit -m "feat(customer): parse a search query into registration form fields"
```

---

## Task 2: `CustomerSearchViewModel` — развязка от `Window`, пустое состояние, создание

**Files:**
- Modify: `src/VvCash/ViewModels/CustomerSearchViewModel.cs` (файл переписывается целиком)
- Test: `tests/VvCash.Tests/CustomerSearchViewModelTest.cs`

После этого шага решение **временно не собирается**: `PosViewModel.cs:1414` зовёт
старый конструктор. Чинится в Task 4 — так и задумано, чтобы правка VM и правка
её вызывающей стороны были разными коммитами.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/VvCash.Tests/CustomerSearchViewModelTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services.Api;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>Окно поиска клиента: когда показывается пустое состояние и что
/// происходит при создании клиента из него. View model развязана от Window на
/// два делегата именно ради этих тестов — Avalonia здесь не поднимается.</summary>
public class CustomerSearchViewModelTest
{
    private sealed class FakeCounterpartyService : ICounterpartyService
    {
        public List<CounterpartyResponse>? Results;
        public string? LastQuery;

        public Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request)
            => Task.FromResult<CounterpartyResponse?>(null);

        public Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query)
        {
            LastQuery = query;
            return Task.FromResult(Results);
        }

        public Task<string?> GetSystemCounterpartyIdAsync() => Task.FromResult<string?>(null);
    }

    private sealed class Harness
    {
        public FakeCounterpartyService Service { get; } = new();
        public int CloseCount;
        public CounterpartyResponse? ClosedWith;
        public int CreateCount;
        public string? CreateQuery;
        public CounterpartyResponse? CreateResult;

        public CustomerSearchViewModel Build(bool canCreateCustomer = true) => new(
            Service,
            canCreateCustomer,
            result => { CloseCount++; ClosedWith = result; },
            query =>
            {
                CreateCount++;
                CreateQuery = query;
                return Task.FromResult(CreateResult);
            });
    }

    private static CounterpartyResponse Customer(string id, string name)
        => new() { Id = id, FullName = name };

    [Fact]
    public void FreshWindow_ShowsNoEmptyState()
    {
        var vm = new Harness().Build();

        Assert.False(vm.HasSearched);
        Assert.False(vm.HasNoResults);
    }

    [Fact]
    public async Task SearchWithoutResults_ShowsEmptyState()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse>();
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.HasNoResults);
    }

    [Fact]
    public async Task SearchWithResults_HidesEmptyState()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse> { Customer("c-1", "Иванов Иван") };
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.HasNoResults);
        Assert.Single(vm.SearchResults);
    }

    /// <summary>Иначе «Клиент не найден» моргает на каждом поиске между тем,
    /// как список очищен, и тем, как пришёл ответ.</summary>
    [Fact]
    public async Task WhileLoading_EmptyStateStaysHidden()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse>();
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);

        vm.IsLoading = true;

        Assert.False(vm.HasNoResults);
    }

    /// <summary>Пустой запрос — это «не искали», а не «не нашли».</summary>
    [Fact]
    public async Task EmptyQuery_DoesNotMarkAsSearched()
    {
        var vm = new Harness().Build();
        vm.SearchQuery = "   ";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.HasSearched);
        Assert.False(vm.HasNoResults);
    }

    [Fact]
    public async Task CreateCustomer_PassesSearchQueryAsPrefill()
    {
        var harness = new Harness();
        var vm = harness.Build();
        vm.SearchQuery = "9001234567";

        await vm.CreateCustomerCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.CreateCount);
        Assert.Equal("9001234567", harness.CreateQuery);
    }

    [Fact]
    public async Task CreateCustomer_ClosesWindowWithCreatedCustomer()
    {
        var harness = new Harness();
        harness.CreateResult = Customer("c-9", "Новый Клиент");
        var vm = harness.Build();

        await vm.CreateCustomerCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.CloseCount);
        Assert.Same(harness.CreateResult, harness.ClosedWith);
    }

    /// <summary>Отмена регистрации и провал создания для окна поиска
    /// неразличимы — оба дают null и оба обязаны сохранить контекст поиска.</summary>
    [Fact]
    public async Task CreateCustomer_Cancelled_KeepsSearchContext()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse> { Customer("c-1", "Иванов Иван") };
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);
        harness.CreateResult = null;

        await vm.CreateCustomerCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.CloseCount);
        Assert.Single(vm.SearchResults);
        Assert.Equal("Иванов", vm.SearchQuery);
    }

    [Fact]
    public void CreateDisabledByFeatureFlag_HidesCreateAffordances()
    {
        var vm = new Harness().Build(canCreateCustomer: false);

        Assert.False(vm.IsCreateEnabled);
    }

    [Fact]
    public async Task ConfirmSelection_ClosesWithSelectedCustomer()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse> { Customer("c-1", "Иванов Иван") };
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);
        vm.SelectedCounterparty = vm.SearchResults[0];

        vm.ConfirmSelectionCommand.Execute(null);

        Assert.Equal(1, harness.CloseCount);
        Assert.Same(vm.SearchResults[0], harness.ClosedWith);
    }

    [Fact]
    public void Cancel_ClosesWithNull()
    {
        var harness = new Harness();
        var vm = harness.Build();

        vm.CancelCommand.Execute(null);

        Assert.Equal(1, harness.CloseCount);
        Assert.Null(harness.ClosedWith);
    }
}
```

- [ ] **Step 2: Убедиться, что тест не компилируется**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerSearchViewModelTest"
```

Ожидается: ошибка сборки — конструктор `CustomerSearchViewModel` принимает
`Window`, а не делегаты (`error CS1503: Argument 1: cannot convert from
'FakeCounterpartyService' to 'Avalonia.Controls.Window'`), плюс
`error CS1061: 'CustomerSearchViewModel' does not contain a definition for 'HasNoResults'`.

- [ ] **Step 3: Переписать view model**

Заменить содержимое `src/VvCash/ViewModels/CustomerSearchViewModel.cs` целиком:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models.Api;
using VvCash.Services.Api;

namespace VvCash.ViewModels;

public partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly ICounterpartyService _counterpartyService;

    /// <summary>Закрыть окно, отдав выбранного (или только что созданного)
    /// клиента, либо null. Делегат, а не Window: создание клиента открывает
    /// дочернее окно, и владеть этим должна вызывающая сторона — иначе view
    /// model нельзя сконструировать в тесте, а тест с window == null проверял
    /// бы ветку, которой в проде нет.</summary>
    private readonly Action<CounterpartyResponse?> _close;

    /// <summary>Открыть регистрацию, предзаполнив её строкой поиска, и вернуть
    /// созданного клиента. null — отмена или провал создания; для окна поиска
    /// это одно и то же: остаёмся на месте.</summary>
    private readonly Func<string, Task<CounterpartyResponse?>> _createCustomer;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ObservableCollection<CounterpartyResponse> _searchResults = new();
    [ObservableProperty] private CounterpartyResponse? _selectedCounterparty;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Был ли хотя бы один поиск с непустым запросом. Без этого флага
    /// «Клиент не найден» висело бы на только что открытом окне.</summary>
    [ObservableProperty] private bool _hasSearched;

    /// <summary>Снимок cash_customer_registration_enabled на момент открытия
    /// окна — тот же флаг, что прячет кнопку регистрации в тулбаре. Окно поиска
    /// не должно быть обходом флага.</summary>
    public bool IsCreateEnabled { get; }

    public bool HasNoResults => HasSearched && !IsLoading && SearchResults.Count == 0;

    public CustomerSearchViewModel(
        ICounterpartyService counterpartyService,
        bool canCreateCustomer,
        Action<CounterpartyResponse?> close,
        Func<string, Task<CounterpartyResponse?>> createCustomer)
    {
        _counterpartyService = counterpartyService;
        _close = close;
        _createCustomer = createCustomer;
        IsCreateEnabled = canCreateCustomer;

        // Подписка на саму коллекцию, а не строка в конце SearchAsync: поиск
        // наполняет её в несколько шагов (Clear, затем Add на результат), и
        // пустое состояние обязано отслеживать реальное содержимое.
        SearchResults.CollectionChanged += OnSearchResultsCollectionChanged;
    }

    private void OnSearchResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasNoResults));

    /// <summary>SearchResults — [ObservableProperty], то есть коллекцию можно
    /// подменить целиком. Сегодня этого никто не делает, но без перевешивания
    /// подписки такая подмена сломала бы пустое состояние молча.
    ///
    /// oldValue объявлен nullable вслед за сгенерированным объявлением partial-метода;
    /// разойтись с ним в аннотациях — это CS8611. На практике null здесь не приходит:
    /// поле инициализировано `= new()`, а инициализатор поля идёт мимо сеттера.</summary>
    partial void OnSearchResultsChanged(
        ObservableCollection<CounterpartyResponse>? oldValue,
        ObservableCollection<CounterpartyResponse> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnSearchResultsCollectionChanged;
        newValue.CollectionChanged += OnSearchResultsCollectionChanged;
        OnPropertyChanged(nameof(HasNoResults));
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            // Пустой запрос — это «не искали», а не «не нашли», в том числе
            // когда до него был успешный поиск: иначе очистка строки оставляла
            // бы «Клиент не найден» висеть над пустым запросом.
            HasSearched = false;
            SearchResults.Clear();
            return;
        }

        IsLoading = true;
        try
        {
            var results = await _counterpartyService.SearchCounterpartiesAsync(SearchQuery);
            SearchResults.Clear();
            if (results != null)
            {
                foreach (var r in results)
                {
                    SearchResults.Add(r);
                }
            }
            HasSearched = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectCounterparty(CounterpartyResponse counterparty)
    {
        SelectedCounterparty = counterparty;
    }

    [RelayCommand]
    private async Task CreateCustomerAsync()
    {
        var created = await _createCustomer(SearchQuery);
        if (created != null)
        {
            _close(created);
        }
    }

    [RelayCommand]
    private void ConfirmSelection()
    {
        if (SelectedCounterparty != null)
        {
            _close(SelectedCounterparty);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _close(null);
    }
}
```

- [ ] **Step 4: Прогнать тесты**

```bash
& ./run-tests.ps1 --filter "FullyQualifiedName~CustomerSearchViewModelTest"
```

Ожидается: ошибка сборки в `PosViewModel.cs:1414` —
`error CS1503: Argument 1: cannot convert from 'VvCash.Views.CustomerSearchWindow' to 'VvCash.Services.Api.ICounterpartyService'`.

Это ожидаемо: вызывающая сторона чинится в Task 4. Тесты этой задачи зелёными
станут там же — коммит здесь делается на некомпилирующемся дереве сознательно,
чтобы правка view model и правка вызывающей стороны читались отдельно.

- [ ] **Step 5: Коммит**

```bash
git add src/VvCash/ViewModels/CustomerSearchViewModel.cs tests/VvCash.Tests/CustomerSearchViewModelTest.cs
git commit -m "feat(customer): let the search window offer creating a client"
```

---

## Task 3: `CustomerRegistrationViewModel.ApplyPrefill`

**Files:**
- Modify: `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs`

Автотеста нет: метод — три условных присваивания, а конструктор view model
требует непустой `Window`, поднять который без Avalonia нельзя. Вся логика,
которую здесь можно сломать, живёт в `CustomerPrefill` и покрыта Task 1.

- [ ] **Step 1: Добавить using**

В `src/VvCash/ViewModels/CustomerRegistrationViewModel.cs` после
`using VvCash.Models.Api;` добавить:

```csharp
using VvCash.Models;
```

- [ ] **Step 2: Добавить метод**

Вставить сразу после конструктора (после закрывающей скобки на строке 64,
перед `partial void OnPhoneNumberChanged`):

```csharp
    /// <summary>Переносит строку поиска в форму, когда регистрацию открыли из
    /// окна поиска. Пустые поля префилла не затирают уже введённое: метод
    /// зовётся до показа окна, но правило «пустое не пишем» держит его
    /// безопасным и при повторном вызове.</summary>
    public void ApplyPrefill(CustomerPrefill prefill)
    {
        if (!string.IsNullOrEmpty(prefill.PhoneNumber)) PhoneNumber = prefill.PhoneNumber;
        if (!string.IsNullOrEmpty(prefill.FirstName)) FirstName = prefill.FirstName;
        if (!string.IsNullOrEmpty(prefill.LastName)) LastName = prefill.LastName;
    }
```

- [ ] **Step 3: Коммит**

Сборку здесь не проверяем — дерево всё ещё сломано после Task 2, чинится в Task 4.

```bash
git add src/VvCash/ViewModels/CustomerRegistrationViewModel.cs
git commit -m "feat(customer): prefill the registration form from a search query"
```

---

## Task 4: `PosViewModel` — общая подстановка клиента и оба входа в регистрацию

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:1405-1447`

- [ ] **Step 1: Заменить оба метода**

В `src/VvCash/ViewModels/PosViewModel.cs` заменить целиком блок от
`[RelayCommand]` перед `private async Task OpenCustomerSearch()` до закрывающей
скобки `OpenCustomerRegistration()` (строки 1405–1447) на:

```csharp
    [RelayCommand]
    private async Task OpenCustomerSearch()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null) return;

        var dialog = new VvCash.Views.CustomerSearchWindow();
        dialog.DataContext = new CustomerSearchViewModel(
            _counterpartyService,
            IsCustomerRegistrationEnabled,
            result => dialog.Close(result),
            // Владелец — окно поиска, а не главное окно: если кассир отменит
            // регистрацию, он вернётся в поиск с целым запросом и списком.
            query => ShowCustomerRegistrationAsync(dialog, query));

        var selected = (CounterpartyResponse?) await dialog.ShowDialog<object>(mainWindow);
        if (selected != null)
        {
            ApplySelectedCustomer(selected);
        }
    }

    private async Task OpenCustomerRegistration()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null) return;

        var created = await ShowCustomerRegistrationAsync(mainWindow, string.Empty);
        if (created != null)
        {
            ApplySelectedCustomer(created);
        }
    }

    /// <summary>Единственное место, где открывается окно регистрации. Оба входа —
    /// кнопка в тулбаре и «Создать клиента» из окна поиска — отличаются только
    /// владельцем окна и наличием строки для префилла.</summary>
    private async Task<CounterpartyResponse?> ShowCustomerRegistrationAsync(Avalonia.Controls.Window owner, string searchQuery)
    {
        var dialog = new VvCash.Views.CustomerRegistrationWindow();
        var vm = new CustomerRegistrationViewModel(dialog, _counterpartyService);
        vm.ApplyPrefill(CustomerPrefill.FromSearchQuery(searchQuery));
        dialog.DataContext = vm;

        return (CounterpartyResponse?) await dialog.ShowDialog<object>(owner);
    }

    /// <summary>Клиент выбран — неважно, найден в базе или только что создан.
    /// Отдельный метод, а не копия в двух местах: правило «применить карту
    /// скидки и пересчитать корзину» должно жить в одном месте, иначе следующий
    /// вход в выбор клиента просто забудет про requote, и расхождение будет
    /// молчаливым — ровно так тулбарная кнопка регистрации и теряла клиента.</summary>
    private void ApplySelectedCustomer(CounterpartyResponse customer)
    {
        SelectedCustomer = customer;
        if (customer.DiscountCard != null && customer.DiscountCard.Discount > 0)
        {
            _cartService.SetCustomerDiscount(customer.DiscountCard.Discount); // offline fallback
            StatusMessage = $"Клиент: {customer.FullName} • Скидка по карте: {customer.DiscountCard.Discount}%";
        }
        else
        {
            _cartService.ClearCustomerDiscount();
            StatusMessage = $"Выбран клиент: {customer.FullName}";
        }
        TriggerRequote();
    }
```

`CounterpartyResponse` и `CustomerPrefill` пишутся без префикса: `using VvCash.Models.Api;`
и `using VvCash.Models;` в файле уже есть (строки 13–14). `Avalonia.Controls.Window`
— полным именем, `using Avalonia.Controls;` в файле нет.

- [ ] **Step 2: Прогнать все тесты**

```bash
& ./run-tests.ps1
```

Ожидается: сборка проходит, `Failed: 0`. Тесты из Task 1 и Task 2 (16 + 12 —
см. «Правки по ходу исполнения») теперь зелёные вместе с остальными.

- [ ] **Step 3: Коммит**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs
git commit -m "fix(customer): put a newly created client into the receipt"
```

---

## Task 5: Строки локализации

**Files:**
- Modify: `src/VvCash/Assets/i18n/ru.json`
- Modify: `src/VvCash/Assets/i18n/en.json`
- Modify: `src/VvCash/Assets/i18n/kk.json`
- Modify: `src/VvCash/Assets/i18n/tg.json`
- Modify: `src/VvCash/Assets/i18n/uz.json`

Ключи английские, как соседний `AddCustomer`. Отсутствующий ключ
`I18nService` рендерит как `[Ключ]` — то есть пропуск любого из пяти файлов
будет видно на экране, но только в этой локали.

- [ ] **Step 1: `ru.json`**

Найти строку `"AddCustomer": "Добавить клиента",` и вставить сразу после неё:

```json
  "CreateCustomer": "СОЗДАТЬ КЛИЕНТА",
  "CustomerNotFound": "Клиент не найден",
  "CustomerNotFoundHint": "Проверьте запрос или создайте нового клиента",
```

- [ ] **Step 2: `en.json`**

После `"AddCustomer": "Add Customer",`:

```json
  "CreateCustomer": "CREATE CUSTOMER",
  "CustomerNotFound": "Customer not found",
  "CustomerNotFoundHint": "Check the query or create a new customer",
```

- [ ] **Step 3: `kk.json`**

После `"AddCustomer": "Клиент қосу",`:

```json
  "CreateCustomer": "КЛИЕНТ ҚҰРУ",
  "CustomerNotFound": "Клиент табылмады",
  "CustomerNotFoundHint": "Сұранысты тексеріңіз немесе жаңа клиент құрыңыз",
```

- [ ] **Step 4: `tg.json`**

После `"AddCustomer": "Иловаи муштарӣ",`:

```json
  "CreateCustomer": "ЭҶОДИ МУШТАРӢ",
  "CustomerNotFound": "Муштарӣ ёфт нашуд",
  "CustomerNotFoundHint": "Дархостро санҷед ё муштарии нав эҷод кунед",
```

- [ ] **Step 5: `uz.json`**

После `"AddCustomer": "Mijoz qo'shish",`:

```json
  "CreateCustomer": "MIJOZ YARATISH",
  "CustomerNotFound": "Mijoz topilmadi",
  "CustomerNotFoundHint": "So'rovni tekshiring yoki yangi mijoz yarating",
```

- [ ] **Step 6: Проверить, что все пять файлов — валидный JSON**

Инструментом PowerShell:

```bash
foreach ($l in 'ru','en','kk','tg','uz') { $j = Get-Content "src/VvCash/Assets/i18n/$l.json" -Raw -Encoding utf8 | ConvertFrom-Json; foreach ($k in 'CreateCustomer','CustomerNotFound','CustomerNotFoundHint') { if (-not $j.$k) { throw "$l missing $k" } }; "$l ok" }
```

Ожидается: пять строк `ru ok`, `en ok`, `kk ok`, `tg ok`, `uz ok`.

- [ ] **Step 7: Коммит**

```bash
git add src/VvCash/Assets/i18n
git commit -m "i18n(customer): add strings for creating a client from search"
```

---

## Task 6: Разметка окна поиска

**Files:**
- Modify: `src/VvCash/Views/CustomerSearchWindow.axaml:53-83`

- [ ] **Step 1: Пустое состояние вместо списка**

Заменить блок `<Border Grid.Row="1" Padding="32,24">` со списком (строки 53–69)
на:

```xml
                <Border Grid.Row="1" Padding="32,24">
                    <Panel>
                        <ListBox ItemsSource="{Binding SearchResults}" SelectedItem="{Binding SelectedCounterparty}" Background="Transparent" IsVisible="{Binding !HasNoResults}">
                            <ListBox.ItemTemplate>
                                <DataTemplate>
                                    <Border Background="{StaticResource Slate50Brush}" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" CornerRadius="8" Padding="16" Margin="0,4">
                                        <Grid ColumnDefinitions="*, Auto">
                                            <StackPanel Grid.Column="0">
                                                <TextBlock Text="{Binding FullName}" FontSize="18" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}" />
                                                <TextBlock Text="{Binding Phone}" FontSize="14" Foreground="{StaticResource Slate600Brush}" Margin="0,4,0,0"/>
                                            </StackPanel>
                                            <TextBlock Grid.Column="1" Text="{Binding CurrentBalance, StringFormat='Баланс: {0:N2}'}" FontSize="16" FontWeight="SemiBold" Foreground="{StaticResource PrimaryBrush}" VerticalAlignment="Center" />
                                        </Grid>
                                    </Border>
                                </DataTemplate>
                            </ListBox.ItemTemplate>
                        </ListBox>

                        <!-- Пустое состояние: показывается только после поиска, давшего ноль результатов -->
                        <StackPanel IsVisible="{Binding HasNoResults}" VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="16">
                            <material:MaterialIcon Kind="AccountSearch" Width="72" Height="72" Foreground="{StaticResource Slate300Brush}" HorizontalAlignment="Center"/>
                            <TextBlock Text="{Binding [CustomerNotFound], Source={x:Static services:I18nService.Instance}}" FontSize="20" FontWeight="Bold" Foreground="{StaticResource Slate700Brush}" HorizontalAlignment="Center"/>
                            <TextBlock Text="{Binding [CustomerNotFoundHint], Source={x:Static services:I18nService.Instance}}" FontSize="14" Foreground="{StaticResource Slate500Brush}" HorizontalAlignment="Center" TextAlignment="Center" TextWrapping="Wrap" MaxWidth="360"/>
                            <Button Classes="PrimaryButton" Command="{Binding CreateCustomerCommand}" IsVisible="{Binding IsCreateEnabled}" HorizontalAlignment="Center" Margin="0,8,0,0">
                                <StackPanel Orientation="Horizontal" Spacing="8">
                                    <material:MaterialIcon Kind="AccountPlus" Width="22" Height="22"/>
                                    <TextBlock Text="{Binding [CreateCustomer], Source={x:Static services:I18nService.Instance}}" Classes="Uppercase" LetterSpacing="1" FontSize="16"/>
                                </StackPanel>
                            </Button>
                        </StackPanel>
                    </Panel>
                </Border>
```

- [ ] **Step 2: Кнопка в футере**

Внутри `<!-- Footer Actions -->` заменить `<Grid ColumnDefinitions="1*, 2*">`
вместе с обеими кнопками внутри него (номера строк после Step 1 уже сдвинулись —
ориентироваться по содержимому) на:

```xml
                <Grid ColumnDefinitions="Auto, Auto, *">
                    <Button Grid.Column="0" Content="ОТМЕНА" Classes="OutlinedButton" Command="{Binding CancelCommand}" Margin="0,0,16,0"/>
                    <!-- Auto-колонка: при выключенном флаге кнопка не измеряется и щели не остаётся -->
                    <Button Grid.Column="1" Classes="OutlinedButton" Command="{Binding CreateCustomerCommand}" IsVisible="{Binding IsCreateEnabled}" Margin="0,0,16,0">
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <material:MaterialIcon Kind="AccountPlus" Width="20" Height="20"/>
                            <TextBlock Text="{Binding [CreateCustomer], Source={x:Static services:I18nService.Instance}}" Classes="Uppercase" LetterSpacing="1"/>
                        </StackPanel>
                    </Button>
                    <Button Grid.Column="2" Classes="PrimaryButton" Command="{Binding ConfirmSelectionCommand}" HorizontalAlignment="Stretch">
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <material:MaterialIcon Kind="CheckCircle" Width="24" Height="24"/>
                            <TextBlock Text="{Binding [ВЫБРАТЬКЛИЕНТА], Source={x:Static services:I18nService.Instance}}" Classes="Uppercase" LetterSpacing="1" FontSize="16"/>
                        </StackPanel>
                    </Button>
                </Grid>
```

- [ ] **Step 3: Собрать (XAML компилируется, ошибки биндингов ловятся здесь)**

```bash
dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify
```

Ожидается: `Build succeeded`, ноль ошибок. Сборка идёт в `build/verify`, потому
что запущенное приложение держит лок на обычной выходной папке.

- [ ] **Step 4: Коммит**

```bash
git add src/VvCash/Views/CustomerSearchWindow.axaml
git commit -m "feat(customer): show an empty state and a create button in search"
```

---

## Task 7: Ручная проверка на запущенной кассе

**Files:** нет

Спека фиксирует эти пять проверок как обязательные перед мержем: подстановка
клиента в `PosViewModel` автотестом не покрыта (22 зависимости, фейки лежат
приватными внутри `PosViewModelSellerGateTest`).

- [ ] **Step 1: Запустить приложение**

```bash
dotnet run --project src/VvCash/VvCash.csproj
```

- [ ] **Step 2: Пройти сценарии**

1. Поиск → выбрать клиента с картой скидки → имя и процент в шапке чека, сумма пересчитана.
2. Поиск по несуществующему → «Клиент не найден» → «Создать клиента» → форма с префиллом → сохранить → окно поиска закрылось, клиент в чеке.
3. Поиск, давший результаты → «Создать клиента» в футере → отмена в форме → вернулись в поиск, запрос и список на месте.
4. Кнопка регистрации в тулбаре → сохранить → клиент сразу в чеке.
5. Выключить `cash_customer_registration_enabled` на сервере, пересинхронизировать → в окне поиска нет ни кнопки в футере, ни кнопки в пустом состоянии.

- [ ] **Step 3: Отметить результат**

Если сценарий 5 недоступен (нет доступа к флагам сервера) — записать это явно в
описании PR, а не молча пропустить.

---

## Правки по ходу исполнения

То, что ревью нашло уже после того, как план был написан. Блоки кода выше
исправлены; счётчики тестов в шагах — нет, актуальные здесь.

**Task 1 — `CustomerPrefill`.** Была мёртвая ветка `if (words.Length == 0) return Empty;`:
гард `IsNullOrWhiteSpace` выше уже гарантирует непустой результат `Split`, потому
что оба используют один и тот же набор пробельных символов. Убрана (`1fbae4d`).
Добавлены четыре краевых теста — двадцать цифр, разделитель-таб, цифра внутри
слова, строка из одной пунктуации. Итог: **16 тестов**, не 12. Первая версия
теста на двадцать цифр брала `"12345678901234567890"` — это `"1234567890"`
дважды, поэтому срез `[..10]` дал бы тот же ответ, что и `[^10..]`, и тест не
проверял направление. Строка заменена на асимметричную (`1bd31ca`).

**Task 2 — пустой запрос после удачного поиска.** `SearchAsync` в ветке пустого
запроса чистил список, но не сбрасывал `HasSearched`, и «Клиент не найден»
оставалось висеть над пустым запросом. Добавлено `HasSearched = false;` плюс
регресс-тест `ClearingQueryAfterSearch_DropsEmptyState`. Итог: **12 тестов**,
не 11 (`e6f41e0`).

**Task 2 — nullability сгенерированного partial-метода.** План утверждал, что
`OnSearchResultsChanged` объявлен с non-nullable `oldValue` и что расхождение
даёт CS8826. Оба утверждения неверны: генератор объявляет `oldValue` nullable, а
расхождение даёт **CS8611**. Сигнатура и комментарий исправлены.

## Self-review

**Покрытие спеки:**

| Раздел спеки | Задача |
|---|---|
| Развязка `CustomerSearchViewModel` от `Window` | Task 2 |
| Пустое состояние (`HasSearched`, `HasNoResults`, перевешивание подписки) | Task 2, Task 6 |
| Команда создания, гашение по флагу | Task 2, Task 6 |
| Префилл формы регистрации | Task 1, Task 3 |
| `ApplySelectedCustomer`, починка тулбарной кнопки | Task 4 |
| Строки i18n | Task 5 |
| Ручная проверка вместо теста `PosViewModel` | Task 7 |

**Согласованность имён:** `CustomerPrefill.FromSearchQuery` (Task 1) →
`ApplyPrefill(CustomerPrefill)` (Task 3) → вызов в `ShowCustomerRegistrationAsync`
(Task 4). Свойства `FirstName`/`LastName`/`PhoneNumber` совпадают с именами полей
`CustomerRegistrationViewModel`. `IsCreateEnabled`, `HasNoResults`,
`CreateCustomerCommand` из Task 2 используются в разметке Task 6 под теми же
именами (`CreateCustomerAsync` → генератор даёт `CreateCustomerCommand`).
