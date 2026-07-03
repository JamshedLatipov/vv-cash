using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Api;

namespace VvCash.Services.Data;

public interface ISyncService
{
    event EventHandler<bool>? SyncStatusChanged;
    event EventHandler? ProductsSynced;
    Task SyncProductsAsync();
    Task FullReinitializeAsync();
    Task<bool> CheckSystemOnlineAsync();
}

public class SyncService : ISyncService
{
    public event EventHandler<bool>? SyncStatusChanged;
    public event EventHandler? ProductsSynced;

    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storageService;

    private readonly IExpenseDocumentService _expenseDocumentService;

    public SyncService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storageService, IExpenseDocumentService expenseDocumentService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storageService = storageService;
        _expenseDocumentService = expenseDocumentService;
    }

    private string GetBaseUrl()
    {
        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        return baseUrl;
    }

    public async Task<bool> CheckSystemOnlineAsync()
    {
        try
        {
            var baseUrl = GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return false;

            // Just ping the versions endpoint which is fast
            var url = $"{baseUrl}cashes/product/versions/";
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            bool isOnline = response.IsSuccessStatusCode;
            SyncStatusChanged?.Invoke(this, isOnline);
            return isOnline;
        }
        catch
        {
            SyncStatusChanged?.Invoke(this, false);
            return false;
        }
    }

    public async Task FullReinitializeAsync()
    {
        try
        {
            Console.WriteLine("[SyncService] FullReinitializeAsync: resetting version to 0");
            await _storageService.SetLastSyncVersionAsync(0);
            await SyncProductsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] Reinitialization error: {ex.Message}");
            Console.WriteLine($"[SyncService] Reinitialization error: {ex.Message}");
        }
    }

    public async Task SyncProductsAsync()
    {
        await _expenseDocumentService.SyncOfflineDocumentsAsync();
        try
        {
            var baseUrl = GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return;

            int lastVersion = await _storageService.GetLastSyncVersionAsync();

            var versionsUrl = $"{baseUrl}cashes/product/versions/";
            Console.WriteLine($"[SyncService] GET {versionsUrl}  (lastVersion={lastVersion})");
            var versionsResponse = await _httpClient.GetAsync(versionsUrl);
            var versionsContent = await versionsResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"[SyncService] GET {versionsUrl} -> {(int)versionsResponse.StatusCode} {versionsResponse.StatusCode}");
            Console.WriteLine($"[SyncService] versions body: {versionsContent}");

            if (versionsResponse.IsSuccessStatusCode)
            {
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
                                    Console.WriteLine($"[SyncService] GET {updateUrl}");
                                    var updateResponse = await _httpClient.GetAsync(updateUrl);
                                    var updateContent = await updateResponse.Content.ReadAsStringAsync();
                                    Console.WriteLine($"[SyncService] GET {updateUrl} -> {(int)updateResponse.StatusCode} {updateResponse.StatusCode}");
                                    Console.WriteLine($"[SyncService] update/{version} body: {updateContent}");

                                    if (updateResponse.IsSuccessStatusCode)
                                    {
                                        using var updateDoc = JsonDocument.Parse(updateContent);
                                        var updateRoot = updateDoc.RootElement;

                                        if (updateRoot.TryGetProperty("status", out var updateStatus) && updateStatus.GetInt32() == 0)
                                        {
                                            // Backend must return an array (possibly empty). A null/missing body
                                            // means "no data delivered" — do NOT advance the version, otherwise
                                            // this version is skipped forever once the backend is fixed.
                                            if (!updateRoot.TryGetProperty("body", out var updateBody) || updateBody.ValueKind != JsonValueKind.Array)
                                            {
                                                Console.WriteLine($"[SyncService] update/{version}: body is null/not an array — backend returned no product data; stopping without advancing version");
                                                break;
                                            }

                                            {
                                                foreach (var item in updateBody.EnumerateArray())
                                                {
                                                    try
                                                    {
                                                        Console.WriteLine($"[SyncService] RAW item: {item.GetRawText()}");
                                                        string productId = Guid.NewGuid().ToString();
                                                        string productName = string.Empty;
                                                        string productSku = string.Empty;
                                                        string productCategory = string.Empty;
                                                        decimal productPrice = 0m;
                                                        string barcode = string.Empty;
                                                        string imagePath = string.Empty;

                                                        if (item.TryGetProperty("id", out var idElem))
                                                            productId = idElem.GetString() ?? productId;

                                                        if (item.TryGetProperty("name", out var nameElem))
                                                            productName = nameElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("article", out var articleElem))
                                                            productSku = articleElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("category", out var catElem))
                                                        {
                                                            if (catElem.ValueKind == JsonValueKind.Object)
                                                            {
                                                                if (catElem.TryGetProperty("id", out var catIdElem))
                                                                    productCategory = catIdElem.GetString() ?? string.Empty;
                                                            }
                                                            else if (catElem.ValueKind == JsonValueKind.String)
                                                            {
                                                                productCategory = catElem.GetString() ?? string.Empty;
                                                            }
                                                        }

                                                        if (item.TryGetProperty("barcode", out var barcodeElem))
                                                            barcode = barcodeElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("sell_price", out var priceElem))
                                                            productPrice = priceElem.ValueKind == JsonValueKind.Number ? priceElem.GetDecimal() : 0m;

                                                        if (item.TryGetProperty("images", out var imagesElem) && imagesElem.ValueKind == JsonValueKind.Array)
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

                                                        if (string.IsNullOrEmpty(imagePath) && item.TryGetProperty("thumb", out var thumbElem) && thumbElem.ValueKind == JsonValueKind.String)
                                                            imagePath = thumbElem.GetString() ?? string.Empty;

                                                        Console.WriteLine($"[SyncService] Product '{productName}' imagePath='{imagePath}' category='{productCategory}'");
                                                        updatedProducts.Add(new Product
                                                        {
                                                            Id = productId,
                                                            Name = productName,
                                                            Sku = productSku,
                                                            Category = productCategory,
                                                            Price = productPrice,
                                                            Barcode = barcode,
                                                            ImagePath = imagePath
                                                        });
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Console.WriteLine($"[SyncService] Error parsing product: {ex.Message}");
                                                    }
                                                }
                                            }

                                            // Processed successfully, commit changes
                                            if (updatedProducts.Count > 0)
                                            {
                                                Console.WriteLine($"[SyncService] Saving {updatedProducts.Count} products for version {version}");
                                                await _storageService.SaveProductsAsync(updatedProducts);
                                            }

                                            // Only advance the version after successful processing
                                            lastVersion = version;
                                            await _storageService.SetLastSyncVersionAsync(lastVersion);
                                            Console.WriteLine($"[SyncService] Version advanced to {lastVersion}");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"[SyncService] update/{version}: backend status != 0, stopping");
                                            // Failed response from backend update API, stop processing
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[SyncService] update/{version}: HTTP error {(int)updateResponse.StatusCode}, stopping");
                                        // Network issue or HTTP error fetching this specific version, stop processing
                                        break;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[SyncService] Version {version} already at or below lastVersion={lastVersion}, skipping");
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"[SyncService] versions endpoint: unexpected status or missing body");
                }
            }
            else
            {
                Console.WriteLine($"[SyncService] versions endpoint HTTP error {(int)versionsResponse.StatusCode}");
            }
            SyncStatusChanged?.Invoke(this, true);
            ProductsSynced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] Sync error: {ex.Message}");
            SyncStatusChanged?.Invoke(this, false);
        }
    }
}
