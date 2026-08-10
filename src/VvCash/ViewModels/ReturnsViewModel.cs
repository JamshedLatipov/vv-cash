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
    private readonly ICashOperationService _cashOperationService;
    private readonly ICounterpartyService _counterpartyService;
    private readonly string? _cashId;

    /// <summary>Resolved once per screen — see ICounterpartyService.GetSystemCounterpartyIdAsync
    /// for why the lookup behind it is not cheap on a store with a large customer book.</summary>
    private string? _payoutCounterpartyId;

    /// <summary>True once <see cref="SubmitReturn"/> has booked the return for the lines
    /// as they currently stand. A retry after a failed payout must not book a second one:
    /// there is no endpoint that cancels a return, so a duplicate credits the stock twice
    /// with nothing to undo it. Cleared whenever the lines change — a different basket is
    /// a different return — exactly as ExchangeViewModel clears its own.</summary>
    private bool _returnBooked;

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

    /// <summary>What the cashier typed into the receipt-number box. Not applied as they
    /// type — <see cref="SearchSales"/> is what sends it, so a half-typed number never
    /// costs a round trip and never empties the list mid-keystroke.</summary>
    [ObservableProperty] private string _documentNumberQuery = string.Empty;

    /// <summary>What the cashier scanned or typed into the barcode box, once a
    /// receipt's lines are already on screen — a faster way to bump a line's
    /// quantity than hunting for it in a long list.</summary>
    [ObservableProperty] private string _returnScanQuery = string.Empty;

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

    /// <summary>False while nobody has told the register which payment category the till
    /// payout belongs under. Surfaced on the screen as its own warning so the cashier
    /// reads it before picking a receipt, not after — same treatment the exchange screen
    /// gives <see cref="ExchangeViewModel.IsPayoutCategoryConfigured"/>.</summary>
    public bool IsPayoutCategoryConfigured
        => !string.IsNullOrWhiteSpace(_settingsService.ReturnPayoutCategoryId);

    public ReturnsViewModel(Window? window, IReturnService returnService,
        IPrinterService printerService, ISettingsService settingsService,
        ICashFeatureService features, ICashOperationService cashOperationService,
        ICounterpartyService counterpartyService, string? cashId)
    {
        _window = window;
        _returnService = returnService;
        _printerService = printerService;
        _settingsService = settingsService;
        _features = features;
        _cashOperationService = cashOperationService;
        _counterpartyService = counterpartyService;
        _cashId = cashId;
        if (window != null)
            _ = LoadSalesAsync();
    }

    private async Task LoadSalesAsync()
    {
        IsLoadingSales = true;
        ErrorMessage = null;
        try
        {
            var res = await _returnService.GetSalesAsync(CurrentPage, DocumentNumberQuery);
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

    /// <summary>Runs the receipt-number search. Resets to page 1 first: the page the
    /// cashier happened to be browsing has nothing to do with where the searched-for
    /// receipt lands, and asking for page 3 of a one-result search returns nothing.</summary>
    [RelayCommand]
    private async Task SearchSales()
    {
        CurrentPage = 1;
        SelectedSale = null;
        await LoadSalesAsync();
    }

    /// <summary>Clears the search and goes back to browsing.</summary>
    [RelayCommand]
    private async Task ClearSearch()
    {
        if (string.IsNullOrEmpty(DocumentNumberQuery)) return;
        DocumentNumberQuery = string.Empty;
        await SearchSales();
    }

    /// <summary>Scans the physical item instead of hunting for its line in the
    /// list: a match bumps ReturnQty by one, same as pressing the line's own +
    /// button, and briefly highlights the card so the cashier can see which row
    /// the scan landed on.
    ///
    /// Deliberately not async/awaiting the highlight: [RelayCommand] generates an
    /// AsyncRelayCommand that reports CanExecute false for as long as its Task is
    /// running, so awaiting the 700ms flash in-line here would block a second scan
    /// that arrives within that window — at scanner speed, an entirely normal case.
    /// The blocked scan's digits would then sit in the now-empty ReturnScanQuery and
    /// never fire Execute, only to concatenate with a third scan into a garbage
    /// string that matches nothing. Firing the flash off as its own task keeps this
    /// command's own Task done (and CanExecute true again) as soon as the quantity
    /// is bumped, so consecutive fast scans each get their own turn.</summary>
    [RelayCommand]
    private Task ScanReturnBarcode()
    {
        var code = ReturnScanQuery.Trim();
        ReturnScanQuery = string.Empty;
        if (string.IsNullOrWhiteSpace(code)) return Task.CompletedTask;
        ErrorMessage = null;

        var line = Lines.FirstOrDefault(l => l.IsReturnable && l.Barcode == code);
        if (line == null)
        {
            ErrorMessage = I18nService.Instance["BarcodeNotFoundInReceipt"];
            return Task.CompletedTask;
        }

        line.ReturnQty += 1;
        _ = FlashScannedAsync(line);
        return Task.CompletedTask;
    }

    private static async Task FlashScannedAsync(ReturnLineVm line)
    {
        try
        {
            line.IsRecentlyScanned = true;
            await Task.Delay(700);
            line.IsRecentlyScanned = false;
        }
        catch (Exception ex)
        {
            // Detached task, same as PosViewModel.RequoteSafeAsync: nothing awaits
            // this one, so a failure here must not vanish silently nor crash. Purely
            // cosmetic either way — a missed highlight costs the cashier nothing but
            // the visual cue, so this only logs and swallows rather than surfacing
            // an ErrorMessage over a flash nobody but the cashier's eye depends on.
            System.Diagnostics.Debug.WriteLine($"[ReturnsViewModel] Scan highlight flash failed: {ex}");
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
        _returnBooked = false;
        OnPropertyChanged(nameof(TotalRefund));
        OnPropertyChanged(nameof(CanSubmit));
    }

    private void OnLineRefundChanged()
    {
        // Whatever was booked was booked for the old quantities, and the retry guard
        // must not suppress the return of a basket nobody has sent yet.
        _returnBooked = false;
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

    /// <summary>Books the return and then hands the refunded money out of the till, in
    /// that order and no other — the same two legs, against the same endpoints, that
    /// steps 1 and 2 of an exchange run (see ExchangeViewModel). The drawer has always
    /// opened on a return; until the payout leg existed, nothing in the books said the
    /// money had left it.</summary>
    [RelayCommand]
    private async Task SubmitReturn()
    {
        if (SelectedSale == null || !CanSubmit) return;
        IsSubmitting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            // Everything checkable without writing anything is checked before the first
            // call. Discovering an unset category after the return is booked would leave
            // a document that cannot be cancelled and no payout to go with it.
            var categoryId = _settingsService.ReturnPayoutCategoryId;
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                ErrorMessage = PayoutCategoryNotConfigured;
                return;
            }
            if (string.IsNullOrWhiteSpace(_cashId))
            {
                ErrorMessage = CashNotKnown;
                return;
            }
            var counterpartyId = await ResolvePayoutCounterpartyAsync();
            if (string.IsNullOrWhiteSpace(counterpartyId))
            {
                ErrorMessage = CounterpartyNotResolved;
                return;
            }

            // Snapshotted before anything is sent: the reload at the end rebuilds the
            // lines, and the payout must be for the money this press was about.
            var refund = TotalRefund;

            if (!_returnBooked)
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
                _returnBooked = true;
                HasBookedDocument = true;
            }

            // Nothing to hand over when the returned lines were worth nothing, and the
            // server binds the amount as gt=0 — posting a zero would be a 400 with the
            // return already booked, over money that never had to move.
            if (refund > 0m)
            {
                var payout = await _cashOperationService.CreateCashExpenseAsync(
                    BuildPayoutRequest(counterpartyId!, categoryId, refund));
                if (!payout.Success)
                {
                    ErrorMessage = PayoutFailed(payout.Message);
                    return;
                }
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

    /// <summary>The till payout body. Public for the same reason ExchangeViewModel's is:
    /// what lands in each slot is what the back office reads the operation by.</summary>
    public CashExpenseRequest BuildPayoutRequest(string counterpartyId, string paymentCategoryId, decimal amount) => new()
    {
        OperationType = "expense",
        Cash = _cashId ?? string.Empty,
        Counterparty = counterpartyId,
        Note = $"Возврат по чеку {SelectedSale?.DocumentNumber}".TrimEnd(),
        Details = new System.Collections.Generic.List<CashExpenseDetail>
        {
            new() { PaymentCategory = paymentCategoryId, Amount = amount },
        },
    };

    private async Task<string?> ResolvePayoutCounterpartyAsync()
    {
        if (!string.IsNullOrWhiteSpace(_payoutCounterpartyId)) return _payoutCounterpartyId;
        try
        {
            _payoutCounterpartyId = await _counterpartyService.GetSystemCounterpartyIdAsync();
        }
        catch (Exception)
        {
            _payoutCounterpartyId = null;
        }
        return _payoutCounterpartyId;
    }

    // What the cashier reads, written out rather than routed through I18nService for the
    // same reason ExchangeViewModel's are: each one says exactly which leg went through
    // and which did not, and that is what tells the cashier — and the back office after
    // them — what state the books are in.
    public const string PayoutCategoryNotConfigured =
        "Возврат не настроен: не выбрана статья расхода для выдачи из кассы. Задайте её в настройках. Ни один документ не создан.";
    public const string CashNotKnown =
        "Возврат невозможен: касса не определена. Переоткройте смену. Ни один документ не создан.";
    public const string CounterpartyNotResolved =
        "Возврат невозможен: не удалось определить контрагента для выдачи из кассы. Ни один документ не создан.";

    public static string PayoutFailed(string? reason)
        => $"Возврат проведён, выдача из кассы не прошла{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason.Trim()}")}. "
           + "Разберите расхождение в бэк-офисе.";

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
            try
            {
                await _printerService.PrintReturnReceiptAsync(
                    receiptLines, TotalRefund, documentNumber,
                    SelectedSale?.WarehouseName, SelectedSale?.Creator, SelectedSale?.FormattedSelectedDate);
            }
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
