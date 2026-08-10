using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Data;

public interface IOfflineStorageService
{
    Task SaveProductsAsync(IEnumerable<Product> products);
    Task<IEnumerable<Product>> GetAllProductsAsync();
    Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId);
    Task<Product?> GetProductByBarcodeAsync(string barcode);

    Task SaveCategoriesAsync(IEnumerable<Category> categories);
    Task<IEnumerable<Category>> GetCategoriesAsync();

    Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories);
    Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync();

    // Auto-applied promotions, cached so carts can be priced while offline.
    Task SavePromotionsAsync(IEnumerable<Promotion> promotions);
    Task<IEnumerable<Promotion>> GetPromotionsAsync();
    Task ClearPromotionsAsync();

    // Store money rounding, so offline pricing rounds the way the server does.
    Task SaveMoneyPolicyAsync(MoneyPolicy policy);
    Task<MoneyPolicy> GetMoneyPolicyAsync();

    // Which register functions are switched on, so the POS screen knows what to
    // show before the first sync of the day completes.
    Task SaveCashFeaturesAsync(CashFeatures features);
    Task<CashFeatures> GetCashFeaturesAsync();

    Task SetLastSyncVersionAsync(int version);
    Task SaveUnsyncedDocumentAsync(string hash, string payload);
    Task<IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync();
    Task DeleteUnsyncedDocumentAsync(string hash);

    /// <summary>Takes a queued document out of the retry rotation without discarding it,
    /// for the one case where retrying is pointless: the server answered, on its merits,
    /// that it will not accept this document. Retrying that forever is what made the
    /// unsynced badge a permanent fixture on some registers — the count never dropped,
    /// and a genuinely queued sale behind it was indistinguishable from the stuck one.
    /// The row stays in the database with its reason so the back office can still read
    /// what the register tried to book; it just stops being resent.</summary>
    Task MarkDocumentRejectedAsync(string hash, string reason);
    Task<int> GetLastSyncVersionAsync();

    Task ClearCategoriesAsync();
    Task ClearProductsAsync();
    Task ClearUnsyncedDocumentsAsync();

    // Parked sales (отложенные чеки)
    Task SaveParkedSaleAsync(ParkedSale sale);
    Task<IEnumerable<ParkedSale>> GetParkedSalesAsync();
    Task<ParkedSale?> GetParkedSaleAsync(string id);
    Task DeleteParkedSaleAsync(string id);

    // Seller roster cache (продавцы кассы)
    Task SaveSellersAsync(IEnumerable<SellerInfo> sellers);
    Task<IEnumerable<SellerInfo>> GetSellersAsync();

    Task InitializeAsync();
}
