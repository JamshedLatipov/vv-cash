using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services.Api;

namespace VvCash.Services.Data;

public interface ISyncService
{
    event EventHandler<bool>? SyncStatusChanged;
    event EventHandler? ProductsSynced;
    Task SyncProductsAsync();
    Task FullReinitializeAsync();
    Task<bool> CheckSystemOnlineAsync();

    /// <summary>Every stock line for this register's warehouse, or null when the walk
    /// did not complete. Null is not "empty": it means the caller must change nothing.</summary>
    Task<IReadOnlyDictionary<string, decimal>?> FetchAllRemainsAsync();
}

public class SyncService : ISyncService
{
    public event EventHandler<bool>? SyncStatusChanged;
    public event EventHandler? ProductsSynced;

    /// <summary>Служба шаблона чека — синглтон, который держит разобранную
    /// раскладку в памяти и отдаёт её принтеру на каждую печать.
    ///
    /// Необязательная: у теста, строящего SyncService напрямую, её нет, и кэш в
    /// SQLite всё равно обновляется — не обновится только снимок в памяти, а в
    /// тесте его никто не читает.</summary>
    private readonly IReceiptTemplateService? _receiptTemplates;

    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storageService;

    private readonly IExpenseDocumentService _expenseDocumentService;

    public SyncService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storageService, IExpenseDocumentService expenseDocumentService, IReceiptTemplateService? receiptTemplates = null)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storageService = storageService;
        _expenseDocumentService = expenseDocumentService;
        _receiptTemplates = receiptTemplates;
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

            if (versionsResponse.IsSuccessStatusCode)
            {
                using var jsonDoc = JsonDocument.Parse(versionsContent);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Array)
                    {
                        // Sorted, and not taken in the order the endpoint happened to
                        // list them. The walk below compares each version against a
                        // lastVersion that moves as it goes and persists it, so one
                        // out-of-order entry was permanent data loss: handed [3,1,2] it
                        // processed 3, wrote lastVersion=3, then skipped 1 and 2 as
                        // "already done" — on that sync and on every sync afterwards.
                        // The products in those versions never reached the register.
                        var versions = bodyElement.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.Number)
                            .Select(v => v.GetInt32())
                            .OrderBy(v => v)
                            .ToList();

                        foreach (var version in versions)
                        {
                            {
                                if (version > lastVersion)
                                {
                                    var updatedProducts = new List<Product>();
                                    var updateUrl = $"{baseUrl}cashes/product/update/{version}/";
                                    Console.WriteLine($"[SyncService] GET {updateUrl}");
                                    var updateResponse = await _httpClient.GetAsync(updateUrl);
                                    var updateContent = await updateResponse.Content.ReadAsStringAsync();
                                    Console.WriteLine($"[SyncService] GET {updateUrl} -> {(int)updateResponse.StatusCode} {updateResponse.StatusCode}");

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
            await SyncReceiptTemplateAsync(baseUrl);

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

    /// <summary>Забирает шаблон чека и логотип из конфига кассы. Любой отказ
    /// оставляет закэшированное: потеря эндпоинта не должна откатывать магазин
    /// на дефолтный чек, а отсутствие опции — нормальное состояние тенанта, где
    /// миграция ещё не прогнана. Пустое значение опции — тоже: см. развёрнутый
    /// довод у нормализации внутри.
    ///
    /// internal, а не private: SyncServiceTest вызывает её напрямую —
    /// прогонять ради неё весь SyncAsync с товарами и остатками значит проверять
    /// не то.</summary>
    internal async Task SyncReceiptTemplateAsync(string baseUrl)
    {
        try
        {
            var url = $"{baseUrl}cashes/config/get/";
            Console.WriteLine($"[SyncService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] receipt template: HTTP {(int)response.StatusCode}, keeping cache");
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 0)
            {
                Console.WriteLine("[SyncService] receipt template: backend status != 0, keeping cache");
                return;
            }
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("[SyncService] receipt template: no body, keeping cache");
                return;
            }

            var rawTemplate = FindOptionValue(body, "receipt_template");
            var rawLogo = FindOptionValue(body, "receipt_logo");

            // Пустое значение — это НЕ настроенный шаблон, и кэш им затирать
            // нельзя. Бэкенд отдаёт опцию через LEFT JOIN с COALESCE(c.val, '')
            // (cashes/config_serializers.go), то есть после миграции опция
            // приезжает КАЖДОЙ кассе, а касса, которой шаблон в бэкофисе не
            // сохраняли, получает "" — неотличимо от «сервер прислал пустоту» на
            // ровном месте. Сохранять её означает уничтожить рабочий шаблон,
            // который уже лежит в SQLite, из-за конфига, который про эту кассу
            // ничего не знает; ReceiptTemplate.Parse("") даёт Default, и касса
            // печатает раскладку по умолчанию.
            //
            // Цена решения: администратор, стерший значение в бэкофисе НАМЕРЕННО,
            // больше не вернёт кассу на встроенную раскладку — касса продолжит
            // печатать последний непустой шаблон. Пустая строка на проводе не
            // отличается от «значения нет вовсе» (COALESCE их схлопывает), так что
            // одно из двух поведений выбрать всё равно придётся, и «не терять
            // настроенный чек» дороже: молча испорченный чек в бою — это отказ на
            // кассе, а откат к дефолту делается сохранением обычного шаблона.
            // Отличать их можно только на бэкенде, отдавая null там, где строки в
            // configs нет вовсе.
            var template = string.IsNullOrWhiteSpace(rawTemplate) ? null : rawTemplate;
            var logo = string.IsNullOrWhiteSpace(rawLogo) ? null : rawLogo;

            if (template != null) await _storageService.SaveReceiptTemplateAsync(template);

            if (logo != null) await _storageService.SaveReceiptLogoAsync(logo);

            // Перечитать снимок в памяти здесь же, а не только через
            // ProductsSynced. Сохранение выше кладёт шаблон в SQLite, но печатает
            // касса из снимка, который ReceiptTemplateService держит в памяти, и
            // до этой строки его обновлял единственный подписчик события —
            // PosViewModel. Он transient: если синхронизация завершилась, когда
            // живого экрана продажи нет (логин, другой экран, пересозданная
            // модель), снимок оставался тем, что прочитан на старте, и касса
            // печатала раскладку по умолчанию до перезапуска приложения — с
            // кэшем, где уже лежал нужный шаблон.
            if (template != null || logo != null)
            {
                if (_receiptTemplates != null) await _receiptTemplates.RefreshAsync();
            }

            Console.WriteLine(
                $"[SyncService] receipt template: {DescribeOptionValue(rawTemplate)}, " +
                $"logo: {DescribeOptionValue(rawLogo)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] receipt template sync error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Три РАЗНЫХ состояния одной строки лога, которые до этого
    /// сливались в одно слово "cached". Разбор реального обращения: лог кассы
    /// говорил "receipt template: cached" на каждой синхронизации, а печатался
    /// дефолтный чек — потому что "cached" писалось и для пустого значения, и
    /// единственным способом отличить одно от другого было лезть в SQLite
    /// магазина. Слово "empty" стоило бы того часа само по себе.
    ///
    /// Длина, а не первые символы значения: шаблон — это конфиг магазина, ему не
    /// место в логе целиком, а длины хватает, чтобы отличить приехавший шаблон от
    /// обрезанного и увидеть, что после сохранения в бэкофисе он реально
    /// изменился.</summary>
    private static string DescribeOptionValue(string? raw) => raw switch
    {
        null => "absent",
        _ when string.IsNullOrWhiteSpace(raw) => "empty, keeping cache",
        _ => $"cached, {raw.Length} chars",
    };

    /// <summary>Ищет значение опции по коду. Опция, засеянная до 20260728000800,
    /// приезжает с code = "" — сегодня таких два десятка, — но отдельной проверки
    /// на пустую строку здесь нет: сравнение по строгому равенству само отсекает
    /// её, потому что обе вызывающие стороны просят только непустой код
    /// ("receipt_template", "receipt_logo"), и "" с ним никогда не совпадёт.
    /// Раньше явная проверка string.IsNullOrEmpty(value) стояла "на всякий
    /// случай"; ревью Task 10 нашло её мёртвой мутационным тестированием —
    /// отключение проверки не красило ни один тест ни на одном достижимом
    /// входе — и она была убрана вместе с тестом, который её не проверял, а
    /// лишь дублировал сценарий "опция отсутствует".</summary>
    private static string? FindOptionValue(JsonElement groups, string code)
    {
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("code", out var c) || c.GetString() != code) continue;

                return option.TryGetProperty("value", out var v) ? v.GetString() : null;
            }
        }
        return null;
    }

    // Internal rather than private only so
    // FetchAllRemainsAsync_PageCountKeepsGrowing_AbandonsTheWalk can assert against this
    // number directly instead of duplicating it; it is not part of the public API.
    //
    // A server that grows page_count on every response would otherwise keep the walk
    // below running forever. Following a growing count is deliberate — a catalogue can
    // legitimately gain a page mid-walk — but it needs an end. Two hundred pages at two
    // hundred rows is forty thousand products, comfortably past any real catalogue, and
    // reaching it means the count is not to be trusted rather than that the warehouse is
    // enormous.
    internal const int MaxPages = 200;

    /// <summary>NOT CALLED BY ANYTHING. This does not work against the deployed backend,
    /// and that is deliberate: GET /cashes/remain/ never serialises product_id —
    /// cashes/cash_repo.go:152 tags it `json:"-"` — so ProductId is empty on every row,
    /// the guard below drops every row, and this returns an empty map. The "id" the
    /// endpoint does send is the remains row id, not a product id; there is no working
    /// substitute for product_id on the wire today. SyncServiceTest is not evidence this
    /// works: its fixtures set product_id because they were written from the same wrong
    /// assumption this method was, before anyone checked the struct tag. Activating this
    /// needs the backend tag changed to `json:"product_id"` (purely additive — nothing
    /// reads that field yet) and then re-verification against a live endpoint, not
    /// against these fixtures, since the fixtures cannot catch this class of mistake by
    /// construction.
    ///
    /// Two more things for whoever activates this, found in review but left unfixed here
    /// because fixing them has no value while nothing calls this method:
    /// - GetStockRemains has no ORDER BY under its LIMIT/OFFSET, and remains rows are
    ///   updated by every sale on every register while a walk runs. A row can cross a
    ///   page boundary mid-walk and be silently skipped — the walk still *completes*, so
    ///   the null-on-incomplete-walk contract cannot see the gap, and Task 3's
    ///   ApplyRemainsAsync would then delete that product. TotalItems below is the only
    ///   client-side signal for this and is currently parsed but unused. A naive
    ///   equality check against it would still be wrong: total_items counts remains rows
    ///   with no join, while the row query inner-joins products, so the walk legitimately
    ///   collects fewer rows whenever a remains row has no matching product.
    /// - page_size=200 below equals maxPageSize in the backend's settings/config.go:13;
    ///   the server clamps anything larger. The MaxPages arithmetic (200 pages, forty
    ///   thousand products) depends on that page size — change one without the other and
    ///   the ceiling no longer means what its comment says.
    ///
    /// Walks GET /cashes/remain/ page by page and returns product id to quantity for the
    /// whole warehouse.
    ///
    /// Returns null on any incomplete walk — a non-2xx, an unparseable page, a transport
    /// failure, being offline. The caller deletes every product this map does not
    /// mention, so a half-finished walk is not a smaller answer, it is a wrong one. An
    /// empty-but-non-null map is a real, distinct outcome the caller must intercept
    /// before calling ApplyRemainsAsync, which throws on an empty map by design rather
    /// than delete the whole catalogue.
    ///
    /// This endpoint answers with response.List — {body, page_count, total_items,
    /// item_per_page} — and carries no "status" field, unlike the rest of the cash API.
    /// page_count is what ends the loop.</summary>
    public async Task<IReadOnlyDictionary<string, decimal>?> FetchAllRemainsAsync()
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
        {
            Console.WriteLine("[SyncService] remain walk: no backend URL configured; walk abandoned");
            return null;
        }

        var collected = new Dictionary<string, decimal>();
        var page = 1;

        try
        {
            var pageCount = 1;

            while (page <= pageCount)
            {
                if (page > MaxPages)
                {
                    Console.WriteLine($"[SyncService] remain walk exceeded {MaxPages} pages; walk abandoned");
                    return null;
                }

                var url = $"{baseUrl}cashes/remain/?page={page}&page_size=200";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[SyncService] remain page {page} -> {(int)response.StatusCode}; walk abandoned");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var parsed = JsonSerializer.Deserialize<CashRemainPage>(content);
                if (parsed == null)
                {
                    Console.WriteLine($"[SyncService] remain page {page} did not parse; walk abandoned");
                    return null;
                }

                foreach (var item in parsed.Body ?? Enumerable.Empty<CashRemainItem>())
                    if (!string.IsNullOrEmpty(item.ProductId))
                        collected[item.ProductId] = item.Quantity;

                // Read on every page rather than once: a page count that shrinks mid-walk
                // still terminates, and one that grows is followed.
                pageCount = parsed.PageCount > 0 ? parsed.PageCount : 1;
                page++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] remain walk failed on page {page}: {ex.Message}");
            return null;
        }

        return collected;
    }
}
