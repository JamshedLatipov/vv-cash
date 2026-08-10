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
    private readonly IQuoteService? _quoteService;
    private readonly System.Net.Http.HttpClient? _httpClient;
    private readonly MoneyPolicy _moneyPolicy;
    private readonly string _shiftId;
    private readonly string? _sellerId;
    private readonly string? _cashId;
    private readonly string? _warehouseId;

    /// <summary>The server's pricing of the issued basket, or null when nothing has been
    /// priced yet (empty basket, offline, or the quote failed). Same role
    /// ICartService.Quote plays for the POS cart, and read the same way — the register
    /// must not price a replacement off its own cached catalog while a promotion the
    /// server knows about is running, which is exactly how an exchange came to charge the
    /// undiscounted price for goods the original sale had bought at half of it.</summary>
    private QuoteResult? _issuedQuote;

    /// <summary>Cancels the quote in flight when a newer basket edit supersedes it, so a
    /// slow reply can never land on top of a basket it no longer describes. Mirrors
    /// PosViewModel's own _quoteCts.</summary>
    private System.Threading.CancellationTokenSource? _issuedQuoteCts;

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

    /// <summary>The one receipt being exchanged, found by its number. There is
    /// deliberately no browsable list of sales behind this: the exchange screen pays the
    /// whole returned total out of the till, so a cashier who can page through every
    /// receipt the register ever rang can pick an arbitrary one and move drawer money
    /// against it. A number has to come off a slip the customer actually handed over.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSale))]
    private ExpenseListItem? _selectedSale;

    /// <summary>What the cashier typed into the receipt-number box. Applied only when
    /// <see cref="SearchSale"/> runs, not per keystroke.</summary>
    [ObservableProperty] private string _documentNumberQuery = string.Empty;

    [ObservableProperty] private bool _isLoadingSales;
    [ObservableProperty] private bool _isLoadingLines;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _successMessage;
    [ObservableProperty] private string _issuedSearchQuery = string.Empty;

    /// <summary>What the cashier scanned or typed into the returned-side barcode
    /// box, once a receipt's lines are already on screen.</summary>
    [ObservableProperty] private string _returnScanQuery = string.Empty;

    /// <summary>The return and the till payout have no offline queue behind them, so
    /// with no connection there is nothing for the submit button to offer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isSubmitting;

    public bool HasSelectedSale => SelectedSale != null;

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

    /// <summary>What the replacement goods cost before any discount — the sum of the
    /// lines as they read on screen. Split out from <see cref="IssuedTotal"/> so the
    /// screen can show the discount as its own figure, the way the POS totals block
    /// already does; the two are equal whenever nothing is discounted.</summary>
    public decimal IssuedSubtotal => _moneyPolicy.Round(IssuedLines.Sum(l => l.LineTotal));

    /// <summary>What the server takes off the replacement goods: promotions, and the
    /// customer's own card when one applies. Zero while the basket is unpriced (empty,
    /// offline, or the quote failed) — in which case the exchange falls back to catalog
    /// pricing exactly as the POS cart does.</summary>
    public decimal IssuedDiscount => _moneyPolicy.Round(_issuedQuote?.DiscountTotal ?? 0m);

    public bool HasIssuedDiscount => IssuedDiscount > 0m;

    public decimal IssuedTotal => _moneyPolicy.Round(IssuedSubtotal - IssuedDiscount);
    public decimal Difference => IssuedTotal - ReturnedTotal;

    /// <summary>True once the replacement costs more than what came back.</summary>
    public bool CustomerPays => Difference > 0;

    /// <summary>True once the replacement costs less — the till owes the customer.</summary>
    public bool TillPays => Difference < 0;

    /// <summary>Absolute amount the till hands back when <see cref="TillPays"/> —
    /// shown without a minus sign, since the label already carries the direction.</summary>
    public decimal RefundDue => TillPays ? -Difference : 0m;

    /// <summary>How the customer settles the replacement sale. The three-document shape
    /// means step 2 hands the whole returned total out of the till and step 3 takes the
    /// replacement's full price back, so the drawer nets to the difference — but only if
    /// the sale records which way that money actually moved. It was hardcoded to cash,
    /// so a difference settled on a card terminal left the books saying the drawer took
    /// money it never saw, and the shift did not reconcile.</summary>
    [ObservableProperty]
    private bool _payByCard;

    /// <summary>Whether the card option applies at all. Only when the customer owes
    /// something: if the till is the one paying out, the money leaves through the cash
    /// payout of step 2 and there is nothing to put on a terminal.</summary>
    public bool CanPayByCard => CustomerPays;

    public bool CanSubmit => IsOnline && !IsSubmitting
        && ReturnedLines.Any(l => l.ReturnQty > 0)
        && IssuedLines.Any(l => l.Quantity > 0);

    /// <param name="shiftId">Stamped onto the issued document, same as Pay() does
    /// for an ordinary sale.</param>
    /// <param name="cashId">The cash this register is signed in as, from
    /// ISessionContext. Only the till payout makes the client name it.</param>
    /// <param name="warehouseId">From ISessionContext, and normally null — the register
    /// is not told which warehouse its cash stocks from, and the server resolves it from
    /// the cash token instead. Passed through only so the quote request can name it on a
    /// register that does happen to know.</param>
    /// <param name="httpClient">Only ever used to pull product thumbnails; null leaves
    /// both baskets showing the placeholder icon and changes nothing else.</param>
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
        IQuoteService? quoteService = null,
        System.Net.Http.HttpClient? httpClient = null,
        MoneyPolicy? moneyPolicy = null,
        string shiftId = "",
        string? sellerId = null,
        string? cashId = null,
        string? warehouseId = null,
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
        _quoteService = quoteService;
        _httpClient = httpClient;
        _moneyPolicy = moneyPolicy ?? MoneyPolicy.Default;
        _shiftId = shiftId;
        _sellerId = sellerId;
        _cashId = cashId;
        _warehouseId = warehouseId;
        IsOnline = isOnline;

        if (_syncService != null)
            _syncService.SyncStatusChanged += OnSyncStatusChanged;

        // No sales are fetched on open any more: with the browsable list gone there is
        // nothing to fill until the cashier enters a receipt number.
        if (window != null)
            window.Closed += OnWindowClosed;
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
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var wasOffline = !IsOnline;
            IsOnline = isOnline;
            // A basket built while the connection was down was priced off the register's
            // own catalog, with every promotion missed. Coming back online is the first
            // moment that can be corrected, and leaving it uncorrected would sell the
            // replacement at the undiscounted price with the cashier none the wiser.
            if (isOnline && wasOffline && IssuedLines.Count > 0) TriggerIssuedRequote();
        });

    /// <summary>Finds the one receipt the customer is exchanging against, by the number
    /// printed on their slip. The backend match is exact and scoped to this register's
    /// own cash, and it drops the default today-only date range for a numbered lookup —
    /// so a slip from an earlier day is still reachable without the register ever being
    /// able to page through receipts it has no business seeing.</summary>
    [RelayCommand]
    private async Task SearchSale()
    {
        if (_returnService == null) return;
        if (string.IsNullOrWhiteSpace(DocumentNumberQuery))
        {
            // Blank is "no receipt", not "every receipt": sending it would ask the server
            // for an unfiltered page, which is the browsable list this screen just lost.
            ClearSelectedSale();
            return;
        }

        IsLoadingSales = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var res = await _returnService.GetSalesAsync(1, DocumentNumberQuery);
            var match = res.Body.FirstOrDefault();
            if (match == null)
            {
                ClearSelectedSale();
                ErrorMessage = I18nService.Instance["ReceiptNotFound"];
                return;
            }
            // Assigning this is what loads the returnable lines — see OnSelectedSaleChanged.
            SelectedSale = match;
        }
        catch (Exception)
        {
            ClearSelectedSale();
            ErrorMessage = I18nService.Instance["NoConnection"];
        }
        finally
        {
            IsLoadingSales = false;
        }
    }

    /// <summary>Drops the receipt and everything that hung off it. Assigning null is
    /// enough for the returned basket (OnSelectedSaleChanged clears it), but the issued
    /// one belongs to the exchange as a whole and would otherwise survive into the next
    /// receipt the cashier looks up.</summary>
    private void ClearSelectedSale()
    {
        SelectedSale = null;
        foreach (var item in IssuedLines.ToList()) RemoveIssuedLine(item);
    }

    /// <summary>Clears the search box and the receipt with it, so the screen goes back to
    /// its opening state rather than leaving a receipt selected under an empty box.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        DocumentNumberQuery = string.Empty;
        ErrorMessage = null;
        ClearSelectedSale();
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
            // Fire-and-forget, exactly like the catalog grid's own image loading: the
            // lines are already usable and a thumbnail arriving late costs nothing,
            // whereas awaiting it would hold the whole basket behind the slowest jpeg.
            _ = AttachReturnedImagesAsync(expenseId);
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

    /// <summary>Puts a thumbnail on every returned line the register can find a product
    /// for. The receipt endpoint carries no image, so the picture is matched out of the
    /// synced catalog by product id — a product the register has never synced simply
    /// keeps its placeholder.
    ///
    /// Bails out the moment the cashier looks up a different receipt: the lines this was
    /// started for are gone by then, and stamping bitmaps onto them would only race the
    /// newer load.</summary>
    private async Task AttachReturnedImagesAsync(string expenseId)
    {
        if (_productService == null || _httpClient == null) return;

        IEnumerable<Product> catalog;
        try
        {
            catalog = await _productService.GetAllProductsAsync();
        }
        catch (Exception)
        {
            return; // no catalog, no pictures — the lines themselves are unaffected
        }
        if (SelectedSale?.Id != expenseId) return;

        var byId = new Dictionary<string, string>();
        foreach (var p in catalog)
            if (!string.IsNullOrWhiteSpace(p.ImagePath) && !byId.ContainsKey(p.Id))
                byId[p.Id] = p.ImagePath;

        foreach (var line in ReturnedLines.ToList())
        {
            if (SelectedSale?.Id != expenseId) return;
            if (!byId.TryGetValue(line.ProductId, out var imagePath)) continue;
            var bitmap = await ProductImageLoader.GetAsync(_httpClient, _settingsService?.BackendUrl, imagePath);
            if (bitmap != null && SelectedSale?.Id == expenseId) line.ImageBitmap = bitmap;
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
        OnIssuedBasketChanged();
    }

    public void RemoveIssuedLine(CartItem item)
    {
        item.PropertyChanged -= OnIssuedLinePropertyChanged;
        IssuedLines.Remove(item);
        OnIssuedBasketChanged();
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
        {
            existing.Quantity += 1;
        }
        else
        {
            AddIssuedLine(new CartItem { Product = product, Quantity = 1 });
            // The product came out of the catalog with only its image path filled in —
            // nothing has decoded the picture for this screen yet. Fire-and-forget, same
            // as the catalog grid: the line is usable without it.
            if (_httpClient != null)
                _ = ProductImageLoader.LoadIntoAsync(_httpClient, _settingsService?.BackendUrl, product);
        }

        IssuedSearchQuery = string.Empty;
    }

    /// <summary>Scans the item being brought back instead of hunting for its line
    /// among the returned ones: a match bumps ReturnQty by one, same as pressing
    /// the line's own + button, and briefly highlights the card.
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

        var line = ReturnedLines.FirstOrDefault(l => l.IsReturnable && l.Barcode == code);
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
            System.Diagnostics.Debug.WriteLine($"[ExchangeViewModel] Scan highlight flash failed: {ex}");
        }
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
        // The quote-stamped properties (price, discount, percent) are excluded on purpose:
        // applying a quote sets them on every line, and treating that as a basket edit
        // would re-enter the quote path in a loop and, worse, clear the retry bookkeeping
        // RaiseTotalsChanged resets.
        if (e.PropertyName == nameof(CartItem.Quantity))
            OnIssuedBasketChanged();
    }

    private void OnBasketChanged() => RaiseTotalsChanged();

    /// <summary>What a change to the issued basket goes through, as opposed to the
    /// returned one: the totals move exactly as before, and on top of that the server is
    /// asked to re-price the replacement goods, because which promotions apply depends on
    /// what is in the basket.</summary>
    private void OnIssuedBasketChanged()
    {
        RaiseTotalsChanged();
        TriggerIssuedRequote();
    }

    private void RaiseTotalsChanged()
    {
        // The single funnel every basket edit goes through, so also where a
        // changed basket stops being a retry of the previous exchange.
        //
        // HasBookedDocument is deliberately NOT reset here, unlike everything else in
        // this block: it tracks whether this screen ever wrote to the server, which a
        // basket edit cannot undo — see its own doc comment. Resetting it would tell
        // PosViewModel nothing happened and leave the previous seller confirmed.
        _documentHash = null;
        _returnBooked = false;
        NotifyTotalsChanged();
    }

    /// <summary>The notifications alone, without the retry reset. A quote landing changes
    /// every figure on screen but is not a basket edit: clearing <see cref="_returnBooked"/>
    /// for it would let a retry after a failed payout book the return a second time, and
    /// there is no endpoint to cancel a return.</summary>
    private void NotifyTotalsChanged()
    {
        OnPropertyChanged(nameof(ReturnedTotal));
        OnPropertyChanged(nameof(IssuedSubtotal));
        OnPropertyChanged(nameof(IssuedDiscount));
        OnPropertyChanged(nameof(HasIssuedDiscount));
        OnPropertyChanged(nameof(IssuedTotal));
        OnPropertyChanged(nameof(Difference));
        OnPropertyChanged(nameof(CustomerPays));
        OnPropertyChanged(nameof(TillPays));
        OnPropertyChanged(nameof(CanPayByCard));
        OnPropertyChanged(nameof(RefundDue));
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary>Asks the server to price the issued basket, superseding whatever quote was
    /// already in flight. Fire-and-forget by design, like PosViewModel's own requote: the
    /// screen stays usable while it runs and a failure only means catalog pricing, never a
    /// blocked exchange.</summary>
    private void TriggerIssuedRequote()
    {
        _issuedQuoteCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _issuedQuoteCts = cts;
        _ = RequoteIssuedAsync(cts);
    }

    private async Task RequoteIssuedAsync(System.Threading.CancellationTokenSource cts)
    {
        // No card and no promo code: this screen has neither control. Auto-applied
        // promotions are the whole point and need no cashier input — the server can only
        // put them into best-deal if it is asked to price the basket at all.
        if (_quoteService == null || !IsOnline || IssuedLines.Count == 0)
        {
            if (IsCurrentIssuedQuote(cts)) ApplyIssuedQuote(null);
            return;
        }

        QuoteResult? result;
        try
        {
            result = await _quoteService.QuoteAsync(
                QuoteRequestBuilder.Build(IssuedLines, _warehouseId, cardIdentifier: null, code: null),
                cts.Token);
        }
        catch (Exception ex)
        {
            // Never blocks the exchange — the basket just falls back to catalog pricing,
            // same as the POS cart does when a quote fails.
            System.Diagnostics.Debug.WriteLine($"[ExchangeViewModel] Issued requote failed: {ex}");
            result = null;
        }

        if (!IsCurrentIssuedQuote(cts)) return; // a newer basket edit superseded this one
        ApplyIssuedQuote(result);
    }

    private bool IsCurrentIssuedQuote(System.Threading.CancellationTokenSource cts)
        => ReferenceEquals(_issuedQuoteCts, cts) && !cts.IsCancellationRequested;

    /// <summary>Stamps the server's per-line unit price onto the basket and refreshes the
    /// totals. Matched by product id, exactly as CartService.ApplyQuotedPrices does —
    /// AddIssuedProduct merges a repeated product onto one line, so the id is unambiguous.
    /// The stamped price is the line's price BEFORE discount (that is what the quote's
    /// unit_price carries); the discount itself stays document-level, in
    /// <see cref="IssuedDiscount"/>, which is the shape the sale request declares it in.
    ///
    /// The quote's per-line discount is stamped alongside it, purely so each card can show
    /// what came off it. The sale is still booked with one document-level figure — a
    /// cashier reading a card that says the catalog price while the footer quietly shows a
    /// discount cannot tell which of the replacement goods a running promotion actually
    /// touched, and that is what the screen was doing.</summary>
    private void ApplyIssuedQuote(QuoteResult? result)
    {
        _issuedQuote = result;
        foreach (var item in IssuedLines)
        {
            var line = result?.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
            item.QuotedUnitPrice = line?.UnitPrice;
            // Per unit, not per line, so the card still reads as discounted during the
            // window between a quantity change and the replacement quote landing.
            item.QuotedUnitDiscount = line != null && line.Quantity > 0
                ? line.DiscountAmount / line.Quantity
                : null;
            item.QuotedDiscountPercent = line?.DiscountPercent ?? 0m;
        }
        NotifyTotalsChanged();
    }

    /// <summary>What one issued line is actually worth once the basket's discount is
    /// taken off it — the quote's own per-line figure, so the printed lines add up to
    /// <see cref="IssuedTotal"/> instead of to the pre-discount subtotal. Falls back to
    /// the undiscounted line total for a basket nothing priced.</summary>
    private decimal IssuedLineFinalTotal(CartItem item)
    {
        var line = _issuedQuote?.Lines.FirstOrDefault(l => l.ProductId == item.Product.Id);
        return _moneyPolicy.Round(line?.FinalLineTotal ?? item.LineTotal);
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
            // The quote the figures on screen came from, so the server books the
            // replacement against the same pricing it just handed out rather than
            // re-deriving one that may already have moved.
            QuoteId = _issuedQuote?.QuoteId,
            SoldSource = SoldSourcesEnum.CASH,
            // Mirrors what the register's own plain sale sends (PosViewModel.Pay):
            // SellPrice below is the price BEFORE discount, and the whole discount is
            // declared once, document-level, in money. Declaring it as "percent" instead
            // would have the server take each line's catalog percent off a price that
            // already had the discount in it and land under the declared to_pay.
            //
            // Discount was hardcoded to zero here until this screen learned to quote:
            // with no quote there was no discount to declare, which is precisely why an
            // exchange charged the full catalog price for goods a running promotion had
            // sold at half of it.
            // Whole replacement price into one slot or the other — never split, since
            // the cashier states one method for the difference and the rest of this
            // total is the returned money going straight back out through step 2's
            // payout. PayByCard only reaches here when CanPayByCard allowed it.
            Payment = new Payment
            {
                ToPay = IssuedTotal,
                PaidInCash = PayByCard && CanPayByCard ? 0m : IssuedTotal,
                PaidByCreditCard = PayByCard && CanPayByCard ? IssuedTotal : 0m,
                DiscountType = "cash",
                Discount = IssuedDiscount,
                Remained = 0m,
            },
            Products = IssuedLines.Select((l, lineIndex) =>
            {
                // Same resolver the POS sale uses, with no offline promotion to consider:
                // CanSubmit already requires the register to be online, so a basket that
                // reaches here was either priced by the server or not priced at all.
                var (pct, before) = QuoteLineResolver.Resolve(
                    _issuedQuote, offlinePromotion: null, l, lineIndex, _moneyPolicy);
                return new DocumentProduct
                {
                    Name = l.Product.Name,
                    ProductId = l.Product.Id,
                    Quantity = l.Quantity,
                    // The quoted price when a quote priced this line, the cached one
                    // otherwise — the server flags a line is_suspicious when sell_price
                    // differs from its catalog price, so sending a stale cached price
                    // would flag every honest exchange.
                    SellPrice = l.UnitPrice,
                    PriceBeforeDiscount = before,
                    DiscountPercent = pct,
                };
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
            // Priced after the discount, not at the catalog subtotal: the slip prints these
            // lines and then the difference, with no discount line of its own, so
            // undiscounted lines would simply not add up to the money that changed hands.
            var issuedReceiptLines = IssuedLines
                .Select(l => new ReturnReceiptLine(l.Product.Name, (int)l.Quantity, IssuedLineFinalTotal(l))).ToList();
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
                // Both flip here, but they are not the same fact: _returnBooked is retry
                // bookkeeping for these particular baskets and dies with them, while
                // HasBookedDocument records that the one irreversible write in this flow
                // has happened and stays true for the rest of the screen's life.
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
            try
            {
                await _printerService.PrintExchangeReceiptAsync(
                    returnedReceiptLines, issuedReceiptLines, difference, documentNumber,
                    SelectedSale?.WarehouseName, SelectedSale?.Creator, SelectedSale?.FormattedSelectedDate);
            }
            catch { }
        }
    }

    /// <summary>Closing the window is enough — OnWindowClosed is what unsubscribes,
    /// so every other way of dismissing the dialog cleans up identically.</summary>
    [RelayCommand]
    private void Close() => _window?.Close();
}
