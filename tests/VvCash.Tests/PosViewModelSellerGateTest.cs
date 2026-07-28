using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
/// code-behind, not by an automated test.
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
        public Task<bool> PrintReturnReceiptAsync(IEnumerable<ReturnReceiptLine> lines, decimal totalRefund, string documentNumber) => Task.FromResult(true);
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
        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => Task.FromResult(Enumerable.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
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
        public event EventHandler? SettingsChanged;
        public void Save() { }
    }

    private class FakeExpenseDocumentService : IExpenseDocumentService
    {
        public DocumentRequest? LastRequest { get; private set; }

        public Task<bool> CreateExpenseDocumentAsync(DocumentRequest request)
        {
            LastRequest = request;
            return Task.FromResult(true);
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
        public Task<int> GetCountAsync() => Task.FromResult(0);
        public event EventHandler<int>? CountChanged;
    }

    // GetSalesAsync/GetReturnableLinesAsync/CreateReturnAsync are never reached by the
    // scenarios below (no test opens the Returns screen), so they throw loudly rather than
    // silently returning fabricated data that would never be checked.
    private class FakeReturnService : IReturnService
    {
        public Task<ExpenseListResponse> GetSalesAsync(int page = 1) => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
        public Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId) => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
        public Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request) => throw new NotSupportedException("not exercised by PosViewModelSellerGateTest");
    }

    private class FakeQuoteService : IQuoteService
    {
        public Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct) => Task.FromResult<QuoteResult?>(null);
    }

    private class FakeSessionContext : ISessionContext
    {
        public string? WarehouseId { get; set; }
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
            new FakeSyncService(),
            deps.SettingsService,
            deps.ExpenseDocumentService,
            new FakeCounterpartyService(),
            deps.ParkedSaleService,
            new FakeReturnService(),
            new FakeQuoteService(),
            new FakePromotionProvider(),
            new FakeSessionContext(),
            deps.HttpClient,
            deps.SellerSession,
            deps.RosterService,
            deps.AuthService);
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

    [Fact]
    public void AddToCart_EmptyCartAndStaleSession_RaisesSellerSwitchRequested()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.TimedOut = true; // also implied by Current == null, set explicitly for intent
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void AddToCart_EmptyCartButSessionNotStale_DoesNotRaise()
    {
        // IsStale is false only once a seller has actually been confirmed (Current != null)
        // and the idle clock hasn't elapsed — the real SellerSession can never be "not
        // stale" with nobody selected, so the fake must be put in that same state, not just
        // told to report false.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(new SellerInfo { Id = "s0", FirstName = "Prior", LastName = "Seller" });
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void AddToCart_SecondItemMidReceipt_NeverInterruptsEvenWhileSessionRemainsStale()
    {
        // Three items rung up by the same person must ask once, not once per item. Nobody
        // ever completes the overlay in this test (Current stays null throughout), so per
        // the real SellerSession.IsStale rule the session is stale for the whole test —
        // Touch() alone can never clear that. This is exactly the scenario that proves the
        // mid-receipt guard is doing real work: the gate must still not fire a second time,
        // because it is gated on the cart being empty, not on staleness.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.TimedOut = true;
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m)); // cart was empty -> gate fires
        Assert.Equal(1, raisedCount);
        Assert.True(deps.SellerSession.IsStale); // Touch() cannot clear this while Current is still null

        vm.AddToCartCommand.Execute(MakeProduct("p2", 5m)); // cart is non-empty -> gate must not fire again

        Assert.Equal(1, raisedCount);
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

    [Fact]
    public void Pay_WithNoCurrentSeller_OmitsSellerIdFromRequestAndJson()
    {
        using var vm = CreateViewModel(out var deps);
        // No SetCurrent call: ISellerSession.Current stays null, as at a register nobody
        // has confirmed on yet — Pay() must not crash and must leave SellerId unset so the
        // backend falls back to crediting the shift owner.
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
    public void ApplyManualDiscount_AmountMode_NeverGated_RegardlessOfCap()
    {
        // Amount-mode discounts aren't compared against a percent cap — out of scope for
        // NeedsDiscountApproval, same as before this task.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1", maxDiscount: 5m));
        var raisedCount = 0;
        vm.DiscountApprovalRequested += (s, percent) => raisedCount++;
        vm.IsDiscountPercentMode = false;
        vm.DiscountInputValue = "500";

        vm.ApplyManualDiscountCommand.Execute(null);

        Assert.Equal(0, raisedCount);
        Assert.Equal(500m, deps.CartService.ManualDiscountAmount);
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

        // A second, unrelated receipt: nobody approved anything for it.
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
    public async Task ResumeParkedSale_SessionStale_RequestsSellerSwitchOverlay()
    {
        // No SetCurrent call: Current stays null, exactly the "register just restarted,
        // nobody has confirmed on yet" scenario from the bug report — resuming is the
        // cashier's very first action, never touching AddToCart at all.
        using var vm = CreateViewModel(out var deps);
        deps.ParkedSaleService.SeedParkedSnapshot("parked-1", new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } }
        });
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        await vm.ResumeParkedSale("parked-1");

        Assert.Equal(1, raisedCount);
        // The gate does not block the resume itself — the parked items still land in the
        // cart; the overlay is a request the host opens on top, not a hard stop here.
        Assert.Single(deps.CartService.Items);
    }

    [Fact]
    public async Task ResumeParkedSale_SessionFreshWithActiveSeller_DoesNotRequestOverlay()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("s1"));
        deps.ParkedSaleService.SeedParkedSnapshot("parked-1", new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } }
        });
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        await vm.ResumeParkedSale("parked-1");

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public async Task ResumeParkedSale_RestoredApprovedById_IsUnaffectedBySellerGateFiring()
    {
        // A genuine park -> (session goes stale, e.g. the register restarted before anyone
        // resumed) -> resume round trip through FakeParkedSaleService, proving the discount
        // approval carried by ParkedSaleSnapshot.ApprovedById survives completely
        // independently of whether the new seller gate fires on the same resume — the two
        // concerns (who authorised the discount vs. who is selling) must never conflate.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.SetCurrent(MakeSeller("cashier", maxDiscount: 15m));
        vm.AddToCartCommand.Execute(MakeProduct("p1", 100m));
        vm.ApplyApprovedDiscount("supervisor-9", 40m);
        await vm.ConfirmParkSaleCommand.ExecuteAsync(null);

        // Simulate the session going stale/absent before the resume (e.g. a restart with
        // no seller persisted, or the idle timeout) — this is exactly what makes the new
        // gate fire below.
        deps.SellerSession.Clear();
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        await vm.ResumeParkedSale(deps.ParkedSaleService.LastParkedId!);

        Assert.Equal(1, raisedCount); // sanity check: the gate did fire for this resume
        Assert.Equal(40m, deps.CartService.ManualDiscountPercent); // the approved discount still came back

        MixedPaymentViewModel? mixedPaymentVm = null;
        vm.NavigationRequest = navigated => { if (navigated is MixedPaymentViewModel m) mixedPaymentVm = m; };
        vm.PayCommand.Execute(null);
        mixedPaymentVm!.CashAmount = mixedPaymentVm.TotalAmount;
        mixedPaymentVm.ConfirmPaymentCommand.Execute(null);

        // The approver id reached Pay() untouched by the seller gate having fired.
        Assert.Equal("supervisor-9", deps.ExpenseDocumentService.LastRequest!.ApprovedBy);
    }

    [Fact]
    public async Task ResumeParkedSale_AutoParksExistingCart_GateStillFiresOnlyOnce()
    {
        // Resuming while the cart already holds an in-progress receipt auto-parks that
        // receipt first, then loads the requested one — two cart-clearing/filling steps in
        // a single call. The gate must still fire at most once, not once per step.
        using var vm = CreateViewModel(out var deps);
        // Cart starts with an item from a receipt already in progress under a stale
        // session (mirrors AddToCart_SecondItemMidReceipt_NeverInterruptsEvenWhileSessionRemainsStale:
        // Touch() alone can never clear staleness while Current stays null).
        vm.AddToCartCommand.Execute(MakeProduct("p-current", 5m));
        Assert.True(deps.SellerSession.IsStale);

        deps.ParkedSaleService.SeedParkedSnapshot("parked-1", new ParkedSaleSnapshot
        {
            Items = new List<ParkedCartItem> { new() { Product = MakeProduct("p1", 100m), Quantity = 1 } }
        });
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        await vm.ResumeParkedSale("parked-1");

        Assert.Equal(1, raisedCount);
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
}
