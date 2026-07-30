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
/// comes back and what goes out instead, and posting the result.
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
    private readonly IExchangeService? _exchangeService;
    private readonly IProductService? _productService;
    private readonly ISyncService? _syncService;
    private readonly IPrinterService? _printerService;
    private readonly ICashFeatureService? _features;
    private readonly MoneyPolicy _moneyPolicy;
    private readonly string _shiftId;
    private readonly string? _sellerId;

    /// <summary>Idempotency key for the whole exchange, minted once per basket
    /// state and not per submit press. When the first attempt commits server-side
    /// but its reply is lost (timeout, proxy, dropped wifi), the cashier presses
    /// submit again — with the same hash the server answers 409, whereas a fresh
    /// one books a second return plus a second sale for the same goods. Cleared
    /// whenever either basket changes, so a genuinely different exchange never
    /// reuses it.</summary>
    private string? _documentHash;

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

    /// <summary>Exchanges are online-only (see ExchangeService remarks): with no
    /// connection there is nowhere to queue the request, so the submit button
    /// must not offer it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isOnline;

    /// <summary>From ReturnDetailBody.ExchangeAllowed — false once the receipt is
    /// past the store's exchange window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _exchangeAllowed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isSubmitting;

    public bool HasSelectedSale => SelectedSale != null;
    public bool HasMorePages => CurrentPage < PageCount;

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

    public bool CanSubmit => IsOnline && ExchangeAllowed && !IsSubmitting
        && ReturnedLines.Any(l => l.ReturnQty > 0)
        && IssuedLines.Any(l => l.Quantity > 0);

    /// <param name="shiftId">Stamped onto the issued document, same as Pay() does
    /// for an ordinary sale.</param>
    /// <param name="isOnline">Snapshot of the register's connectivity at the
    /// moment the screen opens; kept live afterwards via <paramref name="syncService"/>.</param>
    public ExchangeViewModel(
        Window? window = null,
        IReturnService? returnService = null,
        IExchangeService? exchangeService = null,
        IProductService? productService = null,
        ISyncService? syncService = null,
        IPrinterService? printerService = null,
        ICashFeatureService? features = null,
        MoneyPolicy? moneyPolicy = null,
        string shiftId = "",
        string? sellerId = null,
        bool isOnline = false)
    {
        _window = window;
        _returnService = returnService;
        _exchangeService = exchangeService;
        _productService = productService;
        _syncService = syncService;
        _printerService = printerService;
        _features = features;
        _moneyPolicy = moneyPolicy ?? MoneyPolicy.Default;
        _shiftId = shiftId;
        _sellerId = sellerId;
        IsOnline = isOnline;

        if (_syncService != null)
            _syncService.SyncStatusChanged += OnSyncStatusChanged;

        if (window != null)
            _ = LoadSalesAsync();
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
            ExchangeAllowed = body.ExchangeAllowed;
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
        OnPropertyChanged(nameof(ReturnedTotal));
        OnPropertyChanged(nameof(IssuedTotal));
        OnPropertyChanged(nameof(Difference));
        OnPropertyChanged(nameof(CustomerPays));
        OnPropertyChanged(nameof(TillPays));
        OnPropertyChanged(nameof(RefundDue));
        OnPropertyChanged(nameof(CanSubmit));
    }

    public ExchangeRequest BuildRequest()
    {
        var date = SelectedSale?.SelectedDate;
        var dateOnly = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : (date ?? string.Empty);

        _documentHash ??= Guid.NewGuid().ToString();

        return new ExchangeRequest
        {
            DocumentHash = _documentHash,
            SelectedDate = dateOnly,
            Returned = ReturnedLines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnLineRequest { Product = l.ProductId, Quantity = l.ReturnQty })
                .ToList(),
            Issued = new DocumentRequest
            {
                // The server derives the replacement sale's own hash from the
                // exchange's and ignores whatever is sent here; the field is only
                // populated because the sale body requires it. Deliberately the
                // same value rather than a fresh Guid, so a retry sends a byte-for-byte
                // identical request.
                DocumentHash = _documentHash,
                SellerId = _sellerId,
                ShiftId = _shiftId,
                SoldSource = SoldSourcesEnum.CASH,
                // Mirrors what the register's own plain sale sends (PosViewModel.Pay):
                // SellPrice below is already the discounted price, so the
                // document-level discount is declared in money and is zero here —
                // this screen has no manual-discount control, and the per-line
                // DiscountPercent stays informational. Left at the default
                // "percent", the server took each line's catalog percent off an
                // already-discounted price, so its computed total came out under the
                // declared to_pay: a plain sale only flags that, an exchange is
                // refused with 400, making every discounted product unexchangeable.
                Payment = new Payment
                {
                    ToPay = IssuedTotal,
                    DiscountType = "cash",
                    Discount = 0m,
                },
                Products = IssuedLines.Select(l => new DocumentProduct
                {
                    ProductId = l.Product.Id,
                    Quantity = l.Quantity,
                    SellPrice = l.Product.Price,
                    PriceBeforeDiscount = l.Product.OriginalPrice ?? l.Product.Price,
                    DiscountPercent = l.Product.DiscountPercent ?? 0,
                }).ToList(),
            },
            // Intentional MVP simplification, not an oversight: the whole
            // difference always goes to paid_in_cash. There is no cash/card split
            // control on this screen — a future task can add one; until then this
            // is the one and only place a positive difference is ever charged.
            DifferencePayment = new ExchangeDifferencePayment
            {
                PaidInCash = CustomerPays ? Difference : 0m,
                PaidByCreditCard = 0m,
            },
        };
    }

    [RelayCommand]
    private async Task SubmitExchange()
    {
        if (SelectedSale == null || _exchangeService == null || !CanSubmit) return;
        IsSubmitting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var request = BuildRequest();
            var result = await _exchangeService.CreateExchangeAsync(SelectedSale.Id, request);
            if (result == null)
            {
                // No exchange happened server-side: nothing gets printed, the
                // drawer stays shut, and both baskets are left exactly as the
                // cashier built them so they can retry or cancel — see
                // ExchangeService's own remarks on why there is no offline
                // fallback to fall back to.
                ErrorMessage = I18nService.Instance["ExchangeFailed"];
                return;
            }

            // Snapshot the receipt lines before the baskets are cleared below —
            // RemoveIssuedLine would otherwise change IssuedTotal/Difference out
            // from under the receipt being printed.
            var returnedReceiptLines = ReturnedLines.Where(l => l.ReturnQty > 0)
                .Select(l => new ReturnReceiptLine(l.Name, l.ReturnQty, l.LineRefund)).ToList();
            var issuedReceiptLines = IssuedLines
                .Select(l => new ReturnReceiptLine(l.Product.Name, (int)l.Quantity, l.LineTotal)).ToList();

            // The server is the authority on money, same reason the document
            // number below comes from it and not from the screen: a price-drift
            // audit or a rounding difference could make its figure differ from
            // what this screen computed before the request went out.
            var difference = result.Difference;
            // The document that carries the exchange — from the server, never
            // invented locally.
            var documentNumber = result.ExpenseDocumentNumber ?? string.Empty;

            await RunPostExchangeActionsAsync(returnedReceiptLines, issuedReceiptLines, difference, documentNumber);

            SuccessMessage = I18nService.Instance["ExchangeSuccess"];
            foreach (var item in IssuedLines.ToList()) RemoveIssuedLine(item);
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

    [RelayCommand]
    private void Close()
    {
        if (_syncService != null) _syncService.SyncStatusChanged -= OnSyncStatusChanged;
        _window?.Close();
    }
}
