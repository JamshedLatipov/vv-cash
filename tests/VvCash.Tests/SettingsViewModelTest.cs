using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>The backend URL is the one setting that decides where this register's
/// bearer token and cash token are sent. Nothing validated it.</summary>
public class SettingsViewModelTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = string.Empty;
        public string CashRegisterToken { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public int SaveCallCount { get; private set; }
        public event EventHandler? SettingsChanged;
        public void Save()
        {
            SaveCallCount++;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeFeatures : ICashFeatureService
    {
        public CashFeatures Current => CashFeatures.Default;
        public bool HasLoaded => true;
        public Task RefreshAsync() => Task.CompletedTask;
    }

    private sealed class FakePaymentCategories : IPaymentCategoryService
    {
        public Task<List<PaymentCategory>> GetPaymentCategoriesAsync()
            => Task.FromResult(new List<PaymentCategory>());
    }

    private sealed class FakeStorage : IOfflineStorageService
    {
        public Task SaveProductsAsync(IEnumerable<Product> products) => Task.CompletedTask;
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult(Enumerable.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult(Enumerable.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => Task.FromResult(Enumerable.Empty<Product>());
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
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync()
            => Task.FromResult(Enumerable.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task MarkDocumentRejectedAsync(string hash, string reason) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
        public int ClearCategoriesCallCount { get; private set; }
        public int ClearProductsCallCount { get; private set; }
        public Task ClearCategoriesAsync() { ClearCategoriesCallCount++; return Task.CompletedTask; }
        public Task ClearProductsAsync() { ClearProductsCallCount++; return Task.CompletedTask; }
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult(Enumerable.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers) => Task.CompletedTask;
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult(Enumerable.Empty<SellerInfo>());
        public Task InitializeAsync() => Task.CompletedTask;
    }

    private static SettingsViewModel Build(out FakeSettings settings)
        => Build(out settings, out _);

    private static SettingsViewModel Build(out FakeSettings settings, out FakeStorage storage)
    {
        settings = new FakeSettings();
        storage = new FakeStorage();
        return new SettingsViewModel(
            new MainViewModel(),
            settings,
            storage,
            new FakeFeatures(),
            new FakePaymentCategories());
    }

    /// <summary>Для тестов, которым нужна касса с уже настроенным состоянием:
    /// Build(out …) создаёт FakeSettings сам, и заполнить их до конструктора
    /// вью-модели нечем, а часть настроек читается именно там.</summary>
    private static SettingsViewModel BuildWith(FakeSettings settings)
        => new SettingsViewModel(
            new MainViewModel(),
            settings,
            new FakeStorage(),
            new FakeFeatures(),
            new FakePaymentCategories());

    [Theory]
    [InlineData("http://api.example.test/v1/")]
    [InlineData("example.test")]
    [InlineData("ftp://api.example.test/")]
    [InlineData("not a url at all")]
    public void Save_RefusesAnAddressThatIsNotHttps(string url)
    {
        // Plain http would put the bearer token and the cash token on the wire in the
        // clear, and a bare host or a typo silently produces a register that can never
        // reach anything — the failure only shows up later as "nothing syncs".
        var vm = Build(out var settings);
        vm.BackendUrl = url;

        vm.SaveCommand.Execute(null);

        Assert.Equal(0, settings.SaveCallCount);
        Assert.Equal(string.Empty, settings.BackendUrl);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public void Save_AcceptsAnHttpsAddress()
    {
        var vm = Build(out var settings);
        vm.BackendUrl = "https://api.example.test/v1/";

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal("https://api.example.test/v1/", settings.BackendUrl);
        Assert.True(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact]
    public void Save_AcceptsLocalhostOverHttp()
    {
        // The one address where plain http is not a leak: it never leaves the machine,
        // and it is how the backend is run during development and on-site debugging.
        var vm = Build(out var settings);
        vm.BackendUrl = "http://localhost:8080/api/v1/";

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, settings.SaveCallCount);
        Assert.Equal("http://localhost:8080/api/v1/", settings.BackendUrl);
    }

    // -----------------------------------------------------------------------------
    // The two remaining destructive buttons. This screen opens from the login screen,
    // before anyone has authenticated, and on an offline register a wiped catalog means
    // nothing can be sold until connectivity returns — so neither button may act on a
    // single tap.
    // -----------------------------------------------------------------------------

    [Fact]
    public void ClearProducts_OnlyArmsTheConfirmation_AndTouchesNothing()
    {
        var vm = Build(out _, out var storage);

        vm.ClearProductsCommand.Execute(null);

        Assert.True(vm.IsConfirmVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.ConfirmMessage));
        Assert.Equal(0, storage.ClearProductsCallCount);
    }

    [Fact]
    public void ClearCategories_OnlyArmsTheConfirmation_AndTouchesNothing()
    {
        var vm = Build(out _, out var storage);

        vm.ClearCategoriesCommand.Execute(null);

        Assert.True(vm.IsConfirmVisible);
        Assert.Equal(0, storage.ClearCategoriesCallCount);
    }

    [Fact]
    public async Task Confirm_RunsTheArmedActionAndClosesTheOverlay()
    {
        var vm = Build(out _, out var storage);
        vm.ClearProductsCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, storage.ClearProductsCallCount);
        Assert.Equal(0, storage.ClearCategoriesCallCount);
        Assert.False(vm.IsConfirmVisible);
    }

    [Fact]
    public async Task CancelConfirm_LeavesStorageAlone_AndDisarmsTheAction()
    {
        // Disarming matters as much as closing: a Confirm arriving later — a stray second
        // tap, a keyboard Enter — must not run an action the operator already refused.
        var vm = Build(out _, out var storage);
        vm.ClearCategoriesCommand.Execute(null);

        vm.CancelConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, storage.ClearCategoriesCallCount);
        Assert.False(vm.IsConfirmVisible);
    }

    [Fact]
    public async Task ArmingASecondActionReplacesTheFirst()
    {
        var vm = Build(out _, out var storage);
        vm.ClearProductsCommand.Execute(null);
        vm.ClearCategoriesCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, storage.ClearProductsCallCount);
        Assert.Equal(1, storage.ClearCategoriesCallCount);
    }

    [Fact]
    public void Save_WritesTheCodePagePerPrinter()
    {
        // На принтер, а не на кассу: в магазине могут стоять две разные железки.
        var vm = Build(out var settings);
        // Save() отказывает без валидного адреса (см. Save_RefusesAnAddressThatIsNotHttps);
        // это не то, что проверяет этот тест, поэтому адрес просто предоставлен.
        vm.BackendUrl = "https://api.example.test/v1/";
        vm.AddPrinterCommand.Execute(null);
        vm.Printers[0].SelectedCodePage = EscPosCodePages.Cp1251;

        vm.SaveCommand.Execute(null);

        Assert.Equal("CP1251", settings.Printers[0].CodePageId);
    }

    [Theory]
    [InlineData("CP1251", false)]
    [InlineData("CP-gone", true)]
    public void Load_ResolvesTheStoredCodePage(string stored, bool expectDefault)
    {
        // Известный id обязателен: на одном лишь неизвестном тест зелёный и без
        // загрузки — инициализатор поля даёт ровно ту же ссылку, что и Resolve.
        var settings = new FakeSettings
        {
            Printers = new List<PrinterConfig>
            {
                new() { Name = "P", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", CodePageId = stored }
            }
        };

        var vm = BuildWith(settings);

        Assert.Same(
            expectDefault ? EscPosCodePages.Default : EscPosCodePages.Cp1251,
            vm.Printers[0].SelectedCodePage);
    }
}
