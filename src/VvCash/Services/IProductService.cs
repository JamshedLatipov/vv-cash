using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public interface IProductService
{
    Task<IEnumerable<Product>> GetAllProductsAsync();
    Task<IEnumerable<Product>> GetProductsByCategoryAsync(string category);
    Task<IEnumerable<Product>> SearchProductsAsync(string query);
    Task<Product?> GetProductByBarcodeAsync(string barcode);
    Task<IEnumerable<string>> GetCategoriesAsync();
}
