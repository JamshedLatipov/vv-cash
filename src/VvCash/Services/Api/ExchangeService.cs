using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task<ExchangeOutcome> CreateExchangeAsync(string expenseDocumentId, ExchangeRequest request)
    {
        try
        {
            var url = $"{GetBaseUrl()}documents/exchange/{expenseDocumentId}/";
            var response = await _httpClient.PostAsJsonAsync(url, request);

            // The status is checked before the body is parsed, and a refusal is read
            // as text: this endpoint writes its reason as a bare JSON string rather
            // than the usual envelope, so parsing it as one throws — which used to
            // make an expired window, an already-processed exchange and a dead
            // network all look identical to the cashier.
            if (!response.IsSuccessStatusCode)
                return new ExchangeOutcome
                {
                    StatusCode = (int)response.StatusCode,
                    Message = ReadErrorMessage(await response.Content.ReadAsStringAsync()),
                };

            var res = await response.Content.ReadFromJsonAsync<ExchangeResponse>();
            if (res?.Body == null || res.Status != 0)
                return new ExchangeOutcome { StatusCode = (int)response.StatusCode, Message = res?.Message };

            return new ExchangeOutcome { Body = res.Body, StatusCode = (int)response.StatusCode };
        }
        catch (Exception ex)
        {
            // Network unreachable, DNS failure, timeout, etc. There is no offline
            // fallback for an exchange (see class remarks), so this must not throw —
            // and it leaves StatusCode null, the one case where the server may never
            // have seen the request at all.
            Debug.WriteLine($"[ExchangeService] Error creating exchange: {ex.Message}");
            return new ExchangeOutcome();
        }
    }

    /// <summary>Pulls the human-readable reason out of a refusal body. The exchange
    /// endpoint writes a bare JSON string; the shared middleware (an expired token,
    /// say) writes the usual envelope instead, so both shapes are handled and
    /// anything else is passed through verbatim rather than being dropped.</summary>
    private static string? ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString();
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                return message.GetString();
        }
        catch (JsonException)
        {
            // Not JSON at all (a proxy's HTML error page, for instance).
        }
        return body.Trim();
    }
}
