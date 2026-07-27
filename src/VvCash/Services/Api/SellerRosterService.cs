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

    public Task<IEnumerable<SellerInfo>> GetCachedAsync() => _storage.GetSellersAsync();

    public async Task<IEnumerable<SellerInfo>> RefreshAsync()
    {
        Debug.WriteLine("[SellerRosterService] RefreshAsync called.");

        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Debug.WriteLine("[SellerRosterService] BackendUrl is not configured; returning cache.");
            return await _storage.GetSellersAsync();
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
                return await _storage.GetSellersAsync();
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("status", out var statusElement) || statusElement.GetInt32() != 0)
            {
                Debug.WriteLine("[SellerRosterService] Envelope status is not 0; returning cache.");
                return await _storage.GetSellersAsync();
            }

            if (!root.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.Array)
            {
                Debug.WriteLine("[SellerRosterService] body is missing or not an array; returning cache.");
                return await _storage.GetSellersAsync();
            }

            var sellers = bodyElement.Deserialize<List<SellerInfo>>() ?? new List<SellerInfo>();
            await _storage.SaveSellersAsync(sellers);
            return sellers;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SellerRosterService] Error refreshing seller roster: {ex.Message}");
            return await _storage.GetSellersAsync();
        }
    }
}
