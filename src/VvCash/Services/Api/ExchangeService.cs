using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

/// <summary>Exchanges are online-only, deliberately: unlike a sale, an exchange
/// that never reaches the server means goods handed over that nothing will ever
/// write off. Nothing here falls back to the offline queue.</summary>
public class ExchangeService : IExchangeService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ExchangeService(HttpClient httpClient, ISettingsService settingsService)
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

    public async Task<ExchangeResponseBody?> CreateExchangeAsync(string expenseDocumentId, ExchangeRequest request)
    {
        try
        {
            var url = $"{GetBaseUrl()}documents/exchange/{expenseDocumentId}/";
            var response = await _httpClient.PostAsJsonAsync(url, request);
            var res = await response.Content.ReadFromJsonAsync<ExchangeResponse>();
            if (!response.IsSuccessStatusCode || res == null || res.Status != 0)
                return null;
            return res.Body;
        }
        catch (Exception ex)
        {
            // Network unreachable, DNS failure, timeout, etc. There is no offline
            // fallback for an exchange (see class remarks), so any failure here
            // must surface as null rather than throw.
            Debug.WriteLine($"[ExchangeService] Error creating exchange: {ex.Message}");
            return null;
        }
    }
}
