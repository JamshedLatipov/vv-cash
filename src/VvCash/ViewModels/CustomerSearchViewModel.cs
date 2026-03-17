using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using VvCash.Models.Api;
using VvCash.Services.Api;

namespace VvCash.ViewModels;

public partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly ICounterpartyService _counterpartyService;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ObservableCollection<CounterpartyResponse> _searchResults = new();
    [ObservableProperty] private CounterpartyResponse? _selectedCounterparty;
    [ObservableProperty] private bool _isLoading = false;

    public CustomerSearchViewModel(Window window, ICounterpartyService counterpartyService)
    {
        _window = window;
        _counterpartyService = counterpartyService;
    }

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
    private void ConfirmSelection()
    {
        if (SelectedCounterparty != null)
        {
            _window.Close(SelectedCounterparty);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _window.Close(null);
    }
}
