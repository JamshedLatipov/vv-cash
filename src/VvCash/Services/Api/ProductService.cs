using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services.Api;

public class ProductService : IProductService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storageService;

    public ProductService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storageService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storageService = storageService;
    }

    private string GetBaseUrl()
    {
        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        return baseUrl;
    }

    public Task<IEnumerable<Product>> GetAllProductsAsync()
    {
        return _storageService.GetAllProductsAsync();
    }

    public async Task<IEnumerable<Product>> GetProductsByCategoryAsync(string category)
    {
        return await _storageService.GetProductsByCategoryAsync(category);
    }

    public Task<IEnumerable<Product>> SearchProductsAsync(string query)
    {
        return Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
    }

    public async Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        // Check offline storage first
        var localProduct = await _storageService.GetProductByBarcodeAsync(barcode);
        if (localProduct != null)
        {
            return localProduct;
        }

        Console.WriteLine($"[ProductService] GetProductByBarcodeAsync called for barcode: {barcode}");
        Debug.WriteLine($"[ProductService] GetProductByBarcodeAsync called for barcode: {barcode}");

        try
        {
            var baseUrl = GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return null;

            var url = $"{baseUrl}cashes/product/get/?barcode={Uri.EscapeDataString(barcode)}";
            Console.WriteLine($"[ProductService] GET to {url}");
            Debug.WriteLine($"[ProductService] GET to {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request);

            Console.WriteLine($"[ProductService] Response status: {response.StatusCode}");
            Debug.WriteLine($"[ProductService] Response status: {response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ProductService] Response content: {content}");
                Debug.WriteLine($"[ProductService] Response content: {content}");

                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Object)
                    {
                        string productId = Guid.NewGuid().ToString();
                        string productName = string.Empty;
                        string productSku = string.Empty;
                        string productCategory = string.Empty;
                        decimal productPrice = 0m;

                        if (bodyElement.TryGetProperty("sell_price", out var priceElem))
                        {
                            productPrice = priceElem.ValueKind == JsonValueKind.Number ? priceElem.GetDecimal() : 0m;
                        }

                        if (bodyElement.TryGetProperty("product", out var productElem) && productElem.ValueKind == JsonValueKind.Object)
                        {
                            if (productElem.TryGetProperty("id", out var idElem))
                                productId = idElem.GetString() ?? productId;

                            if (productElem.TryGetProperty("name", out var nameElem))
                                productName = nameElem.GetString() ?? string.Empty;

                            if (productElem.TryGetProperty("article", out var articleElem))
                                productSku = articleElem.GetString() ?? string.Empty;

                            if (productElem.TryGetProperty("category", out var catElem) && catElem.ValueKind == JsonValueKind.Object)
                            {
                                if (catElem.TryGetProperty("name", out var catNameElem))
                                    productCategory = catNameElem.GetString() ?? string.Empty;
                                else if (catElem.TryGetProperty("id", out var catIdElem))
                                    productCategory = catIdElem.GetString() ?? string.Empty;
                            }
                        }

                        var product = new Product
                        {
                            Barcode = barcode,
                            Id = productId,
                            Name = productName,
                            Sku = productSku,
                            Category = productCategory,
                            Price = productPrice
                        };

                        await _storageService.SaveProductsAsync(new[] { product });

                        return product;
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProductService] Error fetching product by barcode: {ex.Message}");
            Debug.WriteLine($"[ProductService] Error fetching product by barcode: {ex.Message}");
            return null;
        }
    }

    public Task<IEnumerable<string>> GetCategoriesAsync()
    {
        return Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
    }
}
