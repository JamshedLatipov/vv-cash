using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Services;

namespace VvCash.ViewModels;

public partial class ParkedSalesViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly IParkedSaleService _parkedSaleService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ObservableCollection<ParkedSale> _sales = new();

    [ObservableProperty] private bool _isLoading = false;

    public bool IsEmpty => !IsLoading && Sales.Count == 0;

    public ParkedSalesViewModel(Window window, IParkedSaleService parkedSaleService)
    {
        _window = window;
        _parkedSaleService = parkedSaleService;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var sales = await _parkedSaleService.GetAllAsync();
            Sales = new ObservableCollection<ParkedSale>(sales);
        }
        finally
        {
            IsLoading = false;
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Resume(ParkedSale sale)
    {
        _window.Close(sale.Id);
    }

    [RelayCommand]
    private async Task Delete(ParkedSale sale)
    {
        await _parkedSaleService.DeleteAsync(sale.Id);
        Sales.Remove(sale);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Cancel()
    {
        _window.Close(null);
    }
}
