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
        var allResults = new List<CounterpartyResponse>();
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
                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var result = JsonSerializer.Deserialize<List<CounterpartyResponse>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (result != null)
                    {
                        return result;
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("status", out var statusProp) && statusProp.GetInt32() == 0)
                    {
                        if (root.TryGetProperty("body", out var bodyElement))
                        {
                            var result = JsonSerializer.Deserialize<List<CounterpartyResponse>>(bodyElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (result != null)
                            {
                                return result;
                            }
                        }
                    }
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[CounterpartyService] API returned error: {response.StatusCode} - {errorContent}");
            }

            return allResults;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CounterpartyService] Error searching counterparties: {ex.Message}");
        }
        return null;
    }
}
