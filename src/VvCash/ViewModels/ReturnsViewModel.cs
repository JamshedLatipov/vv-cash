using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Constants;
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
    private readonly ICashFeatureService _features;

    [ObservableProperty] private ObservableCollection<ExpenseListItem> _sales = new();
    [ObservableProperty] private ObservableCollection<ReturnLineVm> _lines = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSale))]
    private ExpenseListItem? _selectedSale;

    [ObservableProperty] private bool _isLoadingSales;
    [ObservableProperty] private bool _isLoadingLines;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isSubmitting;
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

    /// <summary>True once this screen has actually booked a return on the server. Read by
    /// PosViewModel after the modal closes: a screen that was opened and closed without
    /// booking anything is not the end of an operation and must not cost the cashier a
    /// fresh PIN. Sticky — several returns in one sitting are still "a document happened".
    ///
    /// If CreateReturnAsync throws, this stays false even when the server actually
    /// processed the request before the exception happened on our end (e.g. the response
    /// never arrived) — SubmitReturn has no way to tell that case apart from a genuine
    /// failure, and PosViewModel will not re-confirm the seller for it. That is accepted:
    /// the 90-second idle timeout is still the backstop for a receipt genuinely abandoned,
    /// and clearing on an outcome we don't actually know would cost the cashier a PIN for
    /// nothing on every ordinary network hiccup.</summary>
    public bool HasBookedDocument { get; private set; }

    public ReturnsViewModel(Window? window, IReturnService returnService,
        IPrinterService printerService, ISettingsService settingsService,
        ICashFeatureService features)
    {
        _window = window;
        _returnService = returnService;
        _printerService = printerService;
        _settingsService = settingsService;
        _features = features;
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
            if (SelectedSale?.Id != expenseId) return; // selection changed during load; ignore stale result
            SetLines(body.Details.Select(d => new ReturnLineVm(d)));
        }
        catch (Exception)
        {
            if (SelectedSale?.Id != expenseId) return; // stale failure for a no-longer-selected sale
            ErrorMessage = I18nService.Instance["ReturnFailed"];
            SetLines(Array.Empty<ReturnLineVm>());
        }
        finally
        {
            if (SelectedSale?.Id == expenseId)
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

            // Set before the drawer/receipt side effects, not after: those are
            // best-effort (they swallow their own exceptions) and the document is already
            // on the server by this point regardless of how printing goes.
            HasBookedDocument = true;

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
        // The store's setting, not the terminal's: these two used to be local
        // checkboxes, and a store that switched them off centrally must not have
        // them re-enabled by whatever was ticked on one register. The local values
        // are deliberately left in settings untouched, so that removing the flags
        // later restores the old behaviour rather than losing it.
        if (_features.Current.IsEnabled(CashFeatureCodes.ReturnOpenDrawer))
        {
            try { await _printerService.OpenCashDrawerAsync(); } catch { }
        }
        if (_features.Current.IsEnabled(CashFeatureCodes.ReturnPrintReceipt))
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
