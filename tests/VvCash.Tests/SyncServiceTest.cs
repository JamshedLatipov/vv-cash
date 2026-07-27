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
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeStorage : IOfflineStorageService
    {
        public int LastSyncVersion;
        public List<Product> SavedProducts = new();

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
}
