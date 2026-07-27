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
        public bool IsStale { get; set; }
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
            IsStale = false;
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
        public decimal ManualDiscountPercent => 0m;
        public decimal ManualDiscountAmount => 0m;
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

        public void ClearCart()
        {
            _items.Clear();
            CartChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyCoupon(Coupon coupon) { }
        public void RemoveCoupon(string code) { }
        public void SetManualDiscount(decimal percent, decimal amount) { }
        public void ClearManualDiscount() { }
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
        public Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons) => Task.FromResult(true);
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
        public Task<string?> OpenShiftAsync() => Task.FromResult<string?>("shift-1");
        public Task<bool> CloseShiftAsync(string shiftId) => Task.FromResult(true);
        public Task<string?> GetShiftStateAsync() => Task.FromResult<string?>("shift-1");
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
    }

    private class FakeCounterpartyService : ICounterpartyService
    {
        public Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request) => Task.FromResult<CounterpartyResponse?>(null);
        public Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query) => Task.FromResult<List<CounterpartyResponse>?>(new List<CounterpartyResponse>());
    }

    private class FakeParkedSaleService : IParkedSaleService
    {
        public Task<ParkedSale> ParkAsync(ParkedSaleSnapshot snapshot, decimal total) => Task.FromResult(new ParkedSale());
        public Task<IReadOnlyList<ParkedSale>> GetAllAsync() => Task.FromResult<IReadOnlyList<ParkedSale>>(Array.Empty<ParkedSale>());
        public Task<ParkedSaleSnapshot?> ResumeAsync(string id) => Task.FromResult<ParkedSaleSnapshot?>(null);
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

    private sealed class Deps
    {
        public FakeSellerSession SellerSession { get; } = new();
        public FakeExpenseDocumentService ExpenseDocumentService { get; } = new();
        public HttpClient HttpClient { get; } = new();
    }

    private static PosViewModel CreateViewModel(out Deps deps)
    {
        deps = new Deps();
        return new PosViewModel(
            new FakeProductService(),
            new FakeCategoryService(),
            new FakeCartService(),
            new FakePrinterService(),
            new FakeCustomerDisplayService(),
            new FakeShiftService(),
            new FakeOfflineStorageService(),
            new FakeSyncService(),
            new FakeSettingsService(),
            deps.ExpenseDocumentService,
            new FakeCounterpartyService(),
            new FakeParkedSaleService(),
            new FakeReturnService(),
            new FakeQuoteService(),
            new FakeSessionContext(),
            deps.HttpClient,
            deps.SellerSession);
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
        deps.SellerSession.IsStale = true;
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void AddToCart_EmptyCartButSessionNotStale_DoesNotRaise()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.IsStale = false;
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void AddToCart_SecondItemMidReceipt_NeverInterruptsEvenIfSessionGoesStaleAgain()
    {
        // Three items rung up by the same person must ask once, not once per item — and
        // must never interrupt an in-progress receipt even if the idle clock (simulated
        // here directly, since FakeSellerSession's IsStale is test-controlled rather than
        // clock-driven) claims staleness again mid-sale.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.IsStale = true;
        var raisedCount = 0;
        vm.SellerSwitchRequested += (s, e) => raisedCount++;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m)); // cart was empty -> gate fires, Touch() clears IsStale
        Assert.Equal(1, raisedCount);

        deps.SellerSession.IsStale = true; // force staleness back on, as if the clock elapsed mid-receipt
        vm.AddToCartCommand.Execute(MakeProduct("p2", 5m)); // cart is non-empty -> gate must not fire again

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void AddToCart_AlwaysTouchesSessionOnGenuineActivity()
    {
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.IsStale = false;

        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
        vm.AddToCartCommand.Execute(MakeProduct("p2", 5m));

        Assert.Equal(2, deps.SellerSession.TouchCount);
    }

    [Fact]
    public void OpenSellerSwitch_AlwaysRaisesRegardlessOfCartOrStaleness()
    {
        // Tapping the header chip is an explicit request, not the implicit start-of-receipt
        // gate, so it must open the overlay even mid-receipt / while not stale.
        using var vm = CreateViewModel(out var deps);
        deps.SellerSession.IsStale = false;
        vm.AddToCartCommand.Execute(MakeProduct("p1", 10m));
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
}
