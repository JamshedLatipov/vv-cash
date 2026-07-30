using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class SyncServiceTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeStorage : IOfflineStorageService
    {
        public int LastSyncVersion;
        public List<Product> SavedProducts = new();
        public List<Promotion>? SavedPromotions;

        public Task SavePromotionsAsync(IEnumerable<Promotion> promotions)
        {
            SavedPromotions = promotions.ToList();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Promotion>> GetPromotionsAsync()
            => Task.FromResult<IEnumerable<Promotion>>(SavedPromotions ?? new List<Promotion>());

        public Task ClearPromotionsAsync()
        {
            SavedPromotions = new List<Promotion>();
            return Task.CompletedTask;
        }

        public MoneyPolicy? SavedMoneyPolicy;

        public Task SaveMoneyPolicyAsync(MoneyPolicy policy)
        {
            SavedMoneyPolicy = policy;
            return Task.CompletedTask;
        }

        public Task<MoneyPolicy> GetMoneyPolicyAsync()
            => Task.FromResult(SavedMoneyPolicy ?? MoneyPolicy.Default);

        public CashFeatures? SavedCashFeatures;

        public Task SaveCashFeaturesAsync(CashFeatures features)
        {
            SavedCashFeatures = features;
            return Task.CompletedTask;
        }

        public Task<CashFeatures> GetCashFeaturesAsync()
            => Task.FromResult(SavedCashFeatures ?? CashFeatures.Default);

        public Task SaveProductsAsync(IEnumerable<Product> products)
        {
            SavedProducts.AddRange(products);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult<IEnumerable<Product>>(SavedProducts);
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());

        public Task SetLastSyncVersionAsync(int version)
        {
            LastSyncVersion = version;
            return Task.CompletedTask;
        }

        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(Array.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(LastSyncVersion);
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
        public Task ClearUnsyncedDocumentsAsync() => Task.CompletedTask;
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult<IEnumerable<ParkedSale>>(Array.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers) => Task.CompletedTask;
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult<IEnumerable<SellerInfo>>(Array.Empty<SellerInfo>());
        public Task InitializeAsync() => Task.CompletedTask;
    }

    private sealed class FakeExpenseDocuments : IExpenseDocumentService
    {
        public Task<bool> CreateExpenseDocumentAsync(Models.Api.DocumentRequest request) => Task.FromResult(true);
        public Task<Models.Api.ExpenseDocumentOutcome> CreateExpenseDocumentDetailedAsync(Models.Api.DocumentRequest request)
            => Task.FromResult(Models.Api.ExpenseDocumentOutcome.Sent("1"));
        public Task SyncOfflineDocumentsAsync() => Task.CompletedTask;
        public Task<int> GetUnsyncedDocumentsCountAsync() => Task.FromResult(0);
        public event EventHandler<int>? UnsyncedDocumentsCountChanged { add { } remove { } }
        public event EventHandler? SessionRevoked { add { } remove { } }
    }

    private static SyncService Build(StubHttpMessageHandler handler, FakeStorage storage)
        => new SyncService(new HttpClient(handler), new FakeSettings(), storage, new FakeExpenseDocuments());

    [Fact]
    public async Task SyncProductsAsync_NullBodyVersion_AdvancesAndContinues()
    {
        // Versions 1-3 published; 1 and 2 are empty for this cash (body null),
        // 3 carries a product. The sync must walk through the empty versions
        // instead of stopping at the first null body.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1,2,3],"status":0}""");
            if (url.Contains("product/update/3/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"p1","name":"Товар","article":"A1","barcode":"123","sell_price":99.5}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();
        var svc = Build(handler, storage);

        await svc.SyncProductsAsync();

        Assert.Equal(3, storage.LastSyncVersion);
        var product = Assert.Single(storage.SavedProducts);
        Assert.Equal("p1", product.Id);
        Assert.Equal(99.5m, product.Price);
    }

    [Fact]
    public async Task SyncProductsAsync_HttpErrorMidway_StopsWithoutAdvancing()
    {
        // A real failure (HTTP 500) must still stop the loop and keep the
        // version where it was, so the failed version is retried next sync.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1,2],"status":0}""");
            if (url.Contains("product/update/1/"))
                return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
            return (HttpStatusCode.InternalServerError, """{"message":"boom","body":null,"status":1}""");
        });
        var storage = new FakeStorage();
        var svc = Build(handler, storage);

        await svc.SyncProductsAsync();

        Assert.Equal(1, storage.LastSyncVersion);
        Assert.Empty(storage.SavedProducts);
    }

    [Fact]
    public async Task SyncProductsAsync_ParsesTagIds()
    {
        // Tag ids drive tag-targeted promotions offline; dropping them silently
        // would make those promotions apply to nothing on a disconnected register.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1],"status":0}""");
            if (url.Contains("product/update/1/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"p1","name":"Товар","sell_price":10,"tags":["t1","t2"]}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var product = Assert.Single(storage.SavedProducts);
        Assert.Equal(new[] { "t1", "t2" }, product.TagIds);
    }

    [Fact]
    public async Task SyncProductsAsync_CachesPromotions()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            if (url.Contains("cashes/promotion/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"promo1","name":"Летняя акция","enabled":true,"auto_apply":true,"apply_scope":"cart","priority":0,"max_uses":0,"used_count":0,"targets":[],"rules":[{"id":"r1","qty_op":"min","qty_from":2,"effect":"percent","value":15,"repeat":false}]}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var promo = Assert.Single(storage.SavedPromotions!);
        Assert.Equal("promo1", promo.Id);
        Assert.Equal("Летняя акция", promo.Name);
        var rule = Assert.Single(promo.Rules);
        Assert.Equal("min", rule.QtyOp);
        Assert.Equal(2m, rule.QtyFrom);
        Assert.Equal(15m, rule.Value);
    }

    [Fact]
    public async Task SyncPromotions_HttpFailure_KeepsPreviousCache()
    {
        // Losing the promotion endpoint must not blank the cache: pricing with
        // yesterday's promotions beats pricing with none.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            return (HttpStatusCode.InternalServerError, """{"message":"boom","body":null,"status":1}""");
        });
        var storage = new FakeStorage { SavedPromotions = new List<Promotion> { new() { Id = "old" } } };

        await Build(handler, storage).SyncProductsAsync();

        var promo = Assert.Single(storage.SavedPromotions!);
        Assert.Equal("old", promo.Id);
    }

    [Fact]
    public async Task SyncProductsAsync_CachesMoneyPolicy()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            if (url.Contains("cashes/money/"))
                return (HttpStatusCode.OK, """{"message":"success","body":{"scale":3,"mode":"BANK"},"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        Assert.Equal(3, storage.SavedMoneyPolicy!.Scale);
        Assert.Equal("BANK", storage.SavedMoneyPolicy.Mode);
    }

    [Fact]
    public async Task SyncMoneyPolicy_HttpFailure_KeepsCachedValue()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            return (HttpStatusCode.InternalServerError, """{"message":"boom","body":null,"status":1}""");
        });
        var storage = new FakeStorage { SavedMoneyPolicy = new MoneyPolicy { Scale = 0, Mode = "FLOOR" } };

        await Build(handler, storage).SyncProductsAsync();

        Assert.Equal(0, storage.SavedMoneyPolicy!.Scale);
        Assert.Equal("FLOOR", storage.SavedMoneyPolicy.Mode);
    }

    [Fact]
    public async Task SyncPromotions_EmptyBody_ClearsCache()
    {
        // The endpoint returning nothing means every promotion was disabled or
        // deleted; keeping the old set would discount carts for a dead campaign.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage { SavedPromotions = new List<Promotion> { new() { Id = "old" } } };

        await Build(handler, storage).SyncProductsAsync();

        Assert.Empty(storage.SavedPromotions!);
    }

    [Fact]
    public async Task SyncProductsAsync_CachesFeatures()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            if (url.Contains("cashes/features/"))
                return (HttpStatusCode.OK, """{"message":"success","body":{"cash_returns_enabled":false},"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var features = storage.SavedCashFeatures!;
        Assert.False(features.IsEnabled(CashFeatureCodes.Returns));
        Assert.True(features.IsEnabled(CashFeatureCodes.ParkedSales));
    }

    [Fact]
    public async Task SyncFeatures_HttpFailure_KeepsCachedValue()
    {
        // Losing the endpoint must not silently switch a function back on that
        // the store deliberately switched off, nor switch one off that it left on.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            return (HttpStatusCode.InternalServerError, """{"message":"boom","body":null,"status":1}""");
        });
        var storage = new FakeStorage
        {
            SavedCashFeatures = new CashFeatures
            {
                Flags = new Dictionary<string, bool> { [CashFeatureCodes.Returns] = false }
            }
        };

        await Build(handler, storage).SyncProductsAsync();

        Assert.False(storage.SavedCashFeatures!.IsEnabled(CashFeatureCodes.Returns));
    }

    [Fact]
    public async Task SyncFeatures_EmptyBody_SavesEmptyMap()
    {
        // "Nothing configured for this cash" is a legitimate answer meaning
        // everything is on, not an error to be ignored — it must still be saved,
        // so re-enabling the last disabled flag actually reaches the register.
        // Seed a non-empty cached map first so this test only passes if a save
        // genuinely happened, not merely because empty and missing read alike.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[],"status":0}""");
            if (url.Contains("cashes/features/"))
                return (HttpStatusCode.OK, """{"message":"success","body":{},"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage
        {
            SavedCashFeatures = new CashFeatures
            {
                Flags = new Dictionary<string, bool> { [CashFeatureCodes.Returns] = false }
            }
        };

        await Build(handler, storage).SyncProductsAsync();

        Assert.NotNull(storage.SavedCashFeatures);
        Assert.Empty(storage.SavedCashFeatures!.Flags);
        Assert.True(storage.SavedCashFeatures.IsEnabled(CashFeatureCodes.Returns));
    }
}
