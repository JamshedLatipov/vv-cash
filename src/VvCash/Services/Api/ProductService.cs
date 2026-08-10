using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>Straight to the database. This used to load the entire catalog and
    /// filter it with LINQ — a full table scan plus one materialised Product per row,
    /// and PosViewModel calls it on every keystroke in the search box.</summary>
    public Task<IEnumerable<Product>> SearchProductsAsync(string query)
        => _storageService.SearchProductsAsync(query);

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
                        string imagePath = string.Empty;
                        var tagIds = new List<string>();

                        if (bodyElement.TryGetProperty("sell_price", out var priceElem))
                            productPrice = priceElem.ValueKind == JsonValueKind.Number ? priceElem.GetDecimal() : 0m;

                        // The endpoint returns the product flat (category_id, tags at
                        // the top level); the nested "product" object is the older
                        // shape. Read the flat fields first, then let the nested block
                        // below override them where it is present.
                        if (bodyElement.TryGetProperty("id", out var flatIdElem) && flatIdElem.ValueKind == JsonValueKind.String)
                            productId = flatIdElem.GetString() ?? productId;
                        if (bodyElement.TryGetProperty("name", out var flatNameElem) && flatNameElem.ValueKind == JsonValueKind.String)
                            productName = flatNameElem.GetString() ?? string.Empty;
                        if (bodyElement.TryGetProperty("category_id", out var flatCatElem) && flatCatElem.ValueKind == JsonValueKind.String)
                            productCategory = flatCatElem.GetString() ?? string.Empty;
                        ReadTagIds(bodyElement, tagIds);

                        if (bodyElement.TryGetProperty("product", out var productElem) && productElem.ValueKind == JsonValueKind.Object)
                        {
                            ReadTagIds(productElem, tagIds);

                            if (productElem.TryGetProperty("id", out var idElem))
                                productId = idElem.GetString() ?? productId;

                            if (productElem.TryGetProperty("name", out var nameElem))
                                productName = nameElem.GetString() ?? string.Empty;

                            if (productElem.TryGetProperty("article", out var articleElem))
                                productSku = articleElem.GetString() ?? string.Empty;

                            if (productElem.TryGetProperty("category", out var catElem) && catElem.ValueKind == JsonValueKind.Object)
                            {
                                if (catElem.TryGetProperty("id", out var catIdElem))
                                    productCategory = catIdElem.GetString() ?? string.Empty;
                                else if (catElem.TryGetProperty("name", out var catNameElem))
                                    productCategory = catNameElem.GetString() ?? string.Empty;
                            }

                            if (productElem.TryGetProperty("images", out var imagesElem) && imagesElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var img in imagesElem.EnumerateArray())
                                {
                                    if (img.TryGetProperty("path", out var pathElem))
                                    {
                                        imagePath = pathElem.GetString() ?? string.Empty;
                                        break;
                                    }
                                }
                            }
                        }

                        var product = new Product
                        {
                            Barcode = barcode,
                            Id = productId,
                            Name = productName,
                            Sku = productSku,
                            Category = productCategory,
                            Price = productPrice,
                            ImagePath = imagePath,
                            TagIds = tagIds
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

    /// <summary>Collects tag ids into <paramref name="into"/>, skipping duplicates.
    /// Tag ids decide whether a tag-targeted promotion applies to this product, so a
    /// scanned item has to carry them just like a synced one.</summary>
    private static void ReadTagIds(JsonElement element, List<string> into)
    {
        if (!element.TryGetProperty("tags", out var tagsElem) || tagsElem.ValueKind != JsonValueKind.Array)
            return;

        foreach (var tag in tagsElem.EnumerateArray())
        {
            string? id = tag.ValueKind switch
            {
                JsonValueKind.String => tag.GetString(),
                JsonValueKind.Object => tag.TryGetProperty("id", out var idElem) ? idElem.GetString() : null,
                _ => null,
            };
            if (!string.IsNullOrEmpty(id) && !into.Contains(id)) into.Add(id);
        }
    }

    public Task<IEnumerable<string>> GetCategoriesAsync()
    {
        return Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
    }
}
