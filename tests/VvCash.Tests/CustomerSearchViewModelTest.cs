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
        Assert.Equal("Иванов", harness.Service.LastQuery);
    }

    /// <summary>Самая частая последовательность: кассир нашёл не того, поправил
    /// написание — и теперь не находит никого.</summary>
    [Fact]
    public async Task SearchWithResults_ThenNoResults_ShowsEmptyState()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse> { Customer("c-1", "Иванов Иван") };
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.False(vm.HasNoResults);

        harness.Service.Results = new List<CounterpartyResponse>();
        vm.SearchQuery = "Ивановв";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.HasNoResults);
        Assert.Empty(vm.SearchResults);
    }

    /// <summary>HasNoResults — вычисляемое свойство: если по нему не приходит
    /// PropertyChanged, привязка в разметке молча не обновится и «Клиент не
    /// найден» не появится. Пин на механику уведомления, какой бы она ни была.</summary>
    [Fact]
    public async Task ResultsThenNoResults_RaisesHasNoResultsNotification()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse> { Customer("c-1", "Иванов Иван") };
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.Service.Results = new List<CounterpartyResponse>();
        vm.SearchQuery = "Ивановв";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Contains(nameof(CustomerSearchViewModel.HasNoResults), raised);
        Assert.True(vm.HasNoResults);
    }

    /// <summary>Провал поиска (нет связи, ошибка сервера) не должен выглядеть
    /// как «такого клиента нет»: иначе кассир заведёт дубль поверх живого
    /// клиента вместе со второй дисконтной картой.</summary>
    [Fact]
    public async Task SearchFailure_DoesNotShowEmptyState()
    {
        var harness = new Harness();
        harness.Service.Results = null;   // сервис так сообщает о провале
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.HasSearched);
        Assert.False(vm.HasNoResults);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task SearchFailure_KeepsPreviousResults()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse> { Customer("c-1", "Иванов Иван") };
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);

        harness.Service.Results = null;
        vm.SearchQuery = "Петров";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Single(vm.SearchResults);
    }

    [Fact]
    public async Task SuccessfulSearchAfterFailure_ClearsTheError()
    {
        var harness = new Harness();
        harness.Service.Results = null;
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);

        harness.Service.Results = new List<CounterpartyResponse>();
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.HasNoResults);
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

    /// <summary>Регресс: очистка строки после удачного поиска не должна
    /// оставлять «Клиент не найден» — это снова «не искали».</summary>
    [Fact]
    public async Task ClearingQueryAfterSearch_DropsEmptyState()
    {
        var harness = new Harness();
        harness.Service.Results = new List<CounterpartyResponse>();
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasNoResults);

        vm.SearchQuery = string.Empty;
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.HasSearched);
        Assert.False(vm.HasNoResults);
    }

    /// <summary>«Нет соединения» от прошлого поиска — не факт о поиске, которого
    /// не было. Очистка строки убирает и его. Отдельным тестом, а не проверкой в
    /// ClearingQueryAfterSearch_DropsEmptyState: там поиск удачен и ErrorMessage
    /// никто не выставлял, так что проверка прошла бы и без самой правки.</summary>
    [Fact]
    public async Task ClearingQueryAfterFailure_DropsTheError()
    {
        var harness = new Harness();
        harness.Service.Results = null;
        var vm = harness.Build();
        vm.SearchQuery = "Иванов";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);

        vm.SearchQuery = "   ";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
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
        Assert.False(vm.CreateCustomerCommand.CanExecute(null));
    }

    /// <summary>Парная к предыдущей: без неё тест на выключенный флаг проходил
    /// бы и для команды, которую нельзя выполнить никогда.</summary>
    [Fact]
    public void CreateEnabledByFeatureFlag_AllowsTheCommand()
    {
        var vm = new Harness().Build(canCreateCustomer: true);

        Assert.True(vm.IsCreateEnabled);
        Assert.True(vm.CreateCustomerCommand.CanExecute(null));
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

    /// <summary>Главная кнопка не должна превращаться в отмену, когда в списке
    /// ничего не выбрано.</summary>
    [Fact]
    public void ConfirmSelection_WithNothingSelected_DoesNotClose()
    {
        var harness = new Harness();
        var vm = harness.Build();

        vm.ConfirmSelectionCommand.Execute(null);

        Assert.Equal(0, harness.CloseCount);
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
