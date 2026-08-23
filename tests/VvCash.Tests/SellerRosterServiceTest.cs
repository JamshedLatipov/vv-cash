using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class SellerRosterServiceTest
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
        public List<SellerInfo> Sellers = new();
        public bool ThrowOnGetSellers;
        public bool ThrowOnSaveSellers;
        public int GetSellersCallCount;

        public Task SaveProductsAsync(IEnumerable<Product> products) => Task.CompletedTask;
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());
        public Task SavePromotionsAsync(IEnumerable<Promotion> promotions) => Task.CompletedTask;
        public Task<IEnumerable<Promotion>> GetPromotionsAsync() => Task.FromResult<IEnumerable<Promotion>>(Array.Empty<Promotion>());
        public Task ClearPromotionsAsync() => Task.CompletedTask;
        public Task SaveMoneyPolicyAsync(MoneyPolicy policy) => Task.CompletedTask;
        public Task<MoneyPolicy> GetMoneyPolicyAsync() => Task.FromResult(MoneyPolicy.Default);
        public Task SaveCashFeaturesAsync(CashFeatures features) => Task.CompletedTask;
        public Task<CashFeatures> GetCashFeaturesAsync() => Task.FromResult(CashFeatures.Default);

        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(Array.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task MarkDocumentRejectedAsync(string hash, string reason) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
        public Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains) => Task.CompletedTask;
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult<IEnumerable<ParkedSale>>(Array.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;

        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers)
        {
            if (ThrowOnSaveSellers)
                throw new InvalidOperationException("simulated storage failure on save");
            Sellers = sellers.ToList();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<SellerInfo>> GetSellersAsync()
        {
            GetSellersCallCount++;
            if (ThrowOnGetSellers)
                throw new InvalidOperationException("simulated storage failure on read");
            return Task.FromResult<IEnumerable<SellerInfo>>(Sellers);
        }

        public Task InitializeAsync() => Task.CompletedTask;
    }

    private const string SuccessBody = """
        {"status":0,"body":[
          {"id":"u-1","first_name":"Азиз","last_name":"Каримов","pin_hash":"pbkdf2_sha256$1000$c2FsdA==$aGFzaA==",
           "can_sell":true,"can_refund":true,"can_close_shift":false,"max_discount":15},
          {"id":"u-2","first_name":"Дилноза","last_name":"Юсупова","pin_hash":"",
           "can_sell":true,"can_refund":false,"can_close_shift":false,"max_discount":0}
        ]}
        """;

    private static SellerRosterService Build(StubHttpMessageHandler handler, FakeStorage storage)
        => new SellerRosterService(new HttpClient(handler), new FakeSettings(), storage);

    [Fact]
    public async Task RefreshAsync_SuccessfulResponse_ParsesAndCaches()
    {
        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, SuccessBody));
        var storage = new FakeStorage();
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Equal(2, result.Count);

        var aziz = result.Single(s => s.Id == "u-1");
        Assert.Equal("Азиз", aziz.FirstName);
        Assert.Equal("Каримов", aziz.LastName);
        Assert.True(aziz.CanRefund);
        Assert.Equal(15m, aziz.MaxDiscount);
        Assert.True(aziz.HasPin);

        var dilnoza = result.Single(s => s.Id == "u-2");
        Assert.False(dilnoza.CanRefund);
        Assert.Equal(0m, dilnoza.MaxDiscount);
        Assert.False(dilnoza.HasPin);

        // Cache was written.
        Assert.Equal(2, storage.Sellers.Count);
        Assert.Contains(storage.Sellers, s => s.Id == "u-1");

        // Request shape.
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Contains("cashes/seller/", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RefreshAsync_NetworkFailure_ReturnsCachedRoster()
    {
        var cached = new List<SellerInfo>
        {
            new SellerInfo { Id = "cached-1", FirstName = "Кэш", LastName = "Продавцов", CanSell = true }
        };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => throw new HttpRequestException("network down"));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("cached-1", result[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_HttpErrorStatus_ReturnsCachedRoster()
    {
        var cached = new List<SellerInfo>
        {
            new SellerInfo { Id = "cached-2", FirstName = "Кэш2", LastName = "" }
        };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.InternalServerError, """{"message":"boom"}"""));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("cached-2", result[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_NonZeroStatus_ReturnsCachedRoster()
    {
        var cached = new List<SellerInfo> { new SellerInfo { Id = "cached-3" } };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, """{"status":1,"body":[]}"""));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("cached-3", result[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_BodyIsNotArray_ReturnsCachedRoster()
    {
        var cached = new List<SellerInfo> { new SellerInfo { Id = "cached-4" } };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, """{"status":0,"body":null}"""));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("cached-4", result[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_MalformedJson_ReturnsCachedRoster()
    {
        var cached = new List<SellerInfo> { new SellerInfo { Id = "cached-5" } };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, "not json at all"));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("cached-5", result[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_BlankBackendUrl_ReturnsCachedRosterWithoutNetworkCall()
    {
        var cached = new List<SellerInfo> { new SellerInfo { Id = "cached-6" } };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => throw new InvalidOperationException("should not be called"));
        var settings = new FakeSettings { BackendUrl = "" };
        var svc = new SellerRosterService(new HttpClient(handler), settings, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("cached-6", result[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_ServerReturnsEmptyList_OverwritesCacheWithEmpty()
    {
        var cached = new List<SellerInfo> { new SellerInfo { Id = "stale-1" } };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, """{"status":0,"body":[]}"""));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Empty(result);
        Assert.Empty(storage.Sellers);
    }

    [Fact]
    public async Task GetCachedAsync_DelegatesToStorage()
    {
        var cached = new List<SellerInfo> { new SellerInfo { Id = "only-cached" } };
        var storage = new FakeStorage();
        await storage.SaveSellersAsync(cached);

        var handler = new StubHttpMessageHandler(req => throw new InvalidOperationException("must not hit network"));
        var svc = Build(handler, storage);

        var result = (await svc.GetCachedAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("only-cached", result[0].Id);
    }

    [Fact]
    public async Task GetCachedAsync_StorageThrows_PropagatesException()
    {
        // Deliberate: unlike RefreshAsync, GetCachedAsync makes no non-throwing
        // promise. It is a bare passthrough (per spec), so a broken local
        // database should surface to the caller rather than being silently
        // reported as "this register has no sellers".
        var storage = new FakeStorage { ThrowOnGetSellers = true };
        var handler = new StubHttpMessageHandler(req => throw new InvalidOperationException("must not hit network"));
        var svc = Build(handler, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetCachedAsync());
    }

    [Fact]
    public async Task RefreshAsync_NetworkFailsAndCacheReadThrows_ReturnsEmptyWithoutThrowing()
    {
        // The last line of defence (the cache read inside the catch block) must
        // itself be safe. If it isn't, a locked/corrupt local database turns a
        // routine refresh into an unhandled exception - strictly worse than
        // "nothing changed".
        var storage = new FakeStorage { ThrowOnGetSellers = true };
        var handler = new StubHttpMessageHandler(req => throw new HttpRequestException("network down"));
        var svc = Build(handler, storage);

        var result = await svc.RefreshAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task RefreshAsync_SuccessfulResponse_DoesNotTouchCacheReadEvenIfItWouldThrow()
    {
        // The success path returns the freshly parsed roster directly; it must
        // not consult the cache at all, so a broken cache read must not affect
        // a successful refresh.
        var storage = new FakeStorage { ThrowOnGetSellers = true };
        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, SuccessBody));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(0, storage.GetSellersCallCount);
    }

    [Fact]
    public async Task RefreshAsync_SaveThrows_ReturnsOldCachedRosterNotParsedOne()
    {
        // A successful parse whose persistence fails must fall back to the
        // previously cached roster - not the freshly parsed (but unsaved) one,
        // and not an exception.
        var storage = new FakeStorage { ThrowOnSaveSellers = true };
        storage.Sellers = new List<SellerInfo> { new SellerInfo { Id = "old-cached" } };
        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, SuccessBody));
        var svc = Build(handler, storage);

        var result = (await svc.RefreshAsync()).ToList();

        Assert.Single(result);
        Assert.Equal("old-cached", result[0].Id);
    }

    // ---------------------------------------------------------------------------------
    // Coalescing (post-Task-17 fix): the background sync loop and the UI-thread
    // shift-open/restore paths can legitimately call RefreshAsync around the same
    // moment. Without coalescing, two overlapping HTTP round-trips race to write
    // SellerSession's roster last, so whichever happens to resolve later wins
    // regardless of which was actually more recent. StubHttpMessageHandler's responder
    // completes synchronously and so cannot demonstrate two calls genuinely
    // overlapping — SuspendableHttpMessageHandler below provides a real suspension
    // point, same idea as SellerSwitchViewModelTest's TaskCompletionSource-backed
    // SlowSession.
    // ---------------------------------------------------------------------------------

    /// <summary>Unlike StubHttpMessageHandler, SendAsync here genuinely suspends until
    /// the test calls Complete — the only way to make two RefreshAsync callers actually
    /// overlap instead of each completing before the next one starts.</summary>
    private sealed class SuspendableHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<(HttpStatusCode Code, string Body)> _tcs = new();
        public int SendCount { get; private set; }

        public void Complete(HttpStatusCode code, string body) => _tcs.SetResult((code, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SendCount++;
            var (code, body) = await _tcs.Task;
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingBackendUrlSettings : ISettingsService
    {
        public bool ThrowOnAccess { get; set; }
        public string BackendUrl
        {
            get => ThrowOnAccess
                ? throw new InvalidOperationException("simulated settings failure")
                : "https://example.test/api/v1/";
            set { }
        }
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

    [Fact]
    public async Task RefreshAsync_TwoOverlappingCallers_CoalesceIntoOneFetch_BothGetSameRoster()
    {
        var handler = new SuspendableHttpMessageHandler();
        var storage = new FakeStorage();
        var svc = new SellerRosterService(new HttpClient(handler), new FakeSettings(), storage);

        // Neither call awaits yet, so both RefreshAsync() invocations run synchronously
        // up to (and including) the point where the HTTP call genuinely suspends. The
        // second call must observe the first's in-flight task rather than starting its
        // own HTTP round-trip.
        var first = svc.RefreshAsync();
        var second = svc.RefreshAsync();

        Assert.Same(first, second); // literally the same Task — proof of coalescing, not just equal results
        Assert.Equal(1, handler.SendCount); // only one HTTP call was actually issued

        handler.Complete(HttpStatusCode.OK, SuccessBody);

        var firstResult = (await first).ToList();
        var secondResult = (await second).ToList();

        Assert.Equal(1, handler.SendCount); // still just the one fetch after both awaits resolve
        Assert.Equal(2, firstResult.Count);
        Assert.Equal(2, secondResult.Count);
    }

    [Fact]
    public async Task RefreshAsync_CallerAfterCompletedRefresh_StartsFreshFetch()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            callCount++;
            return (HttpStatusCode.OK, SuccessBody);
        });
        var storage = new FakeStorage();
        var svc = Build(handler, storage);

        await svc.RefreshAsync();
        Assert.Equal(1, callCount);

        // The in-flight slot must have been cleared once the first call completed, so
        // this second call — arriving well after, not overlapping — issues its own
        // fresh HTTP request rather than replaying a stale completed task.
        await svc.RefreshAsync();
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RefreshAsync_FailedSharedInFlightTask_DoesNotPoisonNextCall()
    {
        // BackendUrl throwing happens before FetchRosterAsync's own try/catch even
        // starts, so this is a genuine fault propagating out of the in-flight task —
        // the scenario the coalescing wrapper's finally-based cleanup exists for. This
        // does NOT contradict RefreshAsync's documented "never throws" contract for
        // FetchRosterAsync itself (which is unchanged and still fully defensive); it
        // proves the coalescing layer added on top doesn't cache a failure forever even
        // when something upstream of that contract goes wrong.
        var settings = new ThrowingBackendUrlSettings { ThrowOnAccess = true };
        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, SuccessBody));
        var storage = new FakeStorage();
        var svc = new SellerRosterService(new HttpClient(handler), settings, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RefreshAsync());

        // The next caller after the failure must get a fresh attempt, not a cached
        // exception replayed from the same faulted in-flight task.
        settings.ThrowOnAccess = false;
        var result = (await svc.RefreshAsync()).ToList();

        Assert.Equal(2, result.Count);
    }

    // ---------------------------------------------------------------------------------
    // SetPinAsync (Task 19): first-time PIN setup for a seller whose pin_hash is
    // empty. Runs under the caller's own token (the shift owner's) against the admin
    // PIN-reset endpoint, and — same never-throw discipline as RefreshAsync — must
    // report every failure as false rather than propagate an exception.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task SetPinAsync_Success_PostsToResetEndpointAndRefreshesRoster()
    {
        var storage = new FakeStorage();
        HttpRequestMessage? pinResetRequest = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.ToString();
            if (path.Contains("users/pin/reset/"))
            {
                pinResetRequest = req;
                return (HttpStatusCode.OK, """{"status":0,"body":null}""");
            }
            if (path.Contains("cashes/seller/"))
                return (HttpStatusCode.OK, SuccessBody);
            throw new InvalidOperationException($"unexpected request to {path}");
        });
        var svc = Build(handler, storage);

        var result = await svc.SetPinAsync("u-9", "4821");

        Assert.True(result);

        Assert.NotNull(pinResetRequest);
        Assert.Equal(HttpMethod.Post, pinResetRequest!.Method);
        Assert.Contains("users/pin/reset/", pinResetRequest.RequestUri!.ToString());
        Assert.Contains("\"user\":\"u-9\"", handler.LastRequestBody);
        Assert.Contains("\"pin\":\"4821\"", handler.LastRequestBody);

        // Roster was refreshed and re-cached as a consequence of the successful reset.
        Assert.Equal(2, storage.Sellers.Count);
    }

    [Fact]
    public async Task SetPinAsync_NonSuccessStatusCode_ReturnsFalseWithoutRefreshingRoster()
    {
        var storage = new FakeStorage();
        var rosterCallCount = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.ToString();
            if (path.Contains("users/pin/reset/"))
                return (HttpStatusCode.BadRequest, """{"status":1,"message":"weak pin"}""");
            rosterCallCount++;
            return (HttpStatusCode.OK, SuccessBody);
        });
        var svc = Build(handler, storage);

        var result = await svc.SetPinAsync("u-9", "1111");

        Assert.False(result);
        Assert.Equal(0, rosterCallCount);
        Assert.Empty(storage.Sellers);
    }

    [Fact]
    public async Task SetPinAsync_EnvelopeStatusNonZero_ReturnsFalseWithoutRefreshingRoster()
    {
        var storage = new FakeStorage();
        var rosterCallCount = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.ToString();
            if (path.Contains("users/pin/reset/"))
                return (HttpStatusCode.OK, """{"status":1,"message":"weak pin"}""");
            rosterCallCount++;
            return (HttpStatusCode.OK, SuccessBody);
        });
        var svc = Build(handler, storage);

        var result = await svc.SetPinAsync("u-9", "1111");

        Assert.False(result);
        Assert.Equal(0, rosterCallCount);
    }

    [Fact]
    public async Task SetPinAsync_NetworkFailure_ReturnsFalseWithoutThrowing()
    {
        var storage = new FakeStorage();
        var handler = new StubHttpMessageHandler(req => throw new HttpRequestException("network down"));
        var svc = Build(handler, storage);

        var result = await svc.SetPinAsync("u-9", "4821");

        Assert.False(result);
    }

    [Fact]
    public async Task SetPinAsync_BlankBackendUrl_ReturnsFalseWithoutNetworkCall()
    {
        var storage = new FakeStorage();
        var handler = new StubHttpMessageHandler(req => throw new InvalidOperationException("should not be called"));
        var settings = new FakeSettings { BackendUrl = "" };
        var svc = new SellerRosterService(new HttpClient(handler), settings, storage);

        var result = await svc.SetPinAsync("u-9", "4821");

        Assert.False(result);
    }

    [Fact]
    public async Task SetPinAsync_MalformedJsonResponse_ReturnsFalseWithoutThrowing()
    {
        var storage = new FakeStorage();
        var handler = new StubHttpMessageHandler(req => (HttpStatusCode.OK, "not json at all"));
        var svc = Build(handler, storage);

        var result = await svc.SetPinAsync("u-9", "4821");

        Assert.False(result);
    }
}
