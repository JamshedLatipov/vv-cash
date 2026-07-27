using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeStorage : IOfflineStorageService
    {
        public List<SellerInfo> Sellers = new();

        public Task SaveProductsAsync(IEnumerable<Product> products) => Task.CompletedTask;
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());

        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(Array.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
        public Task ClearUnsyncedDocumentsAsync() => Task.CompletedTask;
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult<IEnumerable<ParkedSale>>(Array.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;

        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers)
        {
            Sellers = sellers.ToList();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult<IEnumerable<SellerInfo>>(Sellers);

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
}
