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
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
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

        public string ReceiptTemplate = string.Empty;
        public string ReceiptLogo = string.Empty;

        public Task SaveReceiptTemplateAsync(string raw) { ReceiptTemplate = raw; return Task.CompletedTask; }
        public Task<string> GetReceiptTemplateAsync() => Task.FromResult(ReceiptTemplate);
        public Task SaveReceiptLogoAsync(string base64) { ReceiptLogo = base64; return Task.CompletedTask; }
        public Task<string> GetReceiptLogoAsync() => Task.FromResult(ReceiptLogo);

        public Task SaveProductsAsync(IEnumerable<Product> products)
        {
            SavedProducts.AddRange(products);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult<IEnumerable<Product>>(SavedProducts);
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
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
        public Task MarkDocumentRejectedAsync(string hash, string reason) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(LastSyncVersion);
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
        public Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains) => Task.CompletedTask;
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
    public async Task SyncProductsAsync_VersionsOutOfOrder_StillFetchesEveryOne()
    {
        // The version walk compares each entry against a lastVersion that moves as it
        // goes, so it only ever works on an ascending list. Handed [3,1,2] it processed
        // 3, wrote lastVersion=3, and then skipped 1 and 2 as "already done" — and,
        // because that 3 was persisted, skipped them on every later sync too. The
        // products in those versions never reached the register at all.
        var fetched = new List<int>();
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[3,1,2],"status":0}""");

            var match = System.Text.RegularExpressions.Regex.Match(url, @"product/update/(\d+)/");
            if (match.Success)
            {
                var version = int.Parse(match.Groups[1].Value);
                fetched.Add(version);
                return (HttpStatusCode.OK,
                    $$"""{"message":"success","body":[{"id":"p{{version}}","name":"Товар","article":"A","barcode":"{{version}}","sell_price":10}],"status":0}""");
            }
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();
        var svc = Build(handler, storage);

        await svc.SyncProductsAsync();

        Assert.Equal(new[] { 1, 2, 3 }, fetched);
        Assert.Equal(3, storage.LastSyncVersion);
        Assert.Equal(3, storage.SavedProducts.Count);
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

    [Fact]
    public async Task SyncProductsAsync_ParsesUnitFields()
    {
        // The register converts m2 to pieces with no server to ask, so the whole
        // unit has to arrive during sync. unit_id especially: the document
        // validator matches it against the product's own unit.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1],"status":0}""");
            if (url.Contains("product/update/1/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"p1","name":"Плитка","sell_price":100,"unit_id":"u-1","unit_code":"m2","unit_short_name":"м²","unit_factor":0.24,"is_divisible":false,"sell_in_secondary_unit":true}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var product = Assert.Single(storage.SavedProducts);
        Assert.Equal("u-1", product.UnitId);
        Assert.Equal("m2", product.UnitCode);
        Assert.Equal("м²", product.UnitShortName);
        Assert.Equal(0.24m, product.UnitFactor);
        Assert.False(product.IsDivisible);
        Assert.True(product.SellInSecondaryUnit);
        Assert.True(product.HasSecondaryUnit);
    }

    [Fact]
    public async Task SyncProductsAsync_TreatsAMissingUnitAsPieceOnly()
    {
        // Most products have no secondary unit, and a backend older than the
        // units module sends none of these keys at all. Neither may break sync.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1],"status":0}""");
            if (url.Contains("product/update/1/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"p1","name":"Товар","sell_price":10}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var product = Assert.Single(storage.SavedProducts);
        Assert.Equal(string.Empty, product.UnitId);
        Assert.Equal(0m, product.UnitFactor);
        Assert.False(product.HasSecondaryUnit);
    }

    private const string Page1 =
        """{"body":[{"product_id":"a","quantity":5},{"product_id":"b","quantity":0}],"page_count":2,"total_items":3}""";
    private const string Page2 =
        """{"body":[{"product_id":"c","quantity":2.25}],"page_count":2,"total_items":3}""";

    /// <summary>The walk has to follow page_count, not stop at the first page. The
    /// request count assertion is not decoration: with a single-page stub the loop never
    /// iterates, and a partial-failure test written against it would be green without
    /// exercising anything.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_WalksEveryPage()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return (HttpStatusCode.OK, requests == 1 ? Page1 : Page2);
        });

        var result = await Build(handler, new FakeStorage()).FetchAllRemainsAsync();

        Assert.True(requests >= 2, $"expected the walk to request more than one page, saw {requests}");
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal(5m, result["a"]);
        Assert.Equal(0m, result["b"]);
        Assert.Equal(2.25m, result["c"]);
    }

    /// <summary>The most important test in the batch. A walk that breaks partway must
    /// return null, because the caller deletes everything the map does not mention — and
    /// half a map means half a catalogue deleted.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_SecondPageFails_ReturnsNull()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return requests == 1
                ? (HttpStatusCode.OK, Page1)
                : (HttpStatusCode.InternalServerError, "boom");
        });

        var result = await Build(handler, new FakeStorage()).FetchAllRemainsAsync();

        Assert.True(requests >= 2, $"expected the walk to reach the second page, saw {requests}");
        Assert.Null(result);
    }

    /// <summary>A transport failure is the offline case, and it is not an error worth
    /// throwing out of a background sync loop.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_TransportThrows_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("no network"));

        Assert.Null(await Build(handler, new FakeStorage()).FetchAllRemainsAsync());
    }

    /// <summary>A server that increments page_count on every response can never be
    /// satisfied, so the walk must give up at SyncService.MaxPages rather than loop
    /// forever. The stub's own exception is not the interesting assertion -- it exists
    /// only so a regression fails fast instead of hanging the suite, and it is set well
    /// past the ceiling so it never fires when the fix is in place. The request-count
    /// assertion below is what actually proves the production ceiling stopped the walk,
    /// rather than the stub's guard doing it instead.</summary>
    [Fact]
    public async Task FetchAllRemainsAsync_PageCountKeepsGrowing_AbandonsTheWalk()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            if (requests > SyncService.MaxPages + 50)
                throw new InvalidOperationException(
                    $"stub served {requests} requests without the walk giving up; the page-count ceiling did not fire");
            return (HttpStatusCode.OK, $$"""{"body":[],"page_count":{{requests + 1}},"total_items":0}""");
        });

        var result = await Build(handler, new FakeStorage()).FetchAllRemainsAsync();

        Assert.Null(result);
        Assert.True(requests <= SyncService.MaxPages + 1,
            $"expected the walk to stop at the page ceiling ({SyncService.MaxPages}), saw {requests} requests");
    }

    private const string ConfigOk = """
    {"status":0,"body":[{"id":"g1","name":"Чек","options":[
      {"id":"o1","name":"receiptTemplate","description":"","value":"{\"version\":1,\"width\":42,\"blocks\":[]}",
       "code":"receipt_template","value_type":"json"}]}]}
    """;

    [Fact]
    public async Task SyncReceiptTemplateAsync_CachesTheRawValue_OnSuccess()
    {
        var storage = new FakeStorage();
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, ConfigOk)), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":42", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_KeepsTheCache_OnAnHttpFailure()
    {
        // Потеря эндпоинта не должна откатывать магазин на дефолтный чек.
        var storage = new FakeStorage { ReceiptTemplate = """{"version":1,"width":48,"blocks":[]}""" };
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.InternalServerError, "")), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":48", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_KeepsTheCache_OnANegativeBackendStatus()
    {
        var storage = new FakeStorage { ReceiptTemplate = """{"version":1,"width":48,"blocks":[]}""" };
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, """{"status":-1,"body":null}""")), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":48", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_KeepsTheCache_WhenTheOptionIsAbsent()
    {
        // Тенант, где миграция ещё не прогнана: опции с этим кодом просто нет.
        var storage = new FakeStorage { ReceiptTemplate = """{"version":1,"width":48,"blocks":[]}""" };
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, """{"status":0,"body":[]}""")), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Contains("\"width\":48", storage.ReceiptTemplate);
    }

    [Fact]
    public async Task SyncReceiptTemplateAsync_IgnoresAnOptionWithAnEmptyCode()
    {
        // Каждая опция, засеянная до 20260728000800, приезжает с code = "" —
        // сегодня их два десятка. Совпадение по пустой строке склеило бы их все.
        var body = """
        {"status":0,"body":[{"id":"g1","name":"Прочее","options":[
          {"id":"o9","name":"storeName","description":"","value":"Лавка","code":"","value_type":"string"}]}]}
        """;
        var storage = new FakeStorage();
        var sync = Build(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, body)), storage);

        await sync.SyncReceiptTemplateAsync("http://x/");

        Assert.Equal(string.Empty, storage.ReceiptTemplate);
    }
}
