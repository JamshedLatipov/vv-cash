using System;
using System.Collections.Generic;
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
using VvCash.Services.Data;
using VvCash.Services.Hardware;

namespace VvCash.ViewModels;

/// <summary>Drives the exchange screen: loading a receipt (same source as
/// ReturnsViewModel — GET /documents/return/{id}/), letting the cashier pick what
/// comes back and what goes out instead, and booking the result.
///
/// "Exchange" is a register word only. There is no exchange document and no exchange
/// endpoint: the screen is a wrapper over three ordinary operations the server has
/// always had, run in this order and no other —
///
///   1. the return of the handed-back lines   (POST /documents/return/{id}/)
///   2. a till payout of the whole returned total (POST /documents/money/expense/create/)
///   3. the replacement sale, paid in full    (POST /documents/expense/create/)
///
/// The drawer gives back everything the return was worth and then takes the full
/// price of the replacement, so the till nets to the difference while every document
/// stays an ordinary one the back office already knows how to read. The return has to
/// reach the server before the sale because the processing queue is drained in
/// run_order: stock must come back before the replacement takes it out, or exchanging
/// one size for another of the same product drives the remain negative wherever the
/// store forbids selling below zero.
///
/// Two baskets — <see cref="ReturnedLines"/> reuses ReturnLineVm, the same object
/// ReturnsViewModel builds from that receipt; <see cref="IssuedLines"/> reuses
/// CartItem, the same Product+Quantity pairing the register already prices a cart
/// with — so both totals round the exact way a sale or a return already does,
/// through the store's MoneyPolicy.
///
/// All the new dependencies are optional so the parameterless constructor used by
/// ExchangeViewModelTest keeps working as a pure calculator with no screen, no
/// network, and no receipt behind it.</summary>
public partial class ExchangeViewModel : ViewModelBase
{
    private readonly Window? _window;
    private readonly IReturnService? _returnService;
    private readonly ICashOperationService? _cashOperationService;
    private readonly IExpenseDocumentService? _expenseDocumentService;
    private readonly ICounterpartyService? _counterpartyService;
    private readonly ISettingsService? _settingsService;
    private readonly IProductService? _productService;
    private readonly ISyncService? _syncService;
    private readonly IPrinterService? _printerService;
    private readonly ICashFeatureService? _features;
    private readonly MoneyPolicy _moneyPolicy;
    private readonly string _shiftId;
    private readonly string? _sellerId;
    private readonly string? _cashId;

    /// <summary>Idempotency key for the replacement sale, minted once per basket state
    /// and not per submit press. When a sale commits server-side but its reply is lost
    /// (timeout, proxy, dropped wifi), the cashier presses submit again — with the same
    /// hash the server recognises the duplicate, whereas a fresh one books a second
    /// sale for the same goods. Cleared whenever either basket changes, so a genuinely
    /// different exchange never reuses it.</summary>
    private string? _documentHash;

    /// <summary>True once step 1 has actually booked a return for the current basket
    /// state. A retry after a failed payout must not book a second one: there is no
    /// endpoint to cancel a return, so a duplicate would credit the stock twice with
    /// nothing to undo it. Cleared alongside <see cref="_documentHash"/>, by the same
    /// funnel, for the same reason — a changed basket is a different exchange.</summary>
    private bool _returnBooked;

    /// <summary>Resolved once per screen, because on a store with a large customer book
    /// the lookup behind it is not a small reply. See
    /// ICounterpartyService.GetSystemCounterpartyIdAsync.</summary>
    private string? _payoutCounterpartyId;

    [ObservableProperty] private ObservableCollection<ReturnLineVm> _returnedLines = new();
    [ObservableProperty] private ObservableCollection<CartItem> _issuedLines = new();

    [ObservableProperty] private ObservableCollection<ExpenseListItem> _sales = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSale))]
    private ExpenseListItem? _selectedSale;
    [ObservableProperty] private bool _isLoadingSales;
    [ObservableProperty] private bool _isLoadingLines;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _successMessage;
    [ObservableProperty] private string _issuedSearchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePages))]
    private int _currentPage = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePages))]
    private int _pageCount = 1;

    /// <summary>The return and the till payout have no offline queue behind them, so
    /// with no connection there is nothing for the submit button to offer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isSubmitting;

    public bool HasSelectedSale => SelectedSale != null;
    public bool HasMorePages => CurrentPage < PageCount;

    /// <summary>True once this screen has written anything to the server — that is, from
    /// the moment the return leg is booked, since a return cannot be cancelled and the
    /// remaining legs may still fail. Read by PosViewModel after the modal closes to
    /// decide whether an operation actually happened and the seller must be re-confirmed.
    /// Sticky, exactly like ReturnsViewModel.HasBookedDocument.</summary>
    public bool HasBookedDocument { get; private set; }

    /// <summary>False while nobody has told the register which payment category the
    /// till payout belongs under. Surfaced on the screen as its own warning so the
    /// cashier reads it before building a basket, not after.</summary>
    public bool IsPayoutCategoryConfigured
        => !string.IsNullOrWhiteSpace(_settingsService?.ExchangePayoutCategoryId);

    /// <summary>Rounded the way the server books it — the figure the cashier
    /// reads on screen must match what the receipt ends up saying.</summary>
    public decimal ReturnedTotal => _moneyPolicy.Round(ReturnedLines.Sum(l => l.LineRefund));
    public decimal IssuedTotal => _moneyPolicy.Round(IssuedLines.Sum(l => l.LineTotal));
    public decimal Difference => IssuedTotal - ReturnedTotal;

    /// <summary>True once the replacement costs more than what came back.</summary>
    public bool CustomerPays => Difference > 0;

    /// <summary>True once the replacement costs less — the till owes the customer.</summary>
    public bool TillPays => Difference < 0;

    /// <summary>Absolute amount the till hands back when <see cref="TillPays"/> —
    /// shown without a minus sign, since the label already carries the direction.</summary>
    public decimal RefundDue => TillPays ? -Difference : 0m;

    public bool CanSubmit => IsOnline && !IsSubmitting
        && ReturnedLines.Any(l => l.ReturnQty > 0)
        && IssuedLines.Any(l => l.Quantity > 0);

    /// <param name="shiftId">Stamped onto the issued document, same as Pay() does
    /// for an ordinary sale.</param>
    /// <param name="cashId">The cash this register is signed in as, from
    /// ISessionContext. Only the till payout makes the client name it.</param>
    /// <param name="isOnline">Snapshot of the register's connectivity at the
    /// moment the screen opens; kept live afterwards via <paramref name="syncService"/>.</param>
    public ExchangeViewModel(
        Window? window = null,
        IReturnService? returnService = null,
        ICashOperationService? cashOperationService = null,
        IExpenseDocumentService? expenseDocumentService = null,
        ICounterpartyService? counterpartyService = null,
        ISettingsService? settingsService = null,
        IProductService? productService = null,
        ISyncService? syncService = null,
        IPrinterService? printerService = null,
        ICashFeatureService? features = null,
        MoneyPolicy? moneyPolicy = null,
        string shiftId = "",
        string? sellerId = null,
        string? cashId = null,
        bool isOnline = false)
    {
        _window = window;
        _returnService = returnService;
        _cashOperationService = cashOperationService;
        _expenseDocumentService = expenseDocumentService;
        _counterpartyService = counterpartyService;
        _settingsService = settingsService;
        _productService = productService;
        _syncService = syncService;
        _printerService = printerService;
        _features = features;
        _moneyPolicy = moneyPolicy ?? MoneyPolicy.Default;
        _shiftId = shiftId;
        _sellerId = sellerId;
        _cashId = cashId;
        IsOnline = isOnline;

        if (_syncService != null)
            _syncService.SyncStatusChanged += OnSyncStatusChanged;

        if (window != null)
        {
            window.Closed += OnWindowClosed;
            _ = LoadSalesAsync();
        }
    }

    /// <summary>The Close button is only one way out of this dialog — the window
    /// chrome, Alt+F4 and the owner closing are others, and every one of them must
    /// drop the sync subscription. Wired to Closed rather than to the command so a
    /// closed screen's view model stops being kept alive by SyncService for the rest
    /// of the register's uptime.</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_syncService != null) _syncService.SyncStatusChanged -= OnSyncStatusChanged;
        if (_window != null) _window.Closed -= OnWindowClosed;
    }

    private void OnSyncStatusChanged(object? sender, bool isOnline)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsOnline = isOnline);

    private async Task LoadSalesAsync()
    {
        if (_returnService == null) return;
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
            SetReturnedLines(Array.Empty<ReturnLineVm>());
    }

    private async Task LoadLinesAsync(string expenseId)
    {
        if (_returnService == null) return;
        IsLoadingLines = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var body = await _returnService.GetReturnableLinesAsync(expenseId);
            if (SelectedSale?.Id != expenseId) return; // selection changed during load; ignore stale result
            SetReturnedLines(body.Details.Select(d => new ReturnLineVm(d)));
        }
        catch (Exception)
        {
            if (SelectedSale?.Id != expenseId) return; // stale failure for a no-longer-selected sale
            ErrorMessage = I18nService.Instance["ReturnFailed"];
            SetReturnedLines(Array.Empty<ReturnLineVm>());
        }
        finally
        {
            if (SelectedSale?.Id == expenseId)
                IsLoadingLines = false;
        }
    }

    /// <summary>Replaces the returned-goods basket (e.g. after loading a receipt)
    /// and rewires per-line notifications so a quantity edit on any line updates
    /// the totals on screen.</summary>
    public void SetReturnedLines(IEnumerable<ReturnLineVm> lines)
    {
        foreach (var l in ReturnedLines) l.RefundChanged -= OnBasketChanged;
        ReturnedLines = new ObservableCollection<ReturnLineVm>(lines);
        foreach (var l in ReturnedLines) l.RefundChanged += OnBasketChanged;
        RaiseTotalsChanged();
    }

    /// <summary>Adds one line to the issued-goods basket (a product the cashier
    /// picked to replace the returned item) and wires its quantity changes into
    /// the totals.</summary>
    public void AddIssuedLine(CartItem item)
    {
        item.PropertyChanged += OnIssuedLinePropertyChanged;
        IssuedLines.Add(item);
        RaiseTotalsChanged();
    }

    public void RemoveIssuedLine(CartItem item)
    {
        item.PropertyChanged -= OnIssuedLinePropertyChanged;
        IssuedLines.Remove(item);
        RaiseTotalsChanged();
    }

    /// <summary>Looks up the search box's text as a barcode first (an exact scan),
    /// falling back to a name search and taking the first match — the same order
    /// PosViewModel's own barcode handling favours an exact code over a fuzzy
    /// search. A product already in the basket just gets one more unit rather
    /// than a second line.</summary>
    [RelayCommand]
    private async Task AddIssuedProduct()
    {
        if (_productService == null || string.IsNullOrWhiteSpace(IssuedSearchQuery)) return;
        var query = IssuedSearchQuery.Trim();
        ErrorMessage = null;

        var product = await _productService.GetProductByBarcodeAsync(query)
            ?? (await _productService.SearchProductsAsync(query)).FirstOrDefault();

        if (product == null)
        {
            ErrorMessage = I18nService.Instance["NoProductsFound"];
            return;
        }

        var existing = IssuedLines.FirstOrDefault(l => l.Product.Id == product.Id);
        if (existing != null)
            existing.Quantity += 1;
        else
            AddIssuedLine(new CartItem { Product = product, Quantity = 1 });

        IssuedSearchQuery = string.Empty;
    }

    [RelayCommand]
    private void IncrementIssued(CartItem item) => item.Quantity += 1;

    [RelayCommand]
    private void DecrementIssued(CartItem item)
    {
        if (item.Quantity > 1) item.Quantity -= 1;
        else RemoveIssuedLine(item);
    }

    private void OnIssuedLinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CartItem.Quantity))
            RaiseTotalsChanged();
    }

    private void OnBasketChanged() => RaiseTotalsChanged();

    private void RaiseTotalsChanged()
    {
        // The single funnel every basket edit goes through, so also where a
        // changed basket stops being a retry of the previous exchange.
        _documentHash = null;
        _returnBooked = false;
        OnPropertyChanged(nameof(ReturnedTotal));
        OnPropertyChanged(nameof(IssuedTotal));
        OnPropertyChanged(nameof(Difference));
        OnPropertyChanged(nameof(CustomerPays));
        OnPropertyChanged(nameof(TillPays));
        OnPropertyChanged(nameof(RefundDue));
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary>Step 1's body: the lines the customer handed back, dated today rather
    /// than with the original sale's date — carrying the old date here split one
    /// exchange across two reporting periods. The server binds selected_date as
    /// datetime=2006-01-02, so the format is not negotiable.</summary>
    public ReturnRequest BuildReturnRequest() => new()
    {
        SelectedDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Details = ReturnedLines.Where(l => l.ReturnQty > 0)
            .Select(l => new ReturnLineRequest { Product = l.ProductId, Quantity = l.ReturnQty })
            .ToList(),
    };

    /// <summary>Step 2's body: the till hands back the <em>whole</em> returned total,
    /// not the difference. Step 3 then takes the replacement's full price, so the
    /// drawer nets to the difference either way — and every document involved stays an
    /// ordinary one, which is the entire point of doing it this way.</summary>
    public CashExpenseRequest BuildPayoutRequest(string counterpartyId, string paymentCategoryId) => new()
    {
        OperationType = "expense",
        Cash = _cashId ?? string.Empty,
        Counterparty = counterpartyId,
        Note = $"Обмен по чеку {SelectedSale?.DocumentNumber}".TrimEnd(),
        Details = new List<CashExpenseDetail>
        {
            new() { PaymentCategory = paymentCategoryId, Amount = ReturnedTotal },
        },
    };

    /// <summary>Step 3's body: an ordinary cash sale of the replacement goods, paid in
    /// full. Not the difference — the payout above already handed the returned money
    /// over, and a sale booked for less than its own goods are worth is a sale the
    /// back office cannot read.</summary>
    public DocumentRequest BuildSaleRequest()
    {
        _documentHash ??= Guid.NewGuid().ToString();

        return new DocumentRequest
        {
            DocumentHash = _documentHash,
            SellerId = _sellerId,
            ShiftId = _shiftId,
            SoldSource = SoldSourcesEnum.CASH,
            // Mirrors what the register's own plain sale sends (PosViewModel.Pay):
            // SellPrice below is already the discounted price, so the document-level
            // discount is declared in money and is zero here — this screen has no
            // manual-discount control, and the per-line DiscountPercent stays
            // informational. Left at the default "percent", the server would take each
            // line's catalog percent off an already-discounted price and land under the
            // declared to_pay.
            Payment = new Payment
            {
                ToPay = IssuedTotal,
                PaidInCash = IssuedTotal,
                PaidByCreditCard = 0m,
                DiscountType = "cash",
                Discount = 0m,
                Remained = 0m,
            },
            Products = IssuedLines.Select(l => new DocumentProduct
            {
                Name = l.Product.Name,
                ProductId = l.Product.Id,
                Quantity = l.Quantity,
                SellPrice = l.Product.Price,
                PriceBeforeDiscount = l.Product.OriginalPrice ?? l.Product.Price,
                DiscountPercent = l.Product.DiscountPercent ?? 0,
            }).ToList(),
        };
    }

    [RelayCommand]
    private async Task SubmitExchange()
    {
        if (SelectedSale == null || !CanSubmit) return;
        if (_returnService == null || _cashOperationService == null || _expenseDocumentService == null) return;

        IsSubmitting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            // Everything that can be checked without writing anything is checked here,
            // before the first call. Discovering an unset payout category at step 2
            // would leave a return already booked and nothing to undo it with.
            var categoryId = _settingsService?.ExchangePayoutCategoryId;
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

            // Snapshot the receipt lines before anything can change them — a later
            // basket clear would otherwise move the figures out from under the receipt.
            var returnedReceiptLines = ReturnedLines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnReceiptLine(l.Name, l.ReturnQty, l.LineRefund)).ToList();
            var issuedReceiptLines = IssuedLines
                .Select(l => new ReturnReceiptLine(l.Product.Name, (int)l.Quantity, l.LineTotal)).ToList();
            var difference = Difference;

            // ---- 1. the return -------------------------------------------------
            // Skipped when a previous press already booked it for these same baskets:
            // there is no way to cancel a return, so a retry must not make a second.
            if (!_returnBooked)
            {
                string? returnError = null;
                bool returnOk;
                try
                {
                    returnOk = await _returnService.CreateReturnAsync(SelectedSale.Id, BuildReturnRequest());
                }
                catch (Exception ex)
                {
                    returnOk = false;
                    returnError = ex.Message;
                }

                if (!returnOk)
                {
                    ErrorMessage = ReturnFailed(returnError);
                    return;
                }
                _returnBooked = true;
                HasBookedDocument = true;
            }

            // ---- 2. the till payout --------------------------------------------
            // Nothing to hand over when the returned goods were worth nothing (a fully
            // discounted line, say), and the server binds the amount as gt=0 — posting
            // a zero would be a 400 with the return already booked, over money that
            // never had to move.
            if (ReturnedTotal > 0m)
            {
                var payout = await _cashOperationService.CreateCashExpenseAsync(
                    BuildPayoutRequest(counterpartyId!, categoryId!));
                if (!payout.Success)
                {
                    ErrorMessage = PayoutFailed(payout.Message);
                    return;
                }
            }

            // ---- 3. the replacement sale ---------------------------------------
            ExpenseDocumentOutcome sale;
            try
            {
                sale = await _expenseDocumentService.CreateExpenseDocumentDetailedAsync(BuildSaleRequest());
            }
            catch (Exception ex)
            {
                ErrorMessage = SaleFailed(ex.Message);
                return;
            }

            if (!sale.Posted && !sale.Queued)
            {
                ErrorMessage = SaleFailed(null);
                return;
            }

            await RunPostExchangeActionsAsync(returnedReceiptLines, issuedReceiptLines, difference, sale.DocumentNumber);

            foreach (var item in IssuedLines.ToList()) RemoveIssuedLine(item);
            await LoadLinesAsync(SelectedSale.Id);
            // After the reload, not before it: LoadLinesAsync clears SuccessMessage on
            // its way in, so a confirmation set any earlier never reaches the screen.
            SuccessMessage = sale.Posted ? ExchangeDone : ExchangeDoneSaleQueued;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task<string?> ResolvePayoutCounterpartyAsync()
    {
        if (!string.IsNullOrWhiteSpace(_payoutCounterpartyId)) return _payoutCounterpartyId;
        if (_counterpartyService == null) return null;
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

    // What the cashier reads. Written out in Russian rather than routed through
    // I18nService on purpose: each of these names exactly which of the three legs
    // went through and which did not, and that is the only information that tells a
    // cashier — and the back office after them — what state the books are in. A
    // generic "ошибка обмена" would be worse than silence here.
    public const string PayoutCategoryNotConfigured =
        "Обмен не настроен: не выбрана статья расхода для выдачи из кассы. Задайте её в настройках. Ни один документ не создан.";
    public const string CashNotKnown =
        "Обмен невозможен: касса не определена. Переоткройте смену. Ни один документ не создан.";
    public const string CounterpartyNotResolved =
        "Обмен невозможен: не удалось определить контрагента для выдачи из кассы. Ни один документ не создан.";
    public const string ExchangeDone = "Обмен выполнен: возврат, выдача из кассы и продажа проведены.";
    public const string ExchangeDoneSaleQueued =
        "Обмен выполнен: возврат и выдача из кассы проведены, продажа сохранена локально и уйдёт на сервер при появлении связи.";

    public static string ReturnFailed(string? reason)
        => $"Возврат не прошёл{Because(reason)}. Выдача из кассы и продажа не проводились, ни один документ не создан.";

    public static string PayoutFailed(string? reason)
        => $"Возврат проведён, выдача из кассы не прошла{Because(reason)}, продажа не проводилась. "
           + "Разберите расхождение в бэк-офисе.";

    public static string SaleFailed(string? reason)
        => $"Возврат проведён, выдача из кассы проведена, продажа не прошла{Because(reason)}. "
           + "Разберите расхождение в бэк-офисе.";

    private static string Because(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason.Trim()}";

    /// <summary>Same store-level switches ReturnsViewModel already reads for a
    /// return (RunPostReturnActionsAsync) — reused rather than duplicated: an
    /// exchange prints on the same printer and opens the same drawer, and the
    /// task deliberately did not introduce separate flags for it.</summary>
    private async Task RunPostExchangeActionsAsync(
        IReadOnlyList<ReturnReceiptLine> returnedReceiptLines,
        IReadOnlyList<ReturnReceiptLine> issuedReceiptLines,
        decimal difference, string documentNumber)
    {
        // Only when money actually moves — an exchange priced exactly even has
        // nothing for the drawer to hand over or collect.
        if (difference != 0m
            && (_features?.Current.IsEnabled(CashFeatureCodes.ReturnOpenDrawer) ?? true)
            && _printerService != null)
        {
            try { await _printerService.OpenCashDrawerAsync(); } catch { }
        }
        if ((_features?.Current.IsEnabled(CashFeatureCodes.ReturnPrintReceipt) ?? true)
            && _printerService != null)
        {
            try { await _printerService.PrintExchangeReceiptAsync(returnedReceiptLines, issuedReceiptLines, difference, documentNumber); }
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

    /// <summary>Closing the window is enough — OnWindowClosed is what unsubscribes,
    /// so every other way of dismissing the dialog cleans up identically.</summary>
    [RelayCommand]
    private void Close() => _window?.Close();
}
