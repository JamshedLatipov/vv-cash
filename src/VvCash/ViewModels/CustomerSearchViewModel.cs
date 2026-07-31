using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models.Api;
using VvCash.Services;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _isLoading;

    /// <summary>Провал поиска, а не его пустой результат. Показывается вместо
    /// пустого состояния, чтобы «нет связи» не читалось как «клиента нет».</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Был ли хотя бы один поиск с непустым запросом. Без этого флага
    /// «Клиент не найден» висело бы на только что открытом окне.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _hasSearched;

    /// <summary>Снимок cash_customer_registration_enabled на момент открытия
    /// окна — тот же флаг, что прячет кнопку регистрации в тулбаре. Окно поиска
    /// не должно быть обходом флага.</summary>
    public bool IsCreateEnabled { get; }

    /// <summary>Промежуточные состояния списка (Clear, затем Add на каждый
    /// результат) гасит сам же !IsLoading: всё наполнение идёт под IsLoading ==
    /// true, а сбрасывается он в finally, последним — когда список уже принял
    /// окончательный вид. Поэтому хватает уведомления по двум флагам, следить
    /// за CollectionChanged не нужно.</summary>
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
    }

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
            if (results == null)
            {
                // Поиск не состоялся: связи нет или сервер ответил ошибкой.
                // Ни список, ни HasSearched не трогаем — «Клиент не найден» с
                // кнопкой «Создать» здесь означало бы приглашение завести дубль
                // клиента, который на самом деле в базе есть.
                ErrorMessage = I18nService.Instance["NoConnection"];
                return;
            }

            ErrorMessage = null;
            SearchResults.Clear();
            foreach (var r in results)
            {
                SearchResults.Add(r);
            }
            HasSearched = true;
        }
        catch (Exception)
        {
            // ICounterpartyService не обещает, что реализация не бросает.
            ErrorMessage = I18nService.Instance["NoConnection"];
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

    // CanExecute на флаге, а не только в разметке: окно поиска не должно быть
    // обходом cash_customer_registration_enabled, даже если кнопку кто-то
    // привяжет мимо IsCreateEnabled. Флаг — снимок на момент открытия окна и
    // больше не меняется, поэтому NotifyCanExecuteChangedFor не нужен.
    [RelayCommand(CanExecute = nameof(IsCreateEnabled))]
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
