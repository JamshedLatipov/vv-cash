using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Data;

namespace VvCash.Services.Api;

/// <summary>Loads the roster of sellers assigned to this cash register and caches it
/// locally, so seller switching and capability checks keep working with no network.</summary>
public class SellerRosterService : ISellerRosterService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _storage;

    public SellerRosterService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService storage)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _storage = storage;
    }

    // Deliberately a bare passthrough, per spec ("delegates straight to
    // storage.GetSellersAsync()") and unlike RefreshAsync, this method makes no
    // resilience promise in its contract - it is the on-demand cache read, not a
    // best-effort background refresh. Swallowing a storage failure here would
    // silently turn "the local database is broken" into "this register has no
    // sellers" for every caller, which is a worse outcome than letting the
    // exception surface to whoever is depending on the cache actually being
    // readable. RefreshAsync's own fallback path reads the cache defensively
    // (see SafeGetCachedAsync) precisely because IT must never throw; that
    // requirement does not extend to this method.
    public Task<IEnumerable<SellerInfo>> GetCachedAsync() => _storage.GetSellersAsync();

    public async Task<IEnumerable<SellerInfo>> RefreshAsync()
    {
        Debug.WriteLine("[SellerRosterService] RefreshAsync called.");

        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Debug.WriteLine("[SellerRosterService] BackendUrl is not configured; returning cache.");
            return await SafeGetCachedAsync();
        }

        if (!baseUrl.EndsWith("/"))
            baseUrl += "/";

        try
        {
            var url = $"{baseUrl}cashes/seller/";
            Debug.WriteLine($"[SellerRosterService] GET {url}");
            var response = await _httpClient.GetAsync(url);
            Debug.WriteLine($"[SellerRosterService] Response status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine("[SellerRosterService] Non-success status; returning cache.");
                return await SafeGetCachedAsync();
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("status", out var statusElement) || statusElement.GetInt32() != 0)
            {
                Debug.WriteLine("[SellerRosterService] Envelope status is not 0; returning cache.");
                return await SafeGetCachedAsync();
            }

            if (!root.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.Array)
            {
                Debug.WriteLine("[SellerRosterService] body is missing or not an array; returning cache.");
                return await SafeGetCachedAsync();
            }

            var sellers = bodyElement.Deserialize<List<SellerInfo>>() ?? new List<SellerInfo>();
            await _storage.SaveSellersAsync(sellers);
            return sellers;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] Error refreshing seller roster: {ex.Message}");
            return await SafeGetCachedAsync();
        }
    }

    /// <summary>Reads the cache defensively: this is RefreshAsync's last line of
    /// defence, so it must not itself throw. If even the cache read fails (e.g. a
    /// locked or corrupt local database), an unhandled exception here would be
    /// strictly worse than "nothing changed" - it would propagate into whatever
    /// is calling RefreshAsync (a sync loop, a UI action) instead of degrading
    /// gracefully. Callers of the roster already treat an empty roster as a valid
    /// state (the design falls back to crediting the shift owner), so an empty
    /// enumerable is the correct terminal fallback.</summary>
    private async Task<IEnumerable<SellerInfo>> SafeGetCachedAsync()
    {
        try
        {
            return await _storage.GetSellersAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] Error reading cached seller roster: {ex.Message}");
            return Array.Empty<SellerInfo>();
        }
    }
}
