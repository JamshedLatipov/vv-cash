using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public class CounterpartyService : ICounterpartyService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public CounterpartyService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request)
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var url = $"{baseUrl}users/counterparty/";

            var jsonContent = JsonContent.Create(request, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            var response = await _httpClient.PostAsync(url, jsonContent);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                // Assuming status == 0 means success
                if (root.TryGetProperty("status", out var statusProp) && statusProp.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement))
                    {
                        var counterparty = JsonSerializer.Deserialize<CounterpartyResponse>(bodyElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return counterparty;
                    }
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[CounterpartyService] API returned error: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CounterpartyService] Error creating counterparty: {ex.Message}");
        }

        return null;
    }

    public async Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query)
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var url = $"{baseUrl}cashes/counterparty/?q={Uri.EscapeDataString(query)}";

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<CounterpartySearchResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return result?.Body;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[CounterpartyService] API returned error: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CounterpartyService] Error searching counterparties: {ex.Message}");
        }
        return null;
    }
}
