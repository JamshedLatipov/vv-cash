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

    Task SetLastSyncVersionAsync(int version);
    Task SaveUnsyncedDocumentAsync(string hash, string payload);
    Task<IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync();
    Task DeleteUnsyncedDocumentAsync(string hash);
    Task<int> GetLastSyncVersionAsync();

    Task ClearCategoriesAsync();
    Task ClearProductsAsync();
    Task ClearUnsyncedDocumentsAsync();

    // Parked sales (отложенные чеки)
    Task SaveParkedSaleAsync(ParkedSale sale);
    Task<IEnumerable<ParkedSale>> GetParkedSalesAsync();
    Task<ParkedSale?> GetParkedSaleAsync(string id);
    Task DeleteParkedSaleAsync(string id);

    Task InitializeAsync();
}
