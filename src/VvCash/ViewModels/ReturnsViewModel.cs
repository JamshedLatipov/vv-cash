using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

public partial class ReturnsViewModel : ViewModelBase
{
    private readonly Window? _window;
    private readonly IReturnService _returnService;
    private readonly IPrinterService _printerService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty] private ObservableCollection<ExpenseListItem> _sales = new();
    [ObservableProperty] private ObservableCollection<ReturnLineVm> _lines = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSale))]
    private ExpenseListItem? _selectedSale;

    [ObservableProperty] private bool _isLoadingSales;
    [ObservableProperty] private bool _isLoadingLines;
    [ObservableProperty] private bool _isSubmitting;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _successMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePages))]
    private int _currentPage = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePages))]
    private int _pageCount = 1;

    public bool HasSelectedSale => SelectedSale != null;
    public bool HasMorePages => CurrentPage < PageCount;
    public decimal TotalRefund => Lines.Sum(l => l.LineRefund);
    public bool CanSubmit => !IsSubmitting && Lines.Any(l => l.ReturnQty > 0);

    public ReturnsViewModel(Window? window, IReturnService returnService,
        IPrinterService printerService, ISettingsService settingsService)
    {
        _window = window;
        _returnService = returnService;
        _printerService = printerService;
        _settingsService = settingsService;
        if (window != null)
            _ = LoadSalesAsync();
    }

    private async Task LoadSalesAsync()
    {
        IsLoadingSales = true;
        ErrorMessage = null;
        try
        {
            var res = await _returnService.GetSalesAsync(CurrentPage);
            Sales = new ObservableCollection<ExpenseListItem>(res.Body);
            PageCount = Math.Max(1, res.PageCount);
        }
        catch (Exception)
        {
            ErrorMessage = I18nService.Instance["NoConnection"];
        }
        finally
        {
            IsLoadingSales = false;
        }
    }

    partial void OnSelectedSaleChanged(ExpenseListItem? value)
    {
        if (value != null)
            _ = LoadLinesAsync(value.Id);
        else
            SetLines(Array.Empty<ReturnLineVm>());
    }

    private async Task LoadLinesAsync(string expenseId)
    {
        IsLoadingLines = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var body = await _returnService.GetReturnableLinesAsync(expenseId);
            SetLines(body.Details.Select(d => new ReturnLineVm(d)));
        }
        catch (Exception)
        {
            ErrorMessage = I18nService.Instance["ReturnFailed"];
            SetLines(Array.Empty<ReturnLineVm>());
        }
        finally
        {
            IsLoadingLines = false;
        }
    }

    private void SetLines(System.Collections.Generic.IEnumerable<ReturnLineVm> items)
    {
        foreach (var l in Lines) l.RefundChanged -= OnLineRefundChanged;
        Lines = new ObservableCollection<ReturnLineVm>(items);
        foreach (var l in Lines) l.RefundChanged += OnLineRefundChanged;
        OnPropertyChanged(nameof(TotalRefund));
        OnPropertyChanged(nameof(CanSubmit));
    }

    private void OnLineRefundChanged()
    {
        OnPropertyChanged(nameof(TotalRefund));
        OnPropertyChanged(nameof(CanSubmit));
    }

    public ReturnRequest BuildRequest()
    {
        var date = SelectedSale?.SelectedDate;
        var dateOnly = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : (date ?? string.Empty);
        return new ReturnRequest
        {
            SelectedDate = dateOnly,
            Details = Lines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnLineRequest { Product = l.ProductId, Quantity = l.ReturnQty })
                .ToList()
        };
    }

    [RelayCommand]
    private async Task SubmitReturn()
    {
        if (SelectedSale == null || !CanSubmit) return;
        IsSubmitting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var request = BuildRequest();
            var ok = await _returnService.CreateReturnAsync(SelectedSale.Id, request);
            if (!ok)
            {
                ErrorMessage = I18nService.Instance["ReturnFailed"];
                return;
            }

            await RunPostReturnActionsAsync(SelectedSale.DocumentNumber ?? string.Empty);
            SuccessMessage = I18nService.Instance["ReturnSuccess"];
            await LoadLinesAsync(SelectedSale.Id);
        }
        catch (Exception)
        {
            ErrorMessage = I18nService.Instance["NoConnection"];
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task RunPostReturnActionsAsync(string documentNumber)
    {
        if (_settingsService.ReturnOpenCashDrawer)
        {
            try { await _printerService.OpenCashDrawerAsync(); } catch { }
        }
        if (_settingsService.ReturnPrintReceipt)
        {
            var receiptLines = Lines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnReceiptLine(l.Name, l.ReturnQty, l.LineRefund));
            try { await _printerService.PrintReturnReceiptAsync(receiptLines, TotalRefund, documentNumber); }
            catch { }
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (!HasMorePages) return;
        CurrentPage++;
        await LoadSalesAsync();
    }

    [RelayCommand]
    private async Task PrevPage()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await LoadSalesAsync();
    }

    [RelayCommand]
    private void Close() => _window?.Close();
}
