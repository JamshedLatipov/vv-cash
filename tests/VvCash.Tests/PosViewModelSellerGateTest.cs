using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.Services.Discounts;
using VvCash.Services.Hardware;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>Covers the Task 16 POS integration: the AddToCart seller gate (fires only at
/// the start of a receipt, never mid-sale), Touch() keeping the session alive on genuine
/// activity, the SellerId that Pay() stamps onto the outgoing DocumentRequest, and that
/// Dispose() actually severs the ISellerSession.CurrentChanged subscription.
///
/// This test project references no mocking library (see VvCash.Tests.csproj), so every
/// PosViewModel dependency below is a small hand-written fake. That is a deliberate choice
/// over stubbing PosViewModel itself: constructing the real view model is what lets these
/// tests exercise the actual wiring added in this task (AddToCart, Pay, Dispose) rather than
/// re-testing ISellerSession's own rules, which SellerSessionTest.cs already covers.
///
/// What this file does NOT cover: the seller chip's XAML binding, the SellerSwitchView
/// overlay actually appearing on screen, or App.axaml.cs's event wiring — none of that is
/// reachable without a running Avalonia application, so it was verified by reading the XAML/
/// code-behind, not by an automated test. Same reason for
/// PosViewModel.ShowReturnsDialogAsync / OpenExchange reading HasBookedDocument once
/// ShowDialog returns: both need a live Avalonia Window, which this xunit host never
/// provides — that wiring was verified by reading, and the flag's own correctness (when it
/// does and doesn't get set) is covered by ReturnsViewModelTest / ExchangeViewModelTest
/// instead.
public class PosViewModelSellerGateTest
{
    // ---------------------------------------------------------------------------------
    // Fakes. Every awaited call below returns an already-completed Task so that
    // PosViewModel's fire-and-forget InitializeAsync() (kicked off from the constructor)
    // runs to completion synchronously before the constructor returns — per normal C#
    // async semantics, awaiting an already-completed Task never yields. That makes the
    // constructed view model's state (CurrentShiftId, etc.) deterministic for the test
    // below without any Task.Delay/polling.
    // ---------------------------------------------------------------------------------

    private class FakeSellerSession : ISellerSession
    {
        public SellerInfo? Current { get; private set; }

        /// <summary>Mirrors the elapsed-idle-timeout half of the real
        /// SellerSession.IsStale rule (i.e. "the clock says re-confirm"). This fake has no
        /// real clock, so tests set it directly instead of waiting out a timeout. Touch()
        /// clears it, matching Touch() resetting _lastActivity in the real implementation —
        /// but, like production, that alone can never make IsStale false while no seller is
        /// selected; see IsStale below.</summary>
        public bool TimedOut { get; set; }

        // Mirrors ISellerSession.IsStale's actual rule (SellerSession.cs: "Current == null
        // || _clock() - _lastActivity > _idleTimeout") instead of being an independent
        // settable flag. That distinction matters: Touch() alone can never clear staleness
        // while nobody is selected — only a successful switch (SetCurrent below) can. A
        // fake that let a test claim "not stale" with Current == null would assert a state
        // production can never reach.
        public bool IsStale => Current == null || TimedOut;

        public IReadOnlyList<SellerInfo> Roster { get; private set; } = Array.Empty<SellerInfo>();
        public event EventHandler? CurrentChanged;
        public int TouchCount { get; private set; }

        public Task LoadRosterAsync(IEnumerable<SellerInfo> sellers)
        {
            Roster = sellers.ToList();
            return Task.CompletedTask;
        }

        public Task<SwitchResult> SwitchAsync(string sellerId, string pin) => Task.FromResult(SwitchResult.Ok);

        public Task<ApprovalResult> ApproveAsync(string sellerId, string pin)
            => Task.FromResult(ApprovalResult.Failure(SwitchResult.UnknownSeller));

        public void Touch()
        {
            TouchCount++;
            TimedOut = false;
        }

        public void Clear()
        {
            if (Current == null) return;
            Current = null;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Test hook standing in for a successful SwitchAsync — sets Current and
        /// raises CurrentChanged exactly like the real SellerSession does.</summary>
        public void SetCurrent(SellerInfo? seller)
        {
            Current = seller;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private class FakeCartService : ICartService
    {
        private readonly List<CartItem> _items = new();
        public IReadOnlyList<CartItem> Items => _items;
        public decimal Subtotal => _items.Sum(i => i.Product.Price * i.Quantity);
        public decimal TotalDiscount => 0m;
        public decimal TotalAmount => Subtotal;
        public IReadOnlyList<Coupon> AppliedCoupons => Array.Empty<Coupon>();
        public decimal ManualDiscountPercent { get; private set; }
        public decimal ManualDiscountAmount { get; private set; }
        public int SetManualDiscountCallCount { get; private set; }
        public decimal CustomerDiscountPercent => 0m;
        public QuoteResult? Quote => null;
        public string? QuoteId => null;
        public void ApplyQuote(QuoteResult result) { }
        public void ClearQuote() { }

        public void AddProduct(Product product)
        {
            var existing = _items.FirstOrDefault(i => i.Product.Id == product.Id);
            if (existing != null) existing.Quantity++;
            else _items.Add(new CartItem { Product = product, Quantity = 1 });
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RemoveItem(CartItem item)
        {
            _items.Remove(item);
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void IncreaseQuantity(CartItem item)
        {
            item.Quantity++;
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DecreaseQuantity(CartItem item)
        {
            item.Quantity--;
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetQuantity(CartItem item, decimal quantity)
        {
            item.Quantity = quantity;
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetQuantityInUnit(CartItem item, decimal amountInUnit) { }

        public PromotionOutcome? OfflinePromotion => null;
        public string? AppliedDiscountName => null;
        public MoneyPolicy MoneyPolicy => MoneyPolicy.Default;

        public void ClearCart()
        {
            _items.Clear();
            // Mirrors the real CartService.ClearCart(), which also resets the manual
            // discount — relevant now that Task 21's tests check ManualDiscountPercent
            // across a clear/re-add cycle.
            ManualDiscountPercent = 0;
            ManualDiscountAmount = 0;
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyCoupon(Coupon coupon) { }
        public void RemoveCoupon(string code) { }
        public void SetManualDiscount(decimal percent, decimal amount)
        {
            SetManualDiscountCallCount++;
            ManualDiscountPercent = percent;
            ManualDiscountAmount = amount;
        }
        public void ClearManualDiscount()
        {
            ManualDiscountPercent = 0;
            ManualDiscountAmount = 0;
        }
        public void SetCustomerDiscount(decimal percent) { }
        public void ClearCustomerDiscount() { }

        public void LoadSnapshot(
            IEnumerable<CartItem> items,
            decimal manualDiscountPercent, decimal manualDiscountAmount,
            decimal customerDiscountPercent,
            IEnumerable<Coupon> coupons)
        {
            _items.Clear();
            _items.AddRange(items);
            // Mirrors the real CartService.LoadSnapshot, which does set the discount
            // fields directly (bypassing SetManualDiscount) — that bypass is exactly what
            // let the park/resume approved_by bug through, so this fake must reproduce it
            // rather than silently keep the old discount around from before the resume.
            ManualDiscountPercent = manualDiscountPercent;
            ManualDiscountAmount = manualDiscountAmount;
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? CartChanged;
    }

    private class FakeProductService : IProductService
    {
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult(Enumerable.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string category) => Task.FromResult(Enumerable.Empty<Product>());
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => Task.FromResult(Enumerable.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task<IEnumerable<string>> GetCategoriesAsync() => Task.FromResult(Enumerable.Empty<string>());
    }

    private class FakeCategoryService : ICategoryService
    {
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult(Enumerable.Empty<Category>());
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult(Enumerable.Empty<Category>());
    }

    private class FakePrinterService : IPrinterService
    {
        public PrinterStatus Status => PrinterStatus.Ready;
        public event EventHandler<PrinterStatus>? StatusChanged;
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null) => Task.FromResult(true);
        public Task<bool> PrintPreReceiptAsync(IEnumerable<CartItem> items, decimal total) => Task.FromResult(true);
        public Task<bool> OpenCashDrawerAsync() => Task.FromResult(true);
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> lines, decimal totalRefund, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
        public Task<bool> PrintExchangeReceiptAsync(IEnumerable<ReturnReceiptLine> returned, IEnumerable<ReturnReceiptLine> issued, decimal difference, string documentNumber, string? warehouseName = null, string? sellerName = null, string? saleDate = null) => Task.FromResult(true);
    }

    private class FakeCustomerDisplayService : ICustomerDisplayService
    {
        public Task ShowLineAsync(string line1, string line2) => Task.CompletedTask;
        public Task ShowItemAsync(string name, decimal price) => Task.CompletedTask;
        public Task ShowTotalAsync(decimal total) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

    private class FakeShiftService : IShiftService
    {
        /// <summary>What CloseShiftAsync reports back — defaults to success (matching
        /// prior behaviour); tests exercising the failed-close path (Task 18) flip it.</summary>
        public bool CloseShiftResult { get; set; } = true;
        public int CloseShiftCallCount { get; private set; }

        /// <summary>What OpenShiftAsync/GetShiftStateAsync report back — default "shift-1"
        /// matches prior behaviour (an already-open shift restored on startup). The escape
        /// hatch tests below flip these to null to simulate a rejected or unreachable
        /// session, and separately call RaiseSessionRevoked to simulate the real
        /// ShiftService's 401-only distinction — null alone (no event) stands in for a
        /// network failure, since the real service also returns null without raising
        /// SessionRevoked when the request never reached the server.</summary>
        public string? OpenShiftResult { get; set; } = "shift-1";
        public string? GetShiftStateResult { get; set; } = "shift-1";

        public Task<string?> OpenShiftAsync() => Task.FromResult(OpenShiftResult);
        public Task<bool> CloseShiftAsync(string shiftId)
        {
            CloseShiftCallCount++;
            return Task.FromResult(CloseShiftResult);
        }
        public Task<string?> GetShiftStateAsync() => Task.FromResult(GetShiftStateResult);

        public event EventHandler? SessionRevoked;

        /// <summary>Test hook standing in for the real ShiftService hitting a 401 on
        /// GetShiftStateAsync/OpenShiftAsync — raises the real event so PosViewModel's own
        /// OnShiftSessionRevoked subscription is what's under test, not this fake's
        /// plumbing (mirrors FakeExpenseDocumentService.RaiseSessionRevoked above).</summary>
        public void RaiseSessionRevoked() => SessionRevoked?.Invoke(this, EventArgs.Empty);
    }

    private class FakeOfflineStorageService : IOfflineStorageService
    {
        public Task SaveProductsAsync(IEnumerable<Product> products) => Task.CompletedTask;
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult(Enumerable.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult(Enumerable.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult(Enumerable.Empty<Category>());
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult(Enumerable.Empty<Category>());
        public Task SavePromotionsAsync(IEnumerable<Promotion> promotions) => Task.CompletedTask;
        public Task<IEnumerable<Promotion>> GetPromotionsAsync() => Task.FromResult(Enumerable.Empty<Promotion>());
        public Task ClearPromotionsAsync() => Task.CompletedTask;
        public Task SaveMoneyPolicyAsync(MoneyPolicy policy) => Task.CompletedTask;
        public Task<MoneyPolicy> GetMoneyPolicyAsync() => Task.FromResult(MoneyPolicy.Default);
        public Task SaveCashFeaturesAsync(CashFeatures features) => Task.CompletedTask;
        public Task<CashFeatures> GetCashFeaturesAsync() => Task.FromResult(CashFeatures.Default);
        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => Task.FromResult(Enumerable.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task MarkDocumentRejectedAsync(string hash, string reason) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
        public Task ClearUnsyncedDocumentsAsync() => Task.CompletedTask;
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult(Enumerable.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers) => Task.CompletedTask;
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult(Enumerable.Empty<SellerInfo>());
        public Task InitializeAsync() => Task.CompletedTask;
    }

    private class FakeSyncService : ISyncService
    {
        public event EventHandler<bool>? SyncStatusChanged;
        public event EventHandler? ProductsSynced;
        public Task SyncProductsAsync() => Task.CompletedTask;
        public Task FullReinitializeAsync() => Task.CompletedTask;
        public Task<bool> CheckSystemOnlineAsync() => Task.FromResult(true);

        // Lets a test flip PosViewModel's IsSystemOnline the same way the real
        // background ping does, without waiting on an actual timer.
        public void RaiseSyncStatusChanged(bool isOnline) => SyncStatusChanged?.Invoke(this, isOnline);
    }

    /// <summary>Stands in for the real AuthService: records whether/how many times
    /// ClearSession was called, without touching any settings storage of its own — Part
    /// 0b moved that wiping behind this interface specifically so PosViewModel no longer
    /// needs to know AuthToken/AuthTokenExpiresAt exist at all.</summary>
    private class FakeAuthService : IAuthService
    {
        public int ClearSessionCallCount { get; private set; }
        public Task<bool> LoginAsync(string email, string password, bool rememberMe) => Task.FromResult(true);
        public void ClearSession() => ClearSessionCallCount++;
    }

    private class FakeSettingsService : ISettingsService
    {
        public string BackendUrl { get; set; } = string.Empty;
        public string CashRegisterToken { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; }
        public bool ReturnPrintReceipt { get; set; }
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged;
        public void Save() { }
    }

    private class FakeExpenseDocumentService : IExpenseDocumentService
    {
        public DocumentRequest? LastRequest { get; private set; }

        /// <summary>What CreateExpenseDocumentAsync reports back — defaults to success
        /// (matching prior behaviour). The end-of-receipt tests flip it to false to
        /// exercise the failed-payment branch, where the seller must survive so a retry
        /// doesn't demand a fresh PIN.</summary>
        public bool CreateResult { get; set; } = true;

        public Task<bool> CreateExpenseDocumentAsync(DocumentRequest request)
        {
            LastRequest = request;
            return Task.FromResult(CreateResult);
        }

        public Task<ExpenseDocumentOutcome> CreateExpenseDocumentDetailedAsync(DocumentRequest request)
        {
            LastRequest = request;
            return Task.FromResult(ExpenseDocumentOutcome.Sent("1"));
        }

        public Task SyncOfflineDocumentsAsync() => Task.CompletedTask;
        public Task<int> GetUnsyncedDocumentsCountAsync() => Task.FromResult(0);
        public event EventHandler<int>? UnsyncedDocumentsCountChanged;
        public event EventHandler? SessionRevoked;

        /// <summary>Test hook standing in for SyncOfflineDocumentsAsync hitting a 401 —
        /// raises the real event so PosViewModel's own OnSessionRevoked subscription is
        /// what's under test, not this fake's plumbing.</summary>
        public void RaiseSessionRevoked() => SessionRevoked?.Invoke(this, EventArgs.Empty);
    }

    private class FakeCounterpartyService : ICounterpartyService
    {
        public Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request) => Task.FromResult<CounterpartyResponse?>(null);
        public Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query) => Task.FromResult<List<CounterpartyResponse>?>(new List<CounterpartyResponse>());
        public Task<string?> GetSystemCounterpartyIdAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>Mirrors the real ParkedSaleService's own park/resume round trip (park
    /// stores under a fresh id, resume looks it up and removes it, an unknown id resumes
    /// to null) closely enough that
    /// ResumeParkedSale_ApprovedOverCapDiscount_SurvivesParkThenResume below is a genuine
    /// round trip through this fake's BuildSnapshot -> ParkAsync -> ResumeAsync path,
    /// rather than a canned snapshot handed straight to ResumeAsync.</summary>
    private class FakeParkedSaleService : IParkedSaleService
    {
        private readonly Dictionary<string, ParkedSaleSnapshot> _parked = new();
        public ParkedSaleSnapshot? LastParkedSnapshot { get; private set; }
        public string? LastParkedId { get; private set; }

        /// <summary>What GetCountAsync reports back — defaults to 0 (matching prior
        /// behaviour); the feature-flag tests below set it directly to simulate sales
        /// already parked on the register before the flag was switched off.</summary>
        public int Count { get; set; }

        public Task<ParkedSale> ParkAsync(ParkedSaleSnapshot snapshot, decimal total)
        {
            var id = Guid.NewGuid().ToString();
            _parked[id] = snapshot;
            LastParkedSnapshot = snapshot;
            LastParkedId = id;
            return Task.FromResult(new ParkedSale { Id = id, Total = total });
        }

        public Task<IReadOnlyList<ParkedSale>> GetAllAsync() => Task.FromResult<IReadOnlyList<ParkedSale>>(Array.Empty<ParkedSale>());

        public Task<ParkedSaleSnapshot?> ResumeAsync(string id)
        {
            if (_parked.TryGetValue(id, out var snapshot))
            {
                _parked.Remove(id);
                return Task.FromResult<ParkedSaleSnapshot?>(snapshot);
            }
            return Task.FromResult<ParkedSaleSnapshot?>(null);
        }

        /// <summary>Test hook standing in for a parked sale saved by an older build:
        /// stores a snapshot directly under a given id, bypassing ParkAsync (which always
        /// stamps the current ApprovedById) so the snapshot can omit it entirely, exactly
        /// as System.Text.Json would deserialize a payload that predates the field.</summary>
        public void SeedParkedSnapshot(string id, ParkedSaleSnapshot snapshot) => _parked[id] = snapshot;

        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task<int> GetCountAsync() => Task.FromResult(Count);
        public event EventHandler<int>? CountChanged;
    }

    /// <summary>Stands in for CashFeatureService: no storage, flags set directly.</summary>
    private class FakeCashFeatureService : ICashFeatureService
    {
        public CashFeatures Current { get; } = CashFeatures.Default;

        public void Set(string code, bool enabled) => Current.Flags[code] = enabled;

        /// <summary>When set, RefreshAsync awaits this before returning instead of
        /// completing immediately — reproducing the real gap between the constructor's
        /// ApplyFeatures pass (reading whatever Current already holds, the all-enabled
        /// default on a register that has never synced) and InitializeAsync's own
        /// RefreshAsync/ApplyFeatures pass, which in production only resolves once the
        /// register's local storage is actually ready. Every other fake in this class
        /// completes synchronously by design (see the class remarks), so this is the one
        /// deliberate exception, and only when a test opts in by setting it.</summary>
        public TaskCompletionSource<bool>? PendingRefresh;

        private readonly Dictionary<string, bool> _afterRefresh = new();

        /// <summary>Queues a flag value that only takes effect when RefreshAsync actually
        /// runs — immediately if PendingRefresh is null, or once a test completes
        /// PendingRefresh otherwise — never at the moment this is called. That mirrors
        /// the real CashFeatureService: the map only ever changes as a result of a
        /// refresh actually completing.</summary>
        public void SetAfterRefresh(string code, bool enabled) => _afterRefresh[code] = enabled;

        public async Task RefreshAsync()
        {
            if (PendingRefresh != null) await PendingRefresh.Task;
            foreach (var (code, enabled) in _afterRefresh) Current.Flags[code] = enabled;
        }
    }

    // GetSalesAsync/GetReturnableLinesAsync/CreateReturnAsync are never reached by the
    // scenarios below (no test opens the Returns screen), so they throw loudly rather than
    // silently returning fabricated data that would never be checked.
    private class FakeReturnService : IReturnService
    {
        public Task<ExpenseListResponse> GetSalesAsync(int page = 1, string? documentNumber = null) => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId) => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request) => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
    }

    // No scenario below opens the exchange screen — the till payout throws loudly
    // rather than silently returning fabricated data that would never be checked.
    private class FakeCashOperationService : ICashOperationService
    {
        public Task<CashOpOutcome> CreateCashExpenseAsync(CashExpenseRequest request)
            => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
    }

    private class FakeQuoteService : IQuoteService
    {
        public List<QuoteRequest> Requests { get; } = new();
        public int CallCount => Requests.Count;

        /// <summary>What QuoteAsync hands back. Null (the default) is the real
        /// service's offline/failure answer, which sends the cart down local pricing.</summary>
        public QuoteResult? Result { get; set; }

        public Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private class FakeSessionContext : ISessionContext
    {
        public string? WarehouseId { get; set; }
        public string? CashId { get; set; }
    }

    private class FakePromotionProvider : IPromotionProvider
    {
        public IReadOnlyList<Promotion> Promotions { get; set; } = Array.Empty<Promotion>();
        public MoneyPolicy MoneyPolicy { get; set; } = MoneyPolicy.Default;
        public Task RefreshAsync() => Task.CompletedTask;
    }

    /// <summary>Stands in for the real SellerRosterService: RefreshAsync never throws
    /// (per its documented contract) and this fake just hands back whatever roster the
    /// test configured, defaulting to empty — an empty roster is a legitimate state,
    /// not an error, per Task 17's spec.</summary>
    private class FakeSellerRosterService : ISellerRosterService
    {
        public List<SellerInfo> Roster { get; set; } = new();
        public int RefreshCallCount { get; private set; }

        public Task<IEnumerable<SellerInfo>> RefreshAsync()
        {
            RefreshCallCount++;
            return Task.FromResult<IEnumerable<SellerInfo>>(Roster);
        }

        public Task<IEnumerable<SellerInfo>> GetCachedAsync() => Task.FromResult<IEnumerable<SellerInfo>>(Roster);

        public Task<bool> SetPinAsync(string sellerId, string pin) => Task.FromResult(true);
    }

    private sealed class Deps
    {
        public FakeSellerSession SellerSession { get; } = new();
        public FakeExpenseDocumentService ExpenseDocumentService { get; } = new();
        public FakeSellerRosterService RosterService { get; } = new();
        public FakeSettingsService SettingsService { get; } = new();
        public FakeShiftService ShiftService { get; } = new();
        public FakeAuthService AuthService { get; } = new();
        public FakeCartService CartService { get; } = new();
        public FakeParkedSaleService ParkedSaleService { get; } = new();
        public FakeCashFeatureService Features { get; } = new();
        public FakeSyncService SyncService { get; } = new();
        public FakeQuoteService QuoteService { get; } = new();
        public HttpClient HttpClient { get; } = new();
    }

    private static PosViewModel CreateViewModel(out Deps deps, Action<Deps>? configure = null)
    {
        deps = new Deps();
        configure?.Invoke(deps);
        return new PosViewModel(
            new FakeProductService(),
            new FakeCategoryService(),
            deps.CartService,
            new FakePrinterService(),
            new FakeCustomerDisplayService(),
            deps.ShiftService,
            new FakeOfflineStorageService(),
            deps.SyncService,
            deps.SettingsService,
            deps.ExpenseDocumentService,
            new FakeCounterpartyService(),
            deps.ParkedSaleService,
            new FakeReturnService(),
            new FakeCashOperationService(),
            deps.QuoteService,
            new FakePromotionProvider(),
            new FakeSessionContext(),
            deps.HttpClient,
            deps.SellerSession,
            deps.RosterService,
            deps.AuthService,
            deps.Features,
            new UpdateViewModel(
                new NoUpdateService(),
                new NoInstallerLauncher(),
                deps.CartService,
                new FixedVersionProvider()));
    }

    private sealed class NoUpdateService : VvCash.Services.Update.IUpdateService
    {
        public Task<VvCash.Services.Update.UpdateInfo?> CheckAsync(CancellationToken ct)
            => Task.FromResult<VvCash.Services.Update.UpdateInfo?>(null);

        public Task<string?> DownloadAsync(
            VvCash.Services.Update.UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class NoInstallerLauncher : VvCash.Services.Update.IInstallerLauncher
    {
        public void Launch(string installerPath) { }
    }

    private sealed class FixedVersionProvider : VvCash.Services.Update.IAppVersionProvider
    {
        public Version Current { get; } = new Version(1, 0, 0);
    }

    private static Product MakeProduct(string id, decimal price) => new()
    {
        Id = id,
        Name = $"Product {id}",
        Sku = id,
        Price = price
    };

    // ---------------------------------------------------------------------------------
    // The gate
    // ---------------------------------------------------------------------------------

    // The register asks who is selling in exactly one place: Pay. AddToCart and
    // ResumeParkedSale used to ask too, and the tests below are what is left of that.
    //
    // Asking while the receipt was being built was wrong twice over. It put the overlay
    // over the screen at the busiest moment at the till, on the first product of every
    // receipt; and it could not actually block the add, so the product landed whether or
    // not anyone answered — which is how a dismissed ask left a cart with items, nobody
    // confirmed, and (once the empty-cart guard read that cart as "receipt in progress")
    // no way back to the question. Moving the ask to Pay removes the interruption and the
    // dead end together: nothing is attributed until money is taken, and that is the point
    // where refusing is still free.

    [Fact]
    public void AddToCart_NeverAsksWhoIsSelling_WhateverTheSessionState()
    {
        // All three states that used to raise the overlay from here. None may now.
        using var vm = CreateViewModel(out var deps);
        var raised = 0;
        vm.SellerSwitchRequested += (s, e) => raised++;

        // Nobody confirmed at all.
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        // Somebody confirmed, but the idle timeout lapsed.
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        deps.SellerSession.TimedOut = true;
        vm.AddToCartCommand.Execute(MakeProduct("p2", 5m));

        // Somebody confirmed and the session is live.
        deps.SellerSession.Touch();
        vm.AddToCartCommand.Execute(MakeProduct("p3", 7m));

        Assert.Equal(0, raised);
        Assert.Equal(3, deps.CartService.Items.Count); // and every product still landed
    }

    
    
    
    
    
    
    [Fact]
    public void AddToCart_AlwaysTouchesSessionOnGenuineActivity()
    {
        using var vm = CreateViewModel(out var deps);

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 5m));

        Assert.Equal(2, deps.SellerSession.TouchCount);
    }

    [Fact]
    public void OpenSellerSwitch_AlwaysRaisesRegardlessOfCartOrStaleness()
    {
        // Tapping the header chip is an explicit request, not the implicit start-of-receipt
        // gate, so it must open the overlay even mid-receipt / while not stale. "Not stale"
        // requires an actually-confirmed seller (see AddToCart_EmptyCartButSessionNotStale_
        // DoesNotRaise above), so simulate that first rather than just claiming it.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "s0", FirstName = "Prior", LastName = "Seller" });
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        Assert.False(deps.SellerSession.IsStale);
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.OpenSellerSwitchCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void OpenSellerSwitch_WithEmptyCart_GrantsSignOut()
    {
        // The manual chip tap is the one raise site allowed to offer sign-out at all —
        // and only because, unlike AddToCart/ResumeParkedSale, nothing is about to fill
        // the cart right after this: tapping the chip does not add anything. Grants
        // permission by reading PosViewModel.CanEndSellerSession at the moment of the
        // tap, true here since the cart is genuinely (and durably) empty.
        using var vm = CreateViewModel(out var deps);
        SellerSwitchRequest? request = null;
        vm.SellerSwitchRequested += (s, e) => request = e;

        vm.OpenSellerSwitchCommand.Execute(null);

        Assert.NotNull(request);
        Assert.True(request!.CanSignOut);
    }

    [Fact]
    public void OpenSellerSwitch_MidReceipt_StillGrantsSignOut()
    {
        // This test used to assert the opposite. Withdrawing sign-out mid-receipt rested
        // on one stated premise: "AddToCart's gate only re-asks on an EMPTY cart, so
        // dropping the seller with items still in the cart would leave the rest of that
        // receipt with nobody confirmed and nothing to re-prompt."
        //
        // That premise no longer holds. The gate re-asks on every add while nobody is
        // confirmed, cart empty or not, and Pay() refuses outright without a seller — so
        // a receipt whose seller was dropped mid-way re-prompts on the next item and
        // cannot be paid unattributed either way. With the premise gone the restriction
        // only cost the cashier the one control they need: the cart is almost never empty
        // at the moment somebody wants to stop selling.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "s0", FirstName = "Prior", LastName = "Seller" });
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        Assert.NotEmpty(deps.CartService.Items); // the premise this used to turn on
        SellerSwitchRequest? request = null;
        vm.SellerSwitchRequested += (s, e) => request = e;

        vm.OpenSellerSwitchCommand.Execute(null);

        Assert.NotNull(request);
        Assert.True(request!.CanSignOut);
    }

    [Fact]
    public void SignOutMidReceipt_PayStillRefuses()
    {
        // What makes granting sign-out mid-receipt safe rather than a hole: the receipt
        // left behind cannot quietly finish under nobody's name. Pay refuses and asks, and
        // that refusal is the whole safety net now that nothing upstream asks at all.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s0"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        deps.SellerSession.Clear(); // what SellerSwitchViewModel.SignOutSeller does
        vm.AddToCartCommand.Execute(MakeProduct("p2", 5m));

        var navigated = 0;
        vm.NavigationRequest = _ => navigated++;
        var raised = 0;
        vm.SellerSwitchRequested += (s, e) => raised++;

        vm.PayCommand.Execute(null);

        Assert.Equal(0, navigated);
        Assert.Null(deps.ExpenseDocumentService.LastRequest);
        Assert.Equal(1, raised);
    }

    // ---------------------------------------------------------------------------------
    // The seller chip
    // ---------------------------------------------------------------------------------

    [Fact]
    public void SellerChipText_UpdatesWhenCurrentSellerChanges()
    {
        using var vm = CreateViewModel(out var deps);
        var before = vm.SellerChipText;

        deps.SellerSession.SetCurrent(new SellerInfo { Id = "s1", FirstName = "Anna", LastName = "Lee" });

        Assert.Equal("Anna Lee", vm.SellerChipText);
        Assert.NotEqual(before, vm.SellerChipText);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSellerSessionCurrentChanged()
    {
        var vm = CreateViewModel(out var deps);
        vm.Dispose();

        var raised = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PosViewModel.SellerChipText)) raised = true;
        };

        deps.SellerSession.SetCurrent(new SellerInfo { Id = "s1", FirstName = "Anna", LastName = "Lee" });

        Assert.False(raised);
    }

    // ---------------------------------------------------------------------------------
    // Pay() carries the seller
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Pay_WithCurrentSeller_StampsSellerIdOntoRequest()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "seller-42", FirstName = "Anna", LastName = "Lee" });
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.NotNull(deps.ExpenseDocumentService.LastRequest);
        Assert.Equal("seller-42", deps.ExpenseDocumentService.LastRequest!.SellerId);
    }

    // ---------------------------------------------------------------------------------
    // Pay() carries the customer
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Pay_WithSelectedCustomer_StampsCounterpartyOntoRequest()
    {
        // SelectedCustomer already drives the discount shown on this receipt
        // (SelectedCustomerDiscount) — the sale itself must be attributed to the
        // same customer, not just discounted on their behalf.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "seller-1", FirstName = "Anna", LastName = "Lee" });
        vm.SelectedCustomer = new CounterpartyResponse { Id = "cust-1", FirstName = "Bob" };
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.NotNull(deps.ExpenseDocumentService.LastRequest);
        Assert.Equal("cust-1", deps.ExpenseDocumentService.LastRequest!.Counterparty);
    }

    [Fact]
    public void Pay_NoSelectedCustomer_OmitsCounterpartyFromRequest()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "seller-1", FirstName = "Anna", LastName = "Lee" });
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.NotNull(deps.ExpenseDocumentService.LastRequest);
        Assert.Null(deps.ExpenseDocumentService.LastRequest!.Counterparty);
    }

    [Fact]
    public void Pay_WithSelectedCustomer_PaymentScreenAllowsSellingOnCredit()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "seller-1", FirstName = "Anna", LastName = "Lee" });
        vm.SelectedCustomer = new CounterpartyResponse { Id = "cust-1", FirstName = "Bob" };
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        Assert.True(mixedPaymentVm!.HasCustomer);
    }

    [Fact]
    public void Pay_NoSelectedCustomer_PaymentScreenRefusesSellingOnCredit()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "seller-1", FirstName = "Anna", LastName = "Lee" });
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        Assert.False(mixedPaymentVm!.HasCustomer);
    }

    [Fact]
    public void Pay_SellerSwitchDisabled_NoCurrentSeller_OmitsSellerIdFromRequestAndJson()
    {
        // The seller-switch-off register: nobody ever becomes Current here, so Pay()'s
        // no-seller gate is deliberately not armed (see the gate's own remarks) and this
        // is the one configuration where paying with a null SellerId is still correct —
        // the backend falls back to crediting the shift owner. On a register that HAS
        // seller switching, the same state now refuses instead; see
        // Pay_SellerSwitchEnabled_NoCurrentSeller_RefusesAndAsksWhoIsSelling below.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.SellerSwitch, false));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        var request = deps.ExpenseDocumentService.LastRequest;
        Assert.NotNull(request);
        Assert.Null(request!.SellerId);

        // The property carries [JsonIgnore(Condition = WhenWritingNull)], so a null
        // SellerId must vanish from the wire payload rather than serialize as "seller":null
        // — the backend's documented behaviour for a genuinely absent field, not an empty one.
        var json = System.Text.Json.JsonSerializer.Serialize(request);
        Assert.DoesNotContain("seller", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pay_SellerSwitchEnabled_NoCurrentSeller_RefusesAndAsksWhoIsSelling()
    {
        // The one gate the register has. Nothing upstream asks any more, so a whole
        // receipt can be rung up with nobody confirmed — this is what must catch it, and
        // it is the last point where catching it is still free.
        using var vm = CreateViewModel(out var deps);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        Assert.Null(deps.SellerSession.Current);

        var navigatedCount = 0;
        vm.NavigationRequest = _ => navigatedCount++;
        var raisedCount = 0;
        SellerSwitchRequest? request = null;
        vm.SellerSwitchRequested += (s, e) => { raisedCount++; request = e; };

        vm.PayCommand.Execute(null);

        Assert.Equal(0, navigatedCount);
        Assert.Null(deps.ExpenseDocumentService.LastRequest);
        Assert.Equal(1, raisedCount);
        // The cart is non-empty by the time this fires (Pay returns early on an empty
        // one), so there is nothing to sign out of — see SellerSwitchRequest's remarks.
        Assert.NotNull(request);
        Assert.False(request!.CanSignOut);
    }

    [Fact]
    public async Task Pay_TheAnswerToTheGateResumesThePaymentByItself()
    {
        // What makes the gate a pause rather than a rejected press. The cashier presses
        // Pay, answers "who is selling?", and the payment screen opens off the back of
        // that answer — no second press. Without this the refusal is indistinguishable
        // from a dead button: nothing on screen says the same control now needs pressing
        // again.
        using var vm = CreateViewModel(out var deps);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };
        SellerSwitchRequest? request = null;
        vm.SellerSwitchRequested += (s, e) => request = e;

        vm.PayCommand.Execute(null);
        Assert.Null(mixedPaymentVm); // refused, as it must be
        Assert.NotNull(request);
        Assert.NotNull(request!.OnSwitched);

        // Exactly what SellerSwitchViewModel does on a successful PIN: set the seller,
        // then run the continuation the caller handed over.
        var seller = MakeSeller("seller-7");
        deps.SellerSession.SetCurrent(seller);
        await request.OnSwitched!(seller);

        Assert.NotNull(mixedPaymentVm);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.NotNull(deps.ExpenseDocumentService.LastRequest);
        Assert.Equal("seller-7", deps.ExpenseDocumentService.LastRequest!.SellerId);
    }

    [Fact]
    public void Pay_SellerConfirmedButSessionWentStale_RefusesAndAsksAgain()
    {
        // Being the only gate left, this one carries the idle timeout too. Somebody
        // confirmed, then the register sat idle long enough to lapse — the next person
        // must not have their receipt signed by whoever walked away. Current is non-null
        // here, so a gate that only checked "is anybody confirmed" would wave this
        // through, which is the misattribution the timeout exists for.
        //
        // Touch() runs on every add, so an actively rung-up receipt never reaches this.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("seller-7"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        deps.SellerSession.TimedOut = true;
        Assert.True(deps.SellerSession.IsStale);

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };
        var raised = 0;
        vm.SellerSwitchRequested += (s, e) => raised++;

        vm.PayCommand.Execute(null);

        Assert.Null(mixedPaymentVm);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Pay_ActivelyRungUpReceipt_NeverAsksAtTheTill()
    {
        // The other side of gating on IsStale: adding items keeps the session alive, so a
        // receipt being rung up normally reaches Pay with nothing to ask. If this ever
        // fails, every sale would prompt for a PIN at the moment money changes hands.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("seller-7"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 20m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };
        var raised = 0;
        vm.SellerSwitchRequested += (s, e) => raised++;

        vm.PayCommand.Execute(null);

        Assert.Equal(0, raised);
        Assert.NotNull(mixedPaymentVm);
    }

    [Fact]
    public void DocumentRequest_SerializesSellerIdUnderTheKeyTheBackendReads()
    {
        // documents/serializers.go (DocumentExpenseSerializer, feat/seller-pin branch of
        // cloudmarket-server) declares `SellerID string `json:"seller"``. Getting this
        // JSON name wrong means the backend silently drops the field and every sale is
        // credited to the shift owner instead of the ringing cashier.
        var request = new DocumentRequest { SellerId = "seller-42" };

        var json = System.Text.Json.JsonSerializer.Serialize(request);

        Assert.Contains("\"seller\":\"seller-42\"", json);
        Assert.DoesNotContain("seller_id", json);
    }

    // ---------------------------------------------------------------------------------
    // Roster loading (Task 17): a shift starting or being restored must actually fetch
    // and hand off the roster, or everything built in Task 16 stays inert (nobody to
    // switch to). FakeSellerSession.LoadRosterAsync has no UI-thread assertion, so these
    // tests exercise the wiring (was RefreshAsync called, did the result reach the
    // session) rather than the threading requirement itself, which is not observable
    // through this fake — see the real SellerSession's own AssertUiThread instead.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Construction_RestoresOpenShift_LoadsRosterOntoSession()
    {
        // FakeShiftService.GetShiftStateAsync always returns a non-null shift id, so
        // every construction below restores an "already open" shift on startup —
        // exactly the scenario Task 17 requires: a register that restarts mid-shift
        // must still get its roster before the cashier can ring up a first receipt.
        //
        // Asserting ">= 1" rather than "== 1": InitializeAsync's own restore branch
        // runs synchronously to completion before the constructor returns (see the
        // class-level comment on why the fakes make that true), so it alone guarantees
        // at least one call deterministically. StartBackgroundSync's loop (kicked off
        // moments earlier in the same method, via Task.Run) starts lastSyncTime at
        // DateTime.MinValue, so its very first iteration *also* fires an immediate
        // roster refresh on a background thread — the same pre-existing "sync
        // immediately on startup" pattern this task extended from products to the
        // roster. That second call lands on its own schedule, so whether it has already
        // completed by the time this assertion runs is a race in how many times this
        // *fake* records a call — the fake has no coalescing, unlike the real
        // SellerRosterService (see SellerRosterServiceTest's
        // RefreshAsync_TwoOverlappingCallers_CoalesceIntoOneFetch_BothGetSameRoster),
        // so in production these two call sites overlapping is not a bug: they'd share
        // one in-flight fetch and get the identical result. Here it only means the
        // lower bound is what's deterministic.
        using var vm = CreateViewModel(out var deps);

        Assert.True(deps.RosterService.RefreshCallCount >= 1,
            $"expected at least one roster refresh from the startup restore path, got {deps.RosterService.RefreshCallCount}");
    }

    [Fact]
    public void OpenShiftCommand_OnSuccess_LoadsFreshRosterOntoSession()
    {
        using var vm = CreateViewModel(out var deps);
        var callsBeforeOpen = deps.RosterService.RefreshCallCount;
        deps.RosterService.Roster = new List<SellerInfo>
        {
            new SellerInfo { Id = "s9", FirstName = "Nina", CanSell = true }
        };

        vm.OpenShiftCommand.Execute(null);

        // ">" rather than "== +1": see Construction_RestoresOpenShift_LoadsRosterOntoSession
        // above for why the background sync loop's own first-iteration roster refresh can
        // (harmlessly) land anywhere around startup, including this narrow window. What
        // OpenShiftCommand's own synchronous, non-backgrounded call path guarantees
        // deterministically is at least one more refresh, and — the real proof this is
        // OpenShiftAsync's own call, not just the background loop's — that the roster the
        // session ends up holding is the one only OpenShiftCommand could have fetched
        // (populated with "s9" only after construction finished).
        Assert.True(deps.RosterService.RefreshCallCount > callsBeforeOpen);
        Assert.Single(deps.SellerSession.Roster, s => s.Id == "s9");
    }

    // ---------------------------------------------------------------------------------
    // Close-shift gate and token lifetime (Task 18): closing requires CanCloseShift,
    // escalates through the approval overlay when it's missing, actually finishes
    // closing once approved (not just dismissing the overlay), and only a
    // *successful* close wipes the stored auth token / clears the seller session.
    // ---------------------------------------------------------------------------------

    private static SellerInfo MakeSeller(string id, bool canCloseShift = false, bool canRefund = false, decimal maxDiscount = 0m) =>
        new() { Id = id, FirstName = "Seller", LastName = id, CanCloseShift = canCloseShift, CanRefund = canRefund, MaxDiscount = maxDiscount };

    [Fact]
    public void CloseShiftCommand_SellerLacksRight_RaisesApprovalRequest_DoesNotClose()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", canCloseShift: false));
        var raisedCount = 0;
        vm.CloseShiftApprovalRequested += (s, e) => raisedCount++;

        vm.CloseShiftCommand.Execute(null);

        Assert.Equal(1, raisedCount);
        Assert.True(vm.IsShiftOpen);
        Assert.Equal("shift-1", vm.CurrentShiftId);
        Assert.Equal(0, deps.ShiftService.CloseShiftCallCount);
    }

    [Fact]
    public void CloseShiftCommand_NoCurrentSeller_TreatedAsLackingRight_RaisesApprovalRequest()
    {
        // Nobody has confirmed on yet (Current == null) — fail closed, same as an
        // explicit CanCloseShift == false, rather than allowing an unattributed close.
        using var vm = CreateViewModel(out var deps);
        var raisedCount = 0;
        vm.CloseShiftApprovalRequested += (s, e) => raisedCount++;

        vm.CloseShiftCommand.Execute(null);

        Assert.Equal(1, raisedCount);
        Assert.True(vm.IsShiftOpen);
    }

    [Fact]
    public async Task CloseShiftCommand_SellerHasRight_ClosesDirectly_NoApprovalRaised()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", canCloseShift: true));
        var raisedCount = 0;
        vm.CloseShiftApprovalRequested += (s, e) => raisedCount++;

        await vm.CloseShiftCommand.ExecuteAsync(null);

        Assert.Equal(0, raisedCount);
        Assert.False(vm.IsShiftOpen);
        Assert.Null(vm.CurrentShiftId);
        Assert.Equal(1, deps.ShiftService.CloseShiftCallCount);
    }

    [Fact]
    public async Task OnCloseShiftApproved_AfterApprovalRequested_ActuallyClosesTheShift()
    {
        // The continuation: approving must not just dismiss the overlay, it must
        // finish the close PosViewModel originally asked for.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", canCloseShift: false));
        vm.CloseShiftCommand.Execute(null); // raises CloseShiftApprovalRequested

        await vm.OnCloseShiftApproved();

        Assert.False(vm.IsShiftOpen);
        Assert.Null(vm.CurrentShiftId);
        Assert.Equal(1, deps.ShiftService.CloseShiftCallCount);
    }

    // NOTE: this file used to have an OnCloseShiftApproved_WithoutAPriorRequest_IsNoOp
    // test here, guarding against a stray Approved event closing the shift with no prior
    // CloseShiftApprovalRequested. Part 0/Task 21 removed the boolean pending-flag that
    // guard depended on: SellerSwitchViewModel now owns a per-open-call continuation
    // instead of a shared Approved event (see its class remarks), so OnCloseShiftApproved
    // is only ever invoked as that continuation, when this specific approval succeeded —
    // there is no "stray" case left to guard against here. The equivalent invariant (a
    // cancelled or unrelated approval never runs an abandoned continuation) is now proven
    // at the SellerSwitchViewModel level — see
    // SellerSwitchViewModelTest.Cancel_DuringApprovalMode_DiscardsContinuation_LaterUnrelatedApprovalDoesNotRunIt.

    [Fact]
    public async Task CloseShift_OnSuccess_ClearsAuthSessionAndSellerSession()
    {
        // Part 0b: wiping AuthToken/AuthTokenExpiresAt moved behind IAuthService.ClearSession
        // (AuthService.LoginAsync's own fields — see FakeAuthService's remarks), so this only
        // checks that PosViewModel calls it, not that settings storage got mutated directly.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", canCloseShift: true));

        await vm.CloseShiftCommand.ExecuteAsync(null);

        Assert.Equal(1, deps.AuthService.ClearSessionCallCount);
        Assert.Null(deps.SellerSession.Current);
    }

    [Fact]
    public async Task CloseShift_WhenCloseShiftAsyncFails_LeavesAuthSessionAndSellerSessionUntouched()
    {
        using var vm = CreateViewModel(out var deps);
        deps.ShiftService.CloseShiftResult = false;
        var seller = MakeSeller("s1", canCloseShift: true);
        deps.SellerSession.SetCurrent(seller);

        await vm.CloseShiftCommand.ExecuteAsync(null);

        Assert.True(vm.IsShiftOpen); // the shift itself never actually closed
        Assert.Equal(0, deps.AuthService.ClearSessionCallCount);
        Assert.Same(seller, deps.SellerSession.Current);
    }

    [Fact]
    public void CloseShift_CancelledConfirmDialog_LeavesTokenAndSellerSessionUntouched()
    {
        // Stands in for the "parked sales exist" branch (ProceedToCloseShiftAsync
        // shows IsShiftCloseConfirmVisible instead of closing outright) without
        // depending on FakeParkedSaleService's CountChanged/Dispatcher plumbing,
        // which nothing else in this suite exercises yet — setting the same
        // publicly-settable property the real flow would have set is equivalent
        // for what this test actually checks: that CancelCloseShiftCommand alone
        // never reaches DoCloseShiftAsync.
        using var vm = CreateViewModel(out var deps);
        deps.SettingsService.AuthToken = "some-token";
        var seller = MakeSeller("s1", canCloseShift: true);
        deps.SellerSession.SetCurrent(seller);
        vm.IsShiftCloseConfirmVisible = true;

        vm.CancelCloseShiftCommand.Execute(null);

        Assert.False(vm.IsShiftCloseConfirmVisible);
        Assert.True(vm.IsShiftOpen);
        Assert.Equal("some-token", deps.SettingsService.AuthToken);
        Assert.Same(seller, deps.SellerSession.Current);
        Assert.Equal(0, deps.ShiftService.CloseShiftCallCount);
    }

    // ---------------------------------------------------------------------------------
    // Returns gate (Task 20): opening returns requires CanRefund, escalates through the
    // approval overlay when it's missing (same shape as the close-shift gate above), and
    // approving genuinely opens the returns dialog rather than just dismissing the
    // overlay.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void OpenReturnsCommand_SellerLacksRight_RaisesApprovalRequest()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", canRefund: false));
        var raisedCount = 0;
        vm.RefundApprovalRequested += (s, e) => raisedCount++;

        vm.OpenReturnsCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void OpenReturnsCommand_NoCurrentSeller_TreatedAsLackingRight_RaisesApprovalRequest()
    {
        // Nobody has confirmed on yet (Current == null) — fail closed, same as
        // CloseShiftCommand_NoCurrentSeller_TreatedAsLackingRight_RaisesApprovalRequest.
        using var vm = CreateViewModel(out var deps);
        var raisedCount = 0;
        vm.RefundApprovalRequested += (s, e) => raisedCount++;

        vm.OpenReturnsCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public async Task OpenReturnsCommand_SellerHasRight_NoApprovalRaised()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", canRefund: true));
        var raisedCount = 0;
        vm.RefundApprovalRequested += (s, e) => raisedCount++;

        await vm.OpenReturnsCommand.ExecuteAsync(null);

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public async Task ShowReturnsDialogAsync_IsReachable_AsTheApprovalContinuation()
    {
        // The continuation: approving must not just dismiss the overlay, it must reach
        // the method that actually opens returns — mirrors
        // OnCloseShiftApproved_AfterApprovalRequested_ActuallyClosesTheShift. This test
        // host has no running Avalonia application, so no window actually opens; what
        // this proves is that the method App.axaml.cs wires as the continuation
        // (ShowReturnsDialogAsync) is the same one OpenReturns itself calls once the
        // gate passes, not a dead end that only dismisses the overlay.
        using var vm = CreateViewModel(out var deps);

        await vm.ShowReturnsDialogAsync();
    }

    // ---------------------------------------------------------------------------------
    // Manual discount escalation (Task 21a): a percent discount above the current
    // seller's own MaxDiscount cap requires approval. MaxDiscount == 0 means "no
    // personal cap configured", not "no discounts allowed" — every seller has this right
    // after the seller-PIN migration, so it must never gate (see NeedsDiscountApproval).
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplyManualDiscount_PercentUnderCap_AppliesDirectly_NoApprovalRaised()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 15m));
        var raisedCount = 0;
        vm.DiscountApprovalRequested += (s, percent) => raisedCount++;
        vm.DiscountInputValue = "10";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(1, deps.CartService.SetManualDiscountCallCount);
        Assert.Equal(10m, deps.CartService.ManualDiscountPercent);
    }

    [Fact]
    public void ApplyManualDiscount_PercentAboveCap_RaisesApprovalRequest_DoesNotApplyYet()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 15m));
        decimal? raisedPercent = null;
        vm.DiscountApprovalRequested += (s, percent) => raisedPercent = percent;
        vm.DiscountInputValue = "20";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(20m, raisedPercent);
        Assert.Equal(0, deps.CartService.SetManualDiscountCallCount); // not applied until approved
    }

    [Fact]
    public void ApplyManualDiscount_ZeroCap_NeverGated_AppliesAnyPercent()
    {
        // The critical zero-cap rule: MaxDiscount == 0 means "no personal cap
        // configured", not "no discounts allowed". A seller with no cap must be able to
        // apply any manual discount without a supervisor PIN.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 0m));
        var raisedCount = 0;
        vm.DiscountApprovalRequested += (s, percent) => raisedCount++;
        vm.DiscountInputValue = "90";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(90m, deps.CartService.ManualDiscountPercent);
    }

    [Fact]
    public void ApplyManualDiscount_NoCurrentSeller_TreatedAsZeroCap_NeverGated()
    {
        using var vm = CreateViewModel(out var deps);
        // No SetCurrent call: Current stays null — same "no cap configured" outcome as
        // MaxDiscount == 0, not a reason to fail closed the way CloseShift/OpenReturns do.
        var raisedCount = 0;
        vm.DiscountApprovalRequested += (s, percent) => raisedCount++;
        vm.DiscountInputValue = "50";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(50m, deps.CartService.ManualDiscountPercent);
    }

    [Fact]
    public void ApplyManualDiscount_AmountModeUnderTheCap_AppliesDirectly()
    {
        // 500 off a 1000 cart is 50%, and this seller may go to 60%.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 60m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 1000m));
        var raisedCount = 0;
        vm.DiscountApprovalRequested += (s, percent) => raisedCount++;
        vm.IsDiscountPercentMode = false;
        vm.DiscountInputValue = "500";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(500m, deps.CartService.ManualDiscountAmount);
    }

    [Fact]
    public void ApplyManualDiscount_AmountModeOverTheCapInPercentTerms_RaisesApproval()
    {
        // The hole this closes: the cap is a percent, so an amount-mode discount used to
        // skip the check entirely. A seller allowed 5% could switch the modal to amount
        // mode and take half the receipt off, with nobody asked.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 5m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 1000m));
        decimal? raisedPercent = null;
        vm.DiscountApprovalRequested += (s, percent) => raisedPercent = percent;
        vm.IsDiscountPercentMode = false;
        vm.DiscountInputValue = "500";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(50m, raisedPercent);
        Assert.Equal(0m, deps.CartService.ManualDiscountAmount); // nothing applied yet
    }

    [Fact]
    public void ApplyManualDiscount_AmountModeOnAnEmptyCart_IsRefused()
    {
        // No subtotal to take a percent of, so nothing can establish whether this is
        // within the seller's cap. Refuse rather than guess.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 5m));
        var raisedCount = 0;
        vm.DiscountApprovalRequested += (s, percent) => raisedCount++;
        vm.IsDiscountPercentMode = false;
        vm.DiscountInputValue = "500";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(0m, deps.CartService.ManualDiscountAmount);
    }

    // ---------------------------------------------------------------------------------
    // Bounds. A manual discount is free text off a numeric pad, and CartService clamps
    // the total to the subtotal — so an over-100% entry did not look wrong anywhere, it
    // just produced a receipt for nothing.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplyManualDiscount_PercentAboveOneHundred_IsRefused()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1")); // no cap configured
        vm.DiscountInputValue = "500";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0m, deps.CartService.ManualDiscountPercent);
        Assert.True(vm.IsAlertModalVisible);
    }

    [Fact]
    public void ApplyManualDiscount_PercentOfExactlyOneHundred_IsAccepted()
    {
        // The whole receipt off is a real thing a manager does; 100 is the boundary,
        // not the refusal.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.DiscountInputValue = "100";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(100m, deps.CartService.ManualDiscountPercent);
    }

    [Fact]
    public void ApplyManualDiscount_NegativePercent_IsRefused()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.DiscountInputValue = "-5";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0m, deps.CartService.ManualDiscountPercent);
        Assert.True(vm.IsAlertModalVisible);
    }

    [Fact]
    public void ApplyManualDiscount_AmountLargerThanTheCart_IsRefused()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.IsDiscountPercentMode = false;
        vm.DiscountInputValue = "5000";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0m, deps.CartService.ManualDiscountAmount);
        Assert.True(vm.IsAlertModalVisible);
    }

    // ---------------------------------------------------------------------------------
    // approved_by lifetime (Task 21b): set on approval, sent with the sale, cleared
    // wherever the cart (or the discount it was approved for) is cleared, so it can
    // never leak into a receipt — or a discount — it wasn't actually approved for.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplyApprovedDiscount_AppliesPercentAndRecordsApprover_ReflectedInPay()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        Assert.Equal(40m, deps.CartService.ManualDiscountPercent);

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Equal("supervisor-9", deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public void ApplyManualDiscount_FreshDiscountAfterApproval_ClearsStaleApprover()
    {
        // A later discount that didn't itself need escalation must not inherit an
        // approver id recorded for a previous, different discount on the same receipt.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        vm.DiscountInputValue = "5"; // under cap, no approval needed this time
        vm.ApplyManualDiscountCommand.Execute(null);

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public void ClearManualDiscountCommand_ClearsStaleApprover()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        vm.ClearManualDiscountCommand.Execute(null);

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public void Pay_WithNoApprovedDiscount_OmitsApprovedByFromRequestAndJson()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        var request = deps.ExpenseDocumentService.LastRequest;
        Assert.NotNull(request);
        Assert.Null(request!.ApprovedBy);

        var json = System.Text.Json.JsonSerializer.Serialize(request);
        Assert.DoesNotContain("approved_by", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pay_OnSuccess_ClearsApprovedBy_DoesNotLeakIntoNextReceipt()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        MixedPaymentViewModel? firstPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) firstPaymentVm = m; };
        vm.PayCommand.Execute(null);
        firstPaymentVm!.CashAmount = firstPaymentVm.TotalAmount;
        firstPaymentVm.ConfirmPaymentCommand.Execute(null);
        Assert.Equal("supervisor-9", deps.ExpenseDocumentService.LastRequest!.ApprovedBy); // sanity check

        // A second, unrelated receipt: nobody approved anything for it. The completed
        // sale above dropped the confirmed seller (EndReceipt), so someone has to confirm
        // again before this one can be paid — Pay()'s own gate refuses otherwise.
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 50m));
        MixedPaymentViewModel? secondPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) secondPaymentVm = m; };
        vm.PayCommand.Execute(null);
        secondPaymentVm!.CashAmount = secondPaymentVm.TotalAmount;
        secondPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public void ClearCartCommand_ClearsApprovedById_DoesNotLeakIntoNextReceipt()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        vm.ClearCartCommand.Execute(null);

        // Dropping the receipt drops the confirmed seller with it (EndReceipt), so the
        // next one needs a fresh confirmation before Pay()'s gate will let it through.
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 50m));
        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public async Task ConfirmParkSaleCommand_ClearsApprovedById_DoesNotLeakIntoNextReceipt()
    {
        // Parking is a cart reset too (see ConfirmParkSale), just not a completed sale —
        // an approver id recorded for the parked receipt's discount must not attach to
        // whatever gets rung up next.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);

        vm.AddToCartCommand.Execute(MakeProduct("p2", 50m));
        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    // ---------------------------------------------------------------------------------
    // Park/resume carries the discount approval (review follow-up to Task 21): resuming
    // a parked sale used to restore the manual discount straight through
    // CartService.LoadSnapshot, bypassing ApplyManualDiscount/NeedsDiscountApproval
    // entirely, while ParkedSaleSnapshot had nowhere to keep the approver id — so a
    // properly-approved over-cap discount rode through to Pay() with approved_by silently
    // null. The fix carries the approval with the discount it authorised instead of
    // re-prompting (re-asking a supervisor to re-approve their own earlier decision would
    // be wrong, and would fail once that supervisor has gone home).
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ResumeParkedSale_ApprovedOverCapDiscount_SurvivesParkThenResume()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);

        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);

        // BuildSnapshot must have carried the approver into the parked payload.
        Assert.Equal("supervisor-9", deps.ParkedSaleService.LastParkedSnapshot?.ApprovedById);

        await vm.ResumeParkedSale(deps.ParkedSaleService.LastParkedId!);

        Assert.Equal(40m, deps.CartService.ManualDiscountPercent); // the discount itself came back

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Equal("supervisor-9", deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public void ParkedSaleSnapshot_DeserializesLegacyPayloadMissingApprovedById_NoCrash_ApprovedByIdIsNull()
    {
        // The real ParkedSaleService round-trips ParkedSaleSnapshot through
        // System.Text.Json (see its Payload field). This proves the actual backward-
        // compatibility claim at that boundary — not just "the fake behaves this way" —
        // by deserializing a JSON string shaped exactly like what a build that predates
        // this task would have already written into a real ParkedSale row in SQLite: no
        // "ApprovedById" property at all, not merely a null-valued one.
        const string legacyPayload = """
        {
          "Items": [],
          "ManualDiscountPercent": 40,
          "ManualDiscountAmount": 0,
          "CustomerDiscountPercent": 0,
          "AppliedCoupons": [],
          "Customer": null,
          "Label": null
        }
        """;

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ParkedSaleSnapshot>(legacyPayload);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.ApprovedById);
        Assert.Equal(40m, snapshot.ManualDiscountPercent); // rest of the shape still restores normally
    }

    [Fact]
    public async Task ResumeParkedSale_SnapshotWithoutApproverField_ResumesCleanly_NoApproverAttached()
    {
        // Stands in for a parked sale saved by a build that predates ApprovedById: the
        // property is left at its default (null), exactly what System.Text.Json produces
        // deserializing a payload that never had this field. Resuming it must not crash
        // and must not fabricate an approver for a discount nobody re-confirmed.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 0m)); // no cap: 40% never needed approval anyway
        var oldSnapshot = new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } },
            ManualDiscountPercent = 40m
            // ApprovedById intentionally left unset (null) — the pre-migration shape.
        };
        deps.ParkedSaleService.SeedParkedSnapshot("old-id", oldSnapshot);

        var exception = await Record.ExceptionAsync(() => vm.ResumeParkedSale("old-id"));

        Assert.Null(exception);
        Assert.Equal(40m, deps.CartService.ManualDiscountPercent); // discount itself still restored

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public async Task ResumeParkedSale_ThenClearDiscount_DropsRestoredApprover()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);
        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);
        await vm.ResumeParkedSale(deps.ParkedSaleService.LastParkedId!);

        vm.ClearManualDiscountCommand.Execute(null);

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public async Task ResumeParkedSale_ThenRaiseDiscountFurther_RequiresFreshApproval_DoesNotApplyRestoredApprover()
    {
        // The restored approval only ever covered the discount it was granted for (40%).
        // Asking for more on the same receipt must re-gate through NeedsDiscountApproval
        // exactly like a brand-new discount would — not silently ride on the approver
        // carried in from the resumed snapshot.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);
        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);
        await vm.ResumeParkedSale(deps.ParkedSaleService.LastParkedId!);

        decimal? raisedPercent = null;
        vm.DiscountApprovalRequested += (s, percent) => raisedPercent = percent;
        var callsBeforeAttempt = deps.CartService.SetManualDiscountCallCount;
        vm.DiscountInputValue = "60"; // above the 15% cap
        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(60m, raisedPercent); // escalated, same as any fresh over-cap discount
        Assert.Equal(callsBeforeAttempt, deps.CartService.SetManualDiscountCallCount); // not applied yet
        Assert.Equal(40m, deps.CartService.ManualDiscountPercent); // the resumed 40% is untouched meanwhile
    }

    // ---------------------------------------------------------------------------------
    // Resume seller gate (whole-branch review follow-up): resuming a parked sale fills the
    // cart via CartService.LoadSnapshot directly, never through AddToCart, so it used to be
    // a way to reach Pay() with a stale/absent session and no gate ever having fired — worse
    // than the removed-seller case, because an omitted seller is the legitimate backward-
    // compatible path and the server does not flag it. ResumeParkedSale now applies the same
    // start-of-receipt gate AddToCart uses.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ResumeParkedSale_NeverAsksWhoIsSelling()
    {
        // Resuming used to ask, because it fills the cart without going through AddToCart
        // and so slipped past that gate. With the only gate now at Pay, that reason is
        // gone: a resumed receipt reaches the till like any other and is asked there.
        //
        // Covers the worst case for attribution — nobody confirmed at all, the "register
        // just restarted and resuming is the cashier's first action" scenario. Even then
        // nothing is asked here; Pay is what refuses.
        using var vm = CreateViewModel(out var deps);
        deps.ParkedSaleService.SeedParkedSnapshot("parked-1", new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } }
        });
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        await vm.ResumeParkedSale("parked-1");

        Assert.Equal(0, raisedCount);
        Assert.Single(deps.CartService.Items); // the resume itself is unaffected
        Assert.Null(deps.SellerSession.Current); // still nobody — Pay is what catches this
    }

    [Fact]
    public async Task ResumeParkedSale_RestoredApprovedById_SurvivesAnAbsentSeller()
    {
        // A genuine park -> (session goes stale, e.g. the register restarted before anyone
        // resumed) -> resume round trip through FakeParkedSaleService, proving the discount
        // approval carried by ParkedSaleSnapshot.ApprovedById survives an absent seller on
        // the resume — the two concerns (who authorised the discount vs. who is selling)
        // must never conflate.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);
        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);

        // The register restarted with no seller persisted, or the idle timeout lapsed.
        deps.SellerSession.Clear();

        await vm.ResumeParkedSale(deps.ParkedSaleService.LastParkedId!);

        Assert.Equal(40m, deps.CartService.ManualDiscountPercent); // the approved discount still came back

        // Whoever picked the resumed receipt up confirms at the till, where Pay's gate
        // asks — without that, Pay refuses and this test would be asserting against a
        // document that was never built.
        deps.SellerSession.SetCurrent(MakeSeller("cashier"));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        // The approver id reached Pay() untouched by the seller gate having fired.
        Assert.Equal("supervisor-9", deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public async Task ResumeParkedSale_AutoParksExistingCart_LeavesOnlyTheResumedReceipt()
    {
        // Resuming while the cart already holds an in-progress receipt auto-parks that
        // receipt first, then loads the requested one — two cart-clearing/filling steps in
        // a single call. This used to also pin "the seller gate fires at most once, not
        // once per step"; there is no gate here any more, so what is left to protect is
        // that the two steps land the right cart.
        using var vm = CreateViewModel(out var deps);
        vm.AddToCartCommand.Execute(MakeProduct("p-current", 5m));

        deps.ParkedSaleService.SeedParkedSnapshot("parked-1", new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } }
        });
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        await vm.ResumeParkedSale("parked-1");

        Assert.Equal(0, raisedCount);
        Assert.Single(deps.CartService.Items);
        Assert.Equal("p1", deps.CartService.Items[0].Product.Id);
    }

    // ---------------------------------------------------------------------------------
    // Revoked shift session banner (Task 22): a 401 on a queued document must never throw
    // the cashier out to the login screen mid-receipt — receipts keep queueing, only a
    // banner (bound to IsSessionRevoked) appears. IExpenseDocumentService.SessionRevoked
    // is raised from SyncOfflineDocumentsAsync's background sync loop (see
    // ExpenseDocumentService's own remarks on NotifySessionRevoked), so — like every other
    // background-thread event this class subscribes to (OnUnsyncedDocumentsCountChanged,
    // OnSyncStatusChanged, ...) — the handler must marshal onto the UI thread via
    // Dispatcher.UIThread rather than mutate IsSessionRevoked directly. Dispatcher.UIThread
    // .Post does NOT run its callback synchronously even on a thread CheckAccess reports as
    // the UI thread (confirmed empirically: a callback stayed unrun until the queue was
    // drained), so these tests pump it explicitly with Dispatcher.UIThread.RunJobs() rather
    // than assuming same-thread Post is a same-thread no-op.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void SessionRevoked_FromExpenseDocumentService_SetsIsSessionRevoked_OnUiThread()
    {
        using var vm = CreateViewModel(out var deps);
        Assert.False(vm.IsSessionRevoked);

        deps.ExpenseDocumentService.RaiseSessionRevoked();

        // Not yet true: OnSessionRevoked only posts the mutation, it doesn't run inline —
        // proves the handler is genuinely marshalling rather than setting the property
        // directly from whatever thread raised the event.
        Assert.False(vm.IsSessionRevoked);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsSessionRevoked);
    }

    [Fact]
    public void IsSessionRevoked_OnceRaised_DoesNotSelfClear_OnUnrelatedCartActivity()
    {
        // Decision: the banner never clears itself. A 401 means the current auth token is
        // bad, and a bad token doesn't heal itself — SyncOfflineDocumentsAsync stops at the
        // very first 401 every pass (see its own remarks), so no later sync can ever
        // succeed to justify auto-clearing while this same token is still in use. Ringing
        // up and paying for more receipts (which is exactly what the design says must keep
        // working) must not silently make the warning disappear — only actually signing in
        // again does, and that constructs a brand-new PosViewModel instance.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier"));
        deps.ExpenseDocumentService.RaiseSessionRevoked();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsSessionRevoked);

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.True(vm.IsSessionRevoked);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSessionRevoked_LaterRaiseNeverSetsIsSessionRevoked()
    {
        var vm = CreateViewModel(out var deps);
        vm.Dispose();

        deps.ExpenseDocumentService.RaiseSessionRevoked();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsSessionRevoked);
    }

    // ---------------------------------------------------------------------------------
    // Shift-modal escape hatch / session-expiry lockout fix: unlike the queued-document
    // 401 above (which only raises a banner, since a receipt might be mid-flight),
    // ShiftService.SessionRevoked fires from GetShiftStateAsync/OpenShiftAsync — call
    // sites that only ever run while nothing is mid-receipt (startup, or the register
    // already blocked behind the shift modal with no way for it to ever succeed) — so
    // PosViewModel completes a real sign-out and asks the host to navigate to login,
    // rather than leaving the cashier trapped. The modal's own manual SignOutCommand (the
    // escape hatch a cashier can press regardless of *why* the modal is up) converges on
    // the exact same PerformSignOut path, so both are covered together here. A network
    // failure (simulated below as GetShiftStateAsync returning null without raising the
    // event — see ShiftServiceTest for that distinction proved at the real HTTP level)
    // must never trigger any of this: offline operation must never be treated as a
    // rejected session.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ShiftServiceSessionRevoked_ClearsCredentialsAndSellerSession_RequestsLogoutWithExplanation()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier"));
        var raisedCount = 0;
        string? loggedOutWith = null;
        vm.LogoutRequested += (s, explanation) => { raisedCount++; loggedOutWith = explanation; };

        deps.ShiftService.RaiseSessionRevoked();

        // Not yet applied: OnShiftSessionRevoked only posts the sign-out, it doesn't run it
        // inline — same marshalling proof as SessionRevoked_FromExpenseDocumentService_
        // SetsIsSessionRevoked_OnUiThread above.
        Assert.Equal(0, deps.AuthService.ClearSessionCallCount);
        Assert.Equal(0, raisedCount);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, deps.AuthService.ClearSessionCallCount);
        Assert.Null(deps.SellerSession.Current);
        Assert.Equal(1, raisedCount);
        Assert.Equal(I18nService.Instance["SessionExpiredSignInAgain"], loggedOutWith);
    }

    [Fact]
    public void ShiftServiceNetworkFailure_NoSessionRevokedRaised_NeverClearsCredentials_NeverRequestsLogout()
    {
        // GetShiftStateResult = null with no RaiseSessionRevoked call stands in for the real
        // ShiftService's network-failure path (returns null, event never fires — see
        // ShiftServiceTest.GetShiftStateAsync_NetworkUnreachable_...). The register should
        // just show the ordinary "start a shift" modal, exactly as it does today when it has
        // never been able to reach the server at all.
        using var vm = CreateViewModel(out var deps, d => d.ShiftService.GetShiftStateResult = null);
        var raisedCount = 0;
        vm.LogoutRequested += (s, e) => raisedCount++;

        Assert.False(vm.IsShiftOpen);
        Assert.True(vm.IsShiftModalVisible);
        Assert.Equal(0, deps.AuthService.ClearSessionCallCount);
        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromShiftServiceSessionRevoked_LaterRaiseNeverClearsCredentials()
    {
        var vm = CreateViewModel(out var deps);
        vm.Dispose();

        deps.ShiftService.RaiseSessionRevoked();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, deps.AuthService.ClearSessionCallCount);
    }

    [Fact]
    public void SignOutCommand_ClearsCredentialsAndSellerSession_RequestsLogoutWithEmptyExplanation()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier"));
        var raisedCount = 0;
        string? loggedOutWith = "not set by handler";
        vm.LogoutRequested += (s, explanation) => { raisedCount++; loggedOutWith = explanation; };

        // No Dispatcher pump needed here: unlike the automatic 401 recovery, the manual
        // escape hatch is a direct command execution on the calling (UI) thread, not a
        // background-thread event handler.
        vm.SignOutCommand.Execute(null);

        Assert.Equal(1, deps.AuthService.ClearSessionCallCount);
        Assert.Null(deps.SellerSession.Current);
        Assert.Equal(1, raisedCount);
        Assert.Equal(string.Empty, loggedOutWith);
    }

    [Fact]
    public void SignOutCommand_WorksWithNoOpenShift_RegardlessOfWhyTheModalIsUp()
    {
        // The escape hatch must be reachable whether the shift modal is up because of a
        // dead session or simply because nobody has opened a shift yet (e.g. the wrong
        // cashier launched the app) — same command, same result, either way.
        using var vm = CreateViewModel(out var deps, d => d.ShiftService.GetShiftStateResult = null);
        Assert.True(vm.IsShiftModalVisible);
        var raisedCount = 0;
        vm.LogoutRequested += (s, e) => raisedCount++;

        vm.SignOutCommand.Execute(null);

        Assert.Equal(1, deps.AuthService.ClearSessionCallCount);
        Assert.Equal(1, raisedCount);
    }

    // ---------------------------------------------------------------------------------
    // Feature-flag gates (Task 13): disabled entry points hide rather than grey out, and
    // turning off seller switching must take its permission gates with it. Every flag
    // below is set on Deps.Features BEFORE CreateViewModel runs — InitializeAsync's own
    // ApplyFeatures call (see PosViewModel's remarks) reads the cached map once the fakes'
    // already-completed Tasks let it run synchronously during construction, so a flag
    // flipped afterwards would not reliably be reflected without a real Dispatcher pump.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task CloseShiftCommand_SellerSwitchDisabled_ClosesWithoutApproval()
    {
        // With seller switching off, this register has no notion of separate sellers, so
        // nobody ever becomes Current. If CanCloseShift's gate stayed on regardless, it
        // would fire forever with no seller-switch overlay left to satisfy it (the
        // overlay is hidden along with the flag) — the shift could never close. The flag
        // must take its own gate down with it.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.SellerSwitch, false));
        var raisedCount = 0;
        vm.CloseShiftApprovalRequested += (s, e) => raisedCount++;

        await vm.CloseShiftCommand.ExecuteAsync(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(1, deps.ShiftService.CloseShiftCallCount);
    }

    [Fact]
    public void CloseShiftCommand_SellerSwitchEnabled_StillRequiresApproval()
    {
        // Guards the pre-existing behaviour against being lost while adding the flag
        // above: with seller switching on, a register with nobody confirmed must still
        // fail closed exactly as before — the new escape hatch is scoped to the flag
        // being off, not a general loosening of the close-shift gate.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.SellerSwitch, true));
        var raisedCount = 0;
        vm.CloseShiftApprovalRequested += (s, e) => raisedCount++;

        vm.CloseShiftCommand.Execute(null);

        Assert.Equal(1, raisedCount);
        Assert.Equal(0, deps.ShiftService.CloseShiftCallCount);
    }

    [Fact]
    public void OpenSellerSwitchCommand_Disabled_DoesNotRaise()
    {
        // A command that still raised the overlay behind a hidden chip would be dead
        // code reachable only by a stray click on a control the view no longer shows —
        // the command itself must refuse, not just the XAML visibility.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.SellerSwitch, false));
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.OpenSellerSwitchCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.False(vm.IsSellerSwitchEnabled);
    }

    [Fact]
    public void Returns_Disabled_HidesTheEntryPoint()
    {
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.Returns, false));

        Assert.False(vm.IsReturnsEnabled);
    }

    [Fact]
    public void ParkedSales_DisabledWithSalesStillParked_KeepsTheListReachable()
    {
        // Parked sales already sitting on this register outlive the flag being switched
        // off: "Park" (the write side) disappears at once, but the list stays reachable
        // until the last one is cleared — otherwise switching the flag off would strand
        // receipts with goods already picked and no money taken.
        using var vm = CreateViewModel(out var deps, d =>
        {
            d.Features.Set(CashFeatureCodes.ParkedSales, false);
            d.ParkedSaleService.Count = 2;
        });

        Assert.False(vm.IsParkingEnabled);
        Assert.True(vm.IsParkedSalesListVisible);
    }

    [Fact]
    public void ParkedSales_DisabledAndDrained_HidesTheListToo()
    {
        // Once the last parked sale is gone there is nothing left for the list to
        // reach, so it hides along with the flag, same as any other disabled entry
        // point.
        using var vm = CreateViewModel(out var deps, d =>
        {
            d.Features.Set(CashFeatureCodes.ParkedSales, false);
            d.ParkedSaleService.Count = 0;
        });

        Assert.False(vm.IsParkedSalesListVisible);
    }

    [Fact]
    public void CustomerDisplayDisabled_CartChanges_NotPushedToDisplay()
    {
        // The flag must be set BEFORE CreateViewModel runs (see the class remarks above
        // this section): InitializeAsync's own ApplyFeatures call reads the cached map
        // synchronously during construction with these fakes, so flipping it afterwards
        // would not reliably be reflected. CustomerDisplayViewModel itself is assigned
        // after construction, matching App.axaml.cs's own wiring (it's a settable
        // property, not a constructor dependency).
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.CustomerDisplay, false));
        var display = new CustomerDisplayViewModel();
        vm.CustomerDisplayViewModel = display;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        Assert.False(vm.IsCustomerDisplayEnabled);
        Assert.Empty(display.Items);
        Assert.True(display.IsIdle);
    }

    [Fact]
    public void CustomerDisplay_FlagDisabledAfterRefresh_ResetsAlreadyFedDisplayToIdle()
    {
        // Proves the actual mechanism OnIsCustomerDisplayEnabledChanged exists for: not
        // "a flag disabled from the start blocks pushing" (the test above already covers
        // that), but "a display that was fed on the constructor's optimistic first pass
        // gets pulled back to idle once the real, disabled value lands". PendingRefresh
        // holds InitializeAsync's RefreshAsync/ApplyFeatures pass open past construction,
        // so IsCustomerDisplayEnabled is still true (the default) when CustomerDisplayViewModel
        // is assigned and the cart is fed — exactly the window ApplyFeatures' own remarks
        // describe. Completing PendingRefresh below is what stands in for "the register's
        // storage becomes ready and the real fetch resolves".
        var pending = new TaskCompletionSource<bool>();
        using var vm = CreateViewModel(out var deps, d =>
        {
            d.Features.PendingRefresh = pending;
            d.Features.SetAfterRefresh(CashFeatureCodes.CustomerDisplay, false);
        });
        Assert.True(vm.IsCustomerDisplayEnabled); // still the optimistic default; real value hasn't landed

        var display = new CustomerDisplayViewModel();
        vm.CustomerDisplayViewModel = display;
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        // Fed while the flag still (optimistically) reads as enabled.
        Assert.False(display.IsIdle);
        Assert.NotEmpty(display.Items);

        // The real fetch resolves: RefreshAsync applies the queued disabled value and
        // InitializeAsync's own ApplyFeatures call re-snapshots it. TaskCompletionSource's
        // default (not RunContinuationsAsynchronously) behaviour runs the awaiting
        // continuation synchronously on this thread when nothing captured a
        // SynchronizationContext — there is none in this test host — so this line
        // deterministically drives ApplyFeatures to completion; no delay, no polling.
        pending.SetResult(true);

        Assert.False(vm.IsCustomerDisplayEnabled);
        Assert.True(display.IsIdle); // pulled back to idle, not left showing a stale cart
    }

    // ---------------------------------------------------------------------------------
    // Manual discount and coupon flags (Task 22): the store owner asked for exactly one
    // thing here — hide the button, keep the pricing untouched. Customer-category
    // discounts and automatic promotions do not go through OpenDiscountModal/
    // OpenCouponModal at all, so gating these two commands can never make an offline
    // total disagree with the server's.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void OpenDiscountModalCommand_FlagDisabled_DoesNotOpenTheModal()
    {
        // A command that still opened the modal behind a hidden button would be dead
        // code reachable only by a stray click on a control the view no longer shows —
        // same shape as OpenSellerSwitchCommand_Disabled_DoesNotRaise above, but for a
        // property flip (IsDiscountModalVisible) rather than an event.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.Discount, false));

        vm.OpenDiscountModalCommand.Execute(null);

        Assert.False(vm.IsDiscountEnabled);
        Assert.False(vm.IsDiscountModalVisible);
    }

    [Fact]
    public void OpenCouponModalCommand_FlagDisabled_DoesNotOpenTheModal()
    {
        // Same reasoning as OpenDiscountModalCommand_FlagDisabled_DoesNotOpenTheModal,
        // for the coupon button/modal pair.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.Coupons, false));

        vm.OpenCouponModalCommand.Execute(null);

        Assert.False(vm.IsCouponsEnabled);
        Assert.False(vm.IsCouponModalVisible);
    }

    [Fact]
    public void DiscountAndCouponFlags_NotConfigured_BothEnabled_BothModalsOpen()
    {
        // CashFeatures.IsEnabled treats an unknown/unconfigured code as enabled — the
        // default lives there and nowhere else (see its own remarks) — so a register
        // that hasn't heard about these two codes yet must behave exactly as it did
        // before this task: both buttons work.
        using var vm = CreateViewModel(out var deps);

        Assert.True(vm.IsDiscountEnabled);
        Assert.True(vm.IsCouponsEnabled);

        vm.OpenDiscountModalCommand.Execute(null);
        Assert.True(vm.IsDiscountModalVisible);

        vm.OpenCouponModalCommand.Execute(null);
        Assert.True(vm.IsCouponModalVisible);
    }

    // ---------------------------------------------------------------------------------
    // Exchange button: hidden behind its own flag, and additionally disabled offline —
    // an exchange has no offline queue (see ExchangeService's own remarks), so the
    // button must not promise something a disconnected register cannot deliver.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void IsExchangeVisible_FlagDisabled_HidesTheButtonRegardlessOfConnectivity()
    {
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.Exchange, false));

        Assert.False(vm.IsExchangeVisible);
        Assert.False(vm.IsExchangeEnabled);
    }

    [Fact]
    public void IsExchangeVisible_FlagArrivesDisabled_HidesTheButtonOnTheOpenScreen()
    {
        // The flag map lands after the screen is already up (see ApplyFeatures'
        // remarks), and until it does an unconfigured code reads as enabled — so this
        // is the normal path for a store that switched exchanges off, not an edge
        // case. IsExchangeVisible is computed live from the map, which means only an
        // explicit notification can move the binding: without one the button stays on
        // screen for the whole session, and no cashier is going to restart the
        // register to find out.
        var pending = new TaskCompletionSource<bool>();
        using var vm = CreateViewModel(out var deps, d =>
        {
            d.Features.PendingRefresh = pending;
            d.Features.SetAfterRefresh(CashFeatureCodes.Exchange, false);
        });
        Assert.True(vm.IsExchangeVisible); // the optimistic default; the real value hasn't landed

        var raised = new List<string?>();
        vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

        pending.SetResult(true); // the register's storage becomes ready and the fetch resolves

        Assert.False(vm.IsExchangeVisible);
        Assert.False(vm.IsExchangeEnabled);
        Assert.Contains(nameof(vm.IsExchangeVisible), raised);
        Assert.Contains(nameof(vm.IsExchangeEnabled), raised);
    }

    [Fact]
    public void IsExchangeEnabled_FlagOnButOffline_False()
    {
        // CashFeatures.IsEnabled treats an unconfigured code as enabled, so a register
        // that hasn't heard about this flag yet still shows the button — but offline is
        // still offline.
        using var vm = CreateViewModel(out var deps);

        // OnSyncStatusChanged only posts the mutation (same Dispatcher.UIThread marshalling
        // as OnSessionRevoked above), so the queue must be pumped explicitly.
        deps.SyncService.RaiseSyncStatusChanged(false);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsExchangeVisible);
        Assert.False(vm.IsExchangeEnabled);
    }

    [Fact]
    public void IsExchangeEnabled_FlagOnAndOnline_True_AndFollowsConnectivityLive()
    {
        // Unlike the snapshotted flags above (see ApplyFeatures' own remarks), this one
        // must track IsSystemOnline for as long as the POS screen stays open — a stale
        // "online" reading would offer an exchange the register can no longer send.
        using var vm = CreateViewModel(out var deps);
        Assert.True(vm.IsExchangeEnabled);

        deps.SyncService.RaiseSyncStatusChanged(false);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsExchangeEnabled);

        deps.SyncService.RaiseSyncStatusChanged(true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsExchangeEnabled);
    }

    // ---------------------------------------------------------------------------------
    // Server-side quoting. The register used to gate the quote on a warehouse id that
    // nothing in the server's responses ever carried, so POST /discounts/quote/ was
    // never called at all and every sale was priced locally.
    // ---------------------------------------------------------------------------------

    /// <summary>Blocks until the debounced requote (300 ms) has reached the quote service.
    /// Deliberately does NOT pump the dispatcher: Avalonia's headless dispatcher is not
    /// thread-safe, and RunJobs() from a post-await continuation — which lands on a
    /// thread-pool thread — races PosViewModel's own Dispatcher.Post calls and corrupts
    /// the priority queue. Everything asserted here is recorded by the fake, so the
    /// dispatcher does not need draining at all.</summary>
    private static void WaitForQuotes(FakeQuoteService svc, int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.CallCount < atLeast && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    [Fact]
    public void CartChange_QuotesTheCart_EvenWithNoWarehouseInSession()
    {
        using var vm = CreateViewModel(out var deps);

        deps.CartService.AddProduct(MakeProduct("p1", 100m));
        WaitForQuotes(deps.QuoteService, 1);

        Assert.Equal(1, deps.QuoteService.CallCount);
        Assert.Equal("p1", deps.QuoteService.Requests[0].Lines[0].ProductId);
        // Omitted rather than empty: the server takes the warehouse from the cash token,
        // which is the only place the register's warehouse is actually known.
        Assert.Null(deps.QuoteService.Requests[0].WarehouseId);
    }

    [Fact]
    public void Pay_QuotesTheCartBeforeOpeningThePaymentScreen()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier")); // Pay() refuses with nobody confirmed
        deps.CartService.AddProduct(MakeProduct("p1", 100m));
        WaitForQuotes(deps.QuoteService, 1);

        var quotesWhenPaymentOpened = -1;
        vm.NavigationRequest = target =>
        {
            if (target is MixedPaymentViewModel) quotesWhenPaymentOpened = deps.QuoteService.CallCount;
        };

        var before = deps.QuoteService.CallCount;
        vm.PayCommand.Execute(null);

        // The debounced requote can still be pending when the cashier hits Pay, so the
        // amount presented for payment must come from a quote taken right then.
        Assert.True(quotesWhenPaymentOpened > before,
            $"expected a fresh quote before the payment screen opened; before={before}, atOpen={quotesWhenPaymentOpened}");
    }

    [Fact]
    public void Pay_SendsTheQuotedUnitPriceAsSellPrice()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier")); // Pay() refuses with nobody confirmed
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        // Set directly rather than through a quote: this fake cart service does not
        // implement ApplyQuote, and the stamping itself is CartServiceQuoteTest's job.
        // What matters here is which of the two prices the outgoing document carries.
        deps.CartService.Items[0].QuotedUnitPrice = 90m;

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };
        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        // The server marks a line is_suspicious when sell_price differs from its catalog
        // price, so reporting the register's stale cached price would flag every honest
        // sale and bury the drift the check exists to catch.
        Assert.Equal(90m, deps.ExpenseDocumentService.LastRequest!.Products[0].SellPrice);
    }

    // ---------------------------------------------------------------------------------
    // End of receipt: a finished operation drops the confirmed seller outright, so the
    // next receipt cannot be rung up under the previous person's name inside the idle
    // window. See docs/superpowers/specs/2026-07-31-seller-reset-on-receipt-end-design.md.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Pay_OnSuccess_ClearsCurrentSeller()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        Assert.Null(deps.SellerSession.Current);
    }

    [Fact]
    public void Pay_WhenDocumentCreationFails_KeepsCurrentSeller()
    {
        // A failed payment is not the end of a receipt: the cashier is expected to try
        // again, and demanding a fresh PIN for a retry would punish the wrong person.
        using var vm = CreateViewModel(out var deps);
        deps.ExpenseDocumentService.CreateResult = false;
        var seller = MakeSeller("s1");
        deps.SellerSession.SetCurrent(seller);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated =>
        {
            if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m;
        };

        vm.PayCommand.Execute(null);
        Assert.NotNull(mixedPaymentVm);

        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        // The failure branch posts the alert via Dispatcher.UIThread.Post (see the
        // Revoked-shift-session remarks above). The assertion below doesn't need that
        // posted state, but leaving a job queued at test end is what the rest of this
        // file avoids.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(seller, deps.SellerSession.Current);
    }

    [Fact]
    public void ClearCart_ClearsCurrentSeller()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        vm.ClearCartCommand.Execute(null);

        Assert.Null(deps.SellerSession.Current);
    }

    [Fact]
    public void PayAfterReceiptEnded_AsksAgainWithoutWaitingOutTheIdleTimeout()
    {
        // The whole point of the reset: the idle clock has NOT elapsed (AddToCart's own
        // Touch() keeps resetting it), yet the next receipt must still be asked about,
        // because the previous one ended and dropped whoever sold it. Used to be proven at
        // AddToCart, which no longer asks — Pay is where the same guarantee now shows up.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.ClearCartCommand.Execute(null);
        Assert.False(deps.SellerSession.TimedOut);

        vm.AddToCartCommand.Execute(MakeProduct("p2", 10m));
        Assert.Equal(0, raisedCount); // building the next receipt asks nothing

        var navigated = 0;
        vm.NavigationRequest = _ => navigated++;
        vm.PayCommand.Execute(null);

        Assert.Equal(1, raisedCount);
        Assert.Equal(0, navigated);
    }

    [Fact]
    public void ClearCart_WithNobodyConfirmed_IsANoOp_RaisesNoCurrentChanged()
    {
        // The seller-switching-off case: nobody ever becomes Current there, and the reset
        // must degrade to nothing rather than churn the chip through CurrentChanged.
        using var vm = CreateViewModel(out var deps);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        var chipChanges = 0;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PosViewModel.SellerChipText)) chipChanges++;
        };

        vm.ClearCartCommand.Execute(null);

        Assert.Null(deps.SellerSession.Current);
        Assert.Equal(0, chipChanges);
    }

    // ---------------------------------------------------------------------------------
    // CanEndSellerSession: "no receipt is in progress". It used to also gate the overlay's
    // manual "stop selling" control; it no longer does (see OpenSellerSwitch). What it
    // still does is keep EndReceipt from treating a returns/exchange dialog closing as the
    // end of a receipt that is still being rung up.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void CanEndSellerSession_FalseWithItemsInCart()
    {
        using var vm = CreateViewModel(out var deps);

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        Assert.False(vm.CanEndSellerSession);
    }

    [Fact]
    public void CanEndSellerSession_TrueOnceCartIsEmptyAgain()
    {
        using var vm = CreateViewModel(out var deps);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        Assert.False(vm.CanEndSellerSession); // sanity check on the premise

        vm.ClearCartCommand.Execute(null);

        Assert.True(vm.CanEndSellerSession);
    }

    [Fact]
    public void CanEndSellerSession_TrueOnAFreshEmptyCart()
    {
        using var vm = CreateViewModel(out var deps);

        Assert.True(vm.CanEndSellerSession);
    }

    // ---------------------------------------------------------------------------------
    // Mid-receipt guard (final-review Finding 1): a returns/exchange dialog is a separate
    // window that never touches the POS cart, so it can close having booked a document
    // while the current receipt is still mid-ring — EndReceipt() must not clear the seller
    // in that case, or the rest of the receipt is left with nobody confirmed and nothing to
    // re-prompt (AddToCart's gate only fires on an EMPTY cart). See EndReceipt's own remarks
    // and docs/superpowers/specs/2026-07-31-seller-reset-on-receipt-end-design.md.
    //
    // Coverage note: EndReceipt() has exactly four callers. Two of them —
    // ShowReturnsDialogAsync and OpenExchange, the two the guard actually protects — need a
    // live Avalonia Window (they no-op silently without a desktop
    // IClassicDesktopStyleApplicationLifetime on Application.Current), which this xunit host
    // never provides — see this file's class-level remarks. That leaves no reachable route
    // to invoke EndReceipt() with a non-empty cart from here, so neither test below can
    // actually fail without the guard (both already empty the cart, or never call
    // EndReceipt at all, before the assertion): they document the guard's no-op contract for
    // the two reachable callers rather than reproducing the dialog bug itself. The dialog
    // bug fix was verified by reading ShowReturnsDialogAsync/OpenExchange plus manual
    // reasoning through the guard's condition, not by an automated test — a real regression
    // test would need an Avalonia.Headless-style host this project does not have.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ClearCart_WithItemsStillInCart_ClearsCurrentSeller()
    {
        // ClearCart empties the cart itself before calling EndReceipt(), so the new guard
        // ("only reset when the cart is empty") is a no-op here regardless of how many
        // items were sitting in the cart a moment ago — the legitimate reset must still go
        // through.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 20m));

        vm.ClearCartCommand.Execute(null);

        Assert.Empty(deps.CartService.Items);
        Assert.Null(deps.SellerSession.Current);
    }

    [Fact]
    public void Cart_WithItemsStillOpen_NeverClearsTheSellerOnItsOwn()
    {
        // The other half of the contract the guard protects, as far as this host can prove
        // it: simply having items in the cart never drops the confirmed seller by itself —
        // nothing short of an actual end-of-receipt call (Pay success, ClearCart, or a
        // dialog that booked a document) does. Contrast with the test above: there, the
        // seller drops because the cart got emptied, not because items were rung up.
        using var vm = CreateViewModel(out var deps);
        var seller = MakeSeller("s1");
        deps.SellerSession.SetCurrent(seller);

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 20m));

        Assert.Equal(2, deps.CartService.Items.Count);
        Assert.Same(seller, deps.SellerSession.Current);
    }

    // ---------------------------------------------------------------------------------
    // Deliberate non-reset points (final-review Finding 2): the design doc's "Точки, где
    // сброса намеренно нет" names park and auto-park-inside-resume alongside failed payment
    // (already covered above by Pay_WhenDocumentCreationFails_KeepsCurrentSeller) as points
    // that must NOT call EndReceipt. These two close that gap.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmParkSale_KeepsCurrentSeller()
    {
        // Parking is a pause, not the end of the seller's work — ResumeParkedSale's own
        // gate re-asks if the session has since gone stale, so EndReceipt() must never be
        // reached from here.
        using var vm = CreateViewModel(out var deps);
        var seller = MakeSeller("s1");
        deps.SellerSession.SetCurrent(seller);
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));

        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);

        Assert.Same(seller, deps.SellerSession.Current);
    }

    [Fact]
    public async Task ResumeParkedSale_AutoParkingAnInProgressCart_KeepsCurrentSeller()
    {
        // The auto-park branch inside ResumeParkedSale (an in-progress cart gets parked
        // before the requested one loads) is the middle of one operation, not the end of
        // one — same reasoning as the explicit park command above.
        using var vm = CreateViewModel(out var deps);
        var seller = MakeSeller("s1");
        deps.SellerSession.SetCurrent(seller);
        vm.AddToCartCommand.Execute(MakeProduct("p-current", 5m));

        deps.ParkedSaleService.SeedParkedSnapshot("parked-1", new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } }
        });

        await vm.ResumeParkedSale("parked-1");

        Assert.Same(seller, deps.SellerSession.Current);
    }

    // ---------------------------------------------------------------------------------
    // Exchange seller gate (follow-up to the whole-branch review): OpenExchange snapshots
    // _sellerSession.Current?.Id into ExchangeViewModel's constructor, and that id is what
    // ends up stamped as seller_id on the exchange's replacement-sale document. Before this
    // branch a confirmed seller survived between receipts, so an exchange usually carried
    // someone; this branch made EndReceipt() clear Current after every completed operation,
    // and OpenExchange had no seller gate of its own — so the ordinary "customer pays, next
    // customer wants an exchange" flow opened the exchange screen with nobody confirmed and
    // silently credited the resulting sale to the shift owner, with nothing on screen saying
    // so. Fixed by applying the same start-of-receipt gate AddToCart/ResumeParkedSale
    // already use, here as well — see OpenExchange's own remarks for why this is a
    // SellerSwitchRequested gate, not a RefundApprovalRequested/CloseShiftApprovalRequested
    // one.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void OpenExchange_WithNobodyConfirmed_RaisesSellerSwitchRequested()
    {
        using var vm = CreateViewModel(out var deps);
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.OpenExchangeCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void OpenExchange_WithSellerConfirmed_DoesNotRaise()
    {
        // Application.Current is null in this test host (see the class-level remarks), so
        // once the gate passes the method returns without opening any window — the same
        // limitation ShowReturnsDialogAsync/OpenExchange already live with here; this
        // assertion is about the gate, not the dialog.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.OpenExchangeCommand.Execute(null);

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void OpenExchange_WithSellerSwitchingDisabled_DoesNotRaise()
    {
        // Same seller-switch-off exception as everywhere else: with no separate sellers to
        // confirm, and the overlay itself hidden along with the flag, the gate must not
        // fire regardless of who (if anyone) is Current.
        using var vm = CreateViewModel(out var deps, d => d.Features.Set(CashFeatureCodes.SellerSwitch, false));
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.OpenExchangeCommand.Execute(null);

        Assert.Equal(0, raisedCount);
    }
}
