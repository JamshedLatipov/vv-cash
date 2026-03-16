using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Data;

public interface ISyncService
{
    Task SyncProductsAsync();
    Task FullReinitializeAsync();
}

public class SyncService : ISyncService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storageService;

    public SyncService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storageService)
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

    public async Task FullReinitializeAsync()
    {
        try
        {
            await _storageService.SetLastSyncVersionAsync(0);
            await SyncProductsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SyncService] Reinitialization error: {ex.Message}");
        }
    }

    public async Task SyncProductsAsync()
    {
        try
        {
            var baseUrl = GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return;

            int lastVersion = await _storageService.GetLastSyncVersionAsync();

            var versionsUrl = $"{baseUrl}cashes/product/versions/";
            var versionsResponse = await _httpClient.GetAsync(versionsUrl);

            if (versionsResponse.IsSuccessStatusCode)
            {
                var versionsContent = await versionsResponse.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(versionsContent);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var versionElem in bodyElement.EnumerateArray())
                        {
                            if (versionElem.ValueKind == JsonValueKind.Number)
                            {
                                int version = versionElem.GetInt32();
                                if (version > lastVersion)
                                {
                                    var updatedProducts = new List<Product>();
                                    var updateUrl = $"{baseUrl}cashes/product/update/{version}/";
                                    var updateResponse = await _httpClient.GetAsync(updateUrl);

                                    if (updateResponse.IsSuccessStatusCode)
                                    {
                                        var updateContent = await updateResponse.Content.ReadAsStringAsync();
                                        using var updateDoc = JsonDocument.Parse(updateContent);
                                        var updateRoot = updateDoc.RootElement;

                                        if (updateRoot.TryGetProperty("status", out var updateStatus) && updateStatus.GetInt32() == 0)
                                        {
                                            if (updateRoot.TryGetProperty("body", out var updateBody) && updateBody.ValueKind == JsonValueKind.Array)
                                            {
                                                foreach (var item in updateBody.EnumerateArray())
                                                {
                                                    try
                                                    {
                                                        string productId = Guid.NewGuid().ToString();
                                                        string productName = string.Empty;
                                                        string productSku = string.Empty;
                                                        string productCategory = string.Empty;
                                                        decimal productPrice = 0m;
                                                        string barcode = string.Empty;

                                                        if (item.TryGetProperty("id", out var idElem))
                                                            productId = idElem.GetString() ?? productId;

                                                        if (item.TryGetProperty("name", out var nameElem))
                                                            productName = nameElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("article", out var articleElem))
                                                            productSku = articleElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("category", out var catElem))
                                                            productCategory = catElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("barcode", out var barcodeElem))
                                                            barcode = barcodeElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("sell_price", out var priceElem))
                                                            productPrice = priceElem.ValueKind == JsonValueKind.Number ? priceElem.GetDecimal() : 0m;

                                                        updatedProducts.Add(new Product
                                                        {
                                                            Id = productId,
                                                            Name = productName,
                                                            Sku = productSku,
                                                            Category = productCategory,
                                                            Price = productPrice,
                                                            Barcode = barcode
                                                        });
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Debug.WriteLine($"[SyncService] Error parsing product: {ex.Message}");
                                                    }
                                                }
                                            }

                                            // Processed successfully, commit changes
                                            if (updatedProducts.Count > 0)
                                            {
                                                await _storageService.SaveProductsAsync(updatedProducts);
                                            }

                                            // Only advance the version after successful processing
                                            lastVersion = version;
                                            await _storageService.SetLastSyncVersionAsync(lastVersion);
                                        }
                                        else
                                        {
                                            // Failed response from backend update API, stop processing
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        // Network issue or HTTP error fetching this specific version, stop processing
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SyncService] Sync error: {ex.Message}");
        }
    }
}
