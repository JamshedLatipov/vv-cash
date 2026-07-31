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
    /// oldValue не проверяется на null: поле инициализировано `= new()`, а
    /// инициализатор поля идёт мимо сеттера, поэтому этот метод не может быть
    /// вызван, пока коллекции нет. Сигнатура повторяет сгенерированную
    /// объявленную partial-часть дословно, включая nullability — расхождение
    /// в аннотациях даёт CS8826.</summary>
    partial void OnSearchResultsChanged(
        ObservableCollection<CounterpartyResponse> oldValue,
        ObservableCollection<CounterpartyResponse> newValue)
    {
        oldValue.CollectionChanged -= OnSearchResultsCollectionChanged;
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
