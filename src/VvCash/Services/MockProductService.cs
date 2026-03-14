using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public class MockProductService : IProductService
{
    private readonly List<Product> _products = new()
    {
        new Product { Id = "1", Name = "Classic White T-Shirt", Sku = "CLT-001", Category = "Clothing", Price = 24.99m, Barcode = "1234567890" },
        new Product { Id = "2", Name = "Slim Fit Jeans", Sku = "SFJ-002", Category = "Clothing", Price = 59.99m, OriginalPrice = 79.99m, DiscountPercent = 25, Barcode = "1234567891" },
        new Product { Id = "3", Name = "Floral Summer Dress", Sku = "FSD-003", Category = "Clothing", Price = 44.99m, Barcode = "1234567892" },
        new Product { Id = "4", Name = "Wool Sweater", Sku = "WSW-004", Category = "Clothing", Price = 69.99m, OriginalPrice = 89.99m, DiscountPercent = 22, Barcode = "1234567893" },
        new Product { Id = "5", Name = "Running Sneakers", Sku = "RSN-005", Category = "Shoes", Price = 89.99m, Barcode = "1234567894" },
        new Product { Id = "6", Name = "Leather Oxford Shoes", Sku = "LOS-006", Category = "Shoes", Price = 119.99m, OriginalPrice = 149.99m, DiscountPercent = 20, Barcode = "1234567895" },
        new Product { Id = "7", Name = "Canvas Slip-Ons", Sku = "CSO-007", Category = "Shoes", Price = 39.99m, Barcode = "1234567896" },
        new Product { Id = "8", Name = "Leather Belt", Sku = "LBT-008", Category = "Accessories", Price = 29.99m, Barcode = "1234567897" },
        new Product { Id = "9", Name = "Silk Scarf", Sku = "SSC-009", Category = "Accessories", Price = 34.99m, Barcode = "1234567898" },
        new Product { Id = "10", Name = "Baseball Cap", Sku = "BBC-010", Category = "Accessories", Price = 19.99m, Barcode = "1234567899" },
        new Product { Id = "11", Name = "Denim Jacket", Sku = "DJK-011", Category = "Sale", Price = 49.99m, OriginalPrice = 99.99m, DiscountPercent = 50, Barcode = "1234567900" },
        new Product { Id = "12", Name = "Sport Shorts", Sku = "SPT-012", Category = "Sale", Price = 14.99m, OriginalPrice = 29.99m, DiscountPercent = 50, Barcode = "1234567901" },
    };

    public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult<IEnumerable<Product>>(_products);

    public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string category)
    {
        if (category == "All") return GetAllProductsAsync();
        var result = _products.Where(p => p.Category == category);
        return Task.FromResult<IEnumerable<Product>>(result);
    }

    public Task<IEnumerable<Product>> SearchProductsAsync(string query)
    {
        var lower = query.ToLowerInvariant();
        var result = _products.Where(p =>
            p.Name.Contains(lower, System.StringComparison.OrdinalIgnoreCase) ||
            p.Sku.Contains(lower, System.StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IEnumerable<Product>>(result);
    }

    public Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        var product = _products.FirstOrDefault(p => p.Barcode == barcode);
        return Task.FromResult(product);
    }

    public Task<IEnumerable<string>> GetCategoriesAsync()
    {
        var categories = new[] { "All", "Clothing", "Shoes", "Accessories", "Sale" };
        return Task.FromResult<IEnumerable<string>>(categories);
    }
}
