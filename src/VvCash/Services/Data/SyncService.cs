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
                                            // A null/missing body is a legitimate empty version: the backend
                                            // serializes "no products in this version for this cash's warehouse"
                                            // as null. Advance past it and keep walking, otherwise one empty
                                            // version blocks all later ones.
                                            if (!updateRoot.TryGetProperty("body", out var updateBody) || updateBody.ValueKind != JsonValueKind.Array)
                                            {
                                                Console.WriteLine($"[SyncService] update/{version}: empty body — nothing for this cash in this version; advancing");
                                                lastVersion = version;
                                                await _storageService.SetLastSyncVersionAsync(lastVersion);
                                                continue;
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

                                                        var tagIds = new List<string>();
                                                        if (item.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
                                                        {
                                                            foreach (var tag in tagsElem.EnumerateArray())
                                                            {
                                                                if (tag.ValueKind != JsonValueKind.String) continue;
                                                                var tagId = tag.GetString();
                                                                if (!string.IsNullOrEmpty(tagId)) tagIds.Add(tagId);
                                                            }
                                                        }

                                                        // Secondary unit. Every key is optional: a piece-only product
                                                        // carries none of them, and a backend older than the units
                                                        // module sends none at all. Both read as "sold by the piece".
                                                        var unitId = string.Empty;
                                                        var unitCode = string.Empty;
                                                        var unitShortName = string.Empty;
                                                        var unitFactor = 0m;
                                                        var isDivisible = false;
                                                        var sellInSecondaryUnit = false;

                                                        if (item.TryGetProperty("unit_id", out var unitIdElem) && unitIdElem.ValueKind == JsonValueKind.String)
                                                            unitId = unitIdElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("unit_code", out var unitCodeElem) && unitCodeElem.ValueKind == JsonValueKind.String)
                                                            unitCode = unitCodeElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("unit_short_name", out var unitShortElem) && unitShortElem.ValueKind == JsonValueKind.String)
                                                            unitShortName = unitShortElem.GetString() ?? string.Empty;

                                                        // GetDecimal, not GetDouble: the factor ends up in the snapshot
                                                        // the server re-checks against its tolerance.
                                                        if (item.TryGetProperty("unit_factor", out var unitFactorElem) && unitFactorElem.ValueKind == JsonValueKind.Number)
                                                            unitFactor = unitFactorElem.GetDecimal();

                                                        if (item.TryGetProperty("is_divisible", out var divisibleElem) &&
                                                            (divisibleElem.ValueKind == JsonValueKind.True || divisibleElem.ValueKind == JsonValueKind.False))
                                                            isDivisible = divisibleElem.GetBoolean();

                                                        if (item.TryGetProperty("sell_in_secondary_unit", out var sellInUnitElem) &&
                                                            (sellInUnitElem.ValueKind == JsonValueKind.True || sellInUnitElem.ValueKind == JsonValueKind.False))
                                                            sellInSecondaryUnit = sellInUnitElem.GetBoolean();

                                                        Console.WriteLine($"[SyncService] Product '{productName}' imagePath='{imagePath}' category='{productCategory}'");
                                                        updatedProducts.Add(new Product
                                                        {
                                                            Id = productId,
                                                            Name = productName,
                                                            Sku = productSku,
                                                            Category = productCategory,
                                                            Price = productPrice,
                                                            Barcode = barcode,
                                                            ImagePath = imagePath,
                                                            TagIds = tagIds,
                                                            UnitId = unitId,
                                                            UnitCode = unitCode,
                                                            UnitShortName = unitShortName,
                                                            UnitFactor = unitFactor,
                                                            IsDivisible = isDivisible,
                                                            SellInSecondaryUnit = sellInSecondaryUnit
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
            await SyncPromotionsAsync(baseUrl);
            await SyncMoneyPolicyAsync(baseUrl);
            await SyncFeaturesAsync(baseUrl);

            SyncStatusChanged?.Invoke(this, true);
            ProductsSynced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] Sync error: {ex.Message}");
            SyncStatusChanged?.Invoke(this, false);
        }
    }

    /// <summary>Refreshes the offline promotion cache. Unlike products there is no
    /// version walk: the set is small and the endpoint always returns it whole, so
    /// the cache is replaced outright. A failure here leaves the previous cache in
    /// place — stale promotions price a cart better than none at all — and never
    /// fails the product sync that just succeeded.</summary>
    private async Task SyncPromotionsAsync(string baseUrl)
    {
        try
        {
            var url = $"{baseUrl}cashes/promotion/";
            Console.WriteLine($"[SyncService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] promotions: HTTP {(int)response.StatusCode}, keeping cached set");
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 0)
            {
                Console.WriteLine("[SyncService] promotions: backend status != 0, keeping cached set");
                return;
            }

            // A null body is a legitimate "no promotions configured" and must clear
            // the cache, otherwise a deleted campaign keeps discounting forever.
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
            {
                await _storageService.SavePromotionsAsync(new List<Promotion>());
                Console.WriteLine("[SyncService] promotions: empty body, cache cleared");
                return;
            }

            var promotions = JsonSerializer.Deserialize<List<Promotion>>(body.GetRawText()) ?? new List<Promotion>();
            await _storageService.SavePromotionsAsync(promotions);
            Console.WriteLine($"[SyncService] promotions: cached {promotions.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] promotions sync error: {ex.Message}");
        }
    }

    /// <summary>Refreshes the cached money rounding policy. Any failure keeps the
    /// previously cached value (or the default): rounding with a stale policy is a
    /// last-minor-unit difference, while having none would be a hard stop.</summary>
    private async Task SyncMoneyPolicyAsync(string baseUrl)
    {
        try
        {
            var url = $"{baseUrl}cashes/money/";
            Console.WriteLine($"[SyncService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] money policy: HTTP {(int)response.StatusCode}, keeping cached value");
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 0)
            {
                Console.WriteLine("[SyncService] money policy: backend status != 0, keeping cached value");
                return;
            }
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("[SyncService] money policy: no body, keeping cached value");
                return;
            }

            var policy = JsonSerializer.Deserialize<MoneyPolicy>(body.GetRawText());
            if (policy == null) return;

            await _storageService.SaveMoneyPolicyAsync(policy);
            Console.WriteLine($"[SyncService] money policy: scale={policy.Scale} mode={policy.Mode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] money policy sync error: {ex.Message}");
        }
    }

    /// <summary>Refreshes the cached feature-flag map. Any failure keeps the
    /// previously cached map: losing the endpoint must not silently switch a
    /// function back on that the store deliberately switched off, nor switch one
    /// off that it left on.
    ///
    /// Unlike promotions there is no "empty body means clear the cache" branch to
    /// get right, because an empty map and a missing map already mean the same
    /// thing — every function enabled. An empty object is still saved rather than
    /// ignored, so that switching the last flag back on actually reaches the
    /// register.</summary>
    private async Task SyncFeaturesAsync(string baseUrl)
    {
        try
        {
            var url = $"{baseUrl}cashes/features/";
            Console.WriteLine($"[SyncService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] features: HTTP {(int)response.StatusCode}, keeping cached map");
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 0)
            {
                Console.WriteLine("[SyncService] features: backend status != 0, keeping cached map");
                return;
            }
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("[SyncService] features: no body, keeping cached map");
                return;
            }

            var flags = JsonSerializer.Deserialize<Dictionary<string, bool>>(body.GetRawText())
                        ?? new Dictionary<string, bool>();

            await _storageService.SaveCashFeaturesAsync(new CashFeatures { Flags = flags });
            Console.WriteLine($"[SyncService] features: cached {flags.Count} flag(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] features sync error: {ex.Message}");
        }
    }
}
