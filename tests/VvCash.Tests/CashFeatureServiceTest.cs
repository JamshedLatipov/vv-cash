using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Constants;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class CashFeatureServiceTest
{
    // Minimal stub: only the two feature methods carry real behaviour, everything
    // else is a trivial default this test never exercises.
    private sealed class FakeStorage : IOfflineStorageService
    {
        public CashFeatures? Returns;

        public Task SaveCashFeaturesAsync(CashFeatures features)
        {
            Returns = features;
            return Task.CompletedTask;
        }

        public Task<CashFeatures> GetCashFeaturesAsync() => Task.FromResult(Returns ?? CashFeatures.Default);

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
        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task SaveUnsyncedDocumentAsync(string hash, string payload) => Task.CompletedTask;
        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync() => Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(Array.Empty<KeyValuePair<string, string>>());
        public Task DeleteUnsyncedDocumentAsync(string hash) => Task.CompletedTask;
        public Task MarkDocumentRejectedAsync(string hash, string reason) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
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

    [Fact]
    public void Current_BeforeAnyRefresh_IsEnabledIsTrueForAnyCode()
    {
        // The POS screen binds to Current during construction, before the first
        // await resolves, so a freshly constructed service must already answer
        // "enabled" for anything rather than throw or lock the screen down.
        var svc = new CashFeatureService(new FakeStorage());

        Assert.True(svc.Current.IsEnabled(CashFeatureCodes.Returns));
        Assert.True(svc.Current.IsEnabled("unknown-code"));
    }

    [Fact]
    public async Task RefreshAsync_LoadsTheCachedMap()
    {
        var storage = new FakeStorage
        {
            Returns = new CashFeatures
            {
                Flags = new Dictionary<string, bool> { [CashFeatureCodes.Returns] = false }
            }
        };
        var svc = new CashFeatureService(storage);

        await svc.RefreshAsync();

        Assert.False(svc.Current.IsEnabled(CashFeatureCodes.Returns));
    }
}
