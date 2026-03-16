using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Api;

public class ProductService : IProductService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ProductService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    private string GetBaseUrl()
    {
        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("BackendUrl is not configured.");

        if (!baseUrl.EndsWith("/"))
            baseUrl += "/";

        return baseUrl;
    }

    public Task<IEnumerable<Product>> GetAllProductsAsync()
    {
        return Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
    }

    public async Task<IEnumerable<Product>> GetProductsByCategoryAsync(string category)
    {
        var allProducts = new List<Product>();
        try
        {
            var baseUrl = GetBaseUrl();
            int currentPage = 1;
            int totalPages = 1;

            do
            {
                var url = $"{baseUrl}cashes/product/category/?category={Uri.EscapeDataString(category)}&page={currentPage}";
                Console.WriteLine($"[ProductService] GET products by category to {url}");
                Debug.WriteLine($"[ProductService] GET products by category to {url}");

                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[ProductService] Category response content (page {currentPage}): {responseContent}");
                    Debug.WriteLine($"[ProductService] Category response content (page {currentPage}): {responseContent}");

                    using var jsonDoc = JsonDocument.Parse(responseContent);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("page_count", out var pageCountElement) && pageCountElement.ValueKind == JsonValueKind.Number)
                    {
                        totalPages = pageCountElement.GetInt32();
                    }
                    else
                    {
                        totalPages = 1;
                    }

                    if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                    {
                        if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in bodyElement.EnumerateArray())
                            {
                                try
                                {
                                    string productId = Guid.NewGuid().ToString();
                                    string productName = string.Empty;
                                    string productSku = string.Empty;
                                    string productCategory = category;
                                    decimal productPrice = 0m;
                                    string barcode = string.Empty;

                                    // Try nested structure first
                                    if (item.TryGetProperty("product", out var productElem) && productElem.ValueKind == JsonValueKind.Object)
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
                                                productCategory = catNameElem.GetString() ?? category;
                                        }
                                    }
                                    else
                                    {
                                        // Flat structure
                                        if (item.TryGetProperty("id", out var idElem))
                                            productId = idElem.GetString() ?? productId;

                                        if (item.TryGetProperty("name", out var nameElem))
                                            productName = nameElem.GetString() ?? string.Empty;

                                        if (item.TryGetProperty("article", out var articleElem))
                                            productSku = articleElem.GetString() ?? string.Empty;
                                    }

                                    // Extract price from body element or root product
                                    if (item.TryGetProperty("sell_price", out var priceElem))
                                    {
                                        productPrice = priceElem.ValueKind == JsonValueKind.Number ? priceElem.GetDecimal() : 0m;
                                    }
                                    else if (item.TryGetProperty("price", out var pElem))
                                    {
                                        productPrice = pElem.ValueKind == JsonValueKind.Number ? pElem.GetDecimal() : 0m;
                                    }

                                    allProducts.Add(new Product
                                    {
                                        Id = productId,
                                        Name = productName,
                                        Sku = productSku,
                                        Category = productCategory,
                                        Price = productPrice,
                                        Barcode = barcode
                                    });
                                }
                                catch (Exception itemEx)
                                {
                                    Console.WriteLine($"[ProductService] Error parsing item in category: {itemEx.Message}");
                                    Debug.WriteLine($"[ProductService] Error parsing item in category: {itemEx.Message}");
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"[ProductService] Failed to get category products: {response.StatusCode}");
                    Debug.WriteLine($"[ProductService] Failed to get category products: {response.StatusCode}");
                    break;
                }

                currentPage++;
            } while (currentPage <= totalPages);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProductService] Error fetching products by category: {ex.Message}");
            Debug.WriteLine($"[ProductService] Error fetching products by category: {ex.Message}");
        }

        return allProducts;
    }

    public Task<IEnumerable<Product>> SearchProductsAsync(string query)
    {
        return Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
    }

    public async Task<Product?> GetProductByBarcodeAsync(string barcode)
    {
        Console.WriteLine($"[ProductService] GetProductByBarcodeAsync called for barcode: {barcode}");
        Debug.WriteLine($"[ProductService] GetProductByBarcodeAsync called for barcode: {barcode}");

        try
        {
            var url = $"{GetBaseUrl()}cashes/product/get/?barcode={Uri.EscapeDataString(barcode)}";
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
