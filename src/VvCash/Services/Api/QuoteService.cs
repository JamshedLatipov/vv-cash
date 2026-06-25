using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public class QuoteService : IQuoteService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public QuoteService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    private string GetBaseUrl()
    {
        var baseUrl = _settingsService.BackendUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        return baseUrl;
    }

    public async Task<QuoteResult?> QuoteAsync(QuoteRequest request, CancellationToken ct)
    {
        try
        {
            var baseUrl = GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return null;

            var resp = await _httpClient.PostAsJsonAsync($"{baseUrl}discounts/quote/", request, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var content = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Tolerant to {status, body, message} envelope: unwrap only when
            // body is an object; a null/array body falls through to root and a
            // non-QuoteResult shape yields null rather than relying on a thrown
            // exception being swallowed below.
            var target = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("body", out var body)
                && body.ValueKind == JsonValueKind.Object
                ? body
                : root;

            if (target.ValueKind != JsonValueKind.Object) return null;

            return JsonSerializer.Deserialize<QuoteResult>(target.GetRawText());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
