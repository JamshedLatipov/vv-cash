using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

/// <summary>Posts POST /documents/money/expense/create/. Needs the
/// <c>documents.MoneyExpenseCreate</c> permission, and the signed-in user must be a
/// registered seller of the cash named in the body — the server answers 403 otherwise.
///
/// There is deliberately no offline queue here: a payout the server never sees is
/// money out of the drawer that nothing accounts for, and unlike a sale there is no
/// replay path that would ever reconcile it.</summary>
public class CashOperationService : ICashOperationService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public CashOperationService(HttpClient httpClient, ISettingsService settingsService)
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

    public async Task<CashOpOutcome> CreateCashExpenseAsync(CashExpenseRequest request)
    {
        try
        {
            var url = $"{GetBaseUrl()}documents/money/expense/create/";
            var response = await _httpClient.PostAsJsonAsync(url, request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return CashOpOutcome.Failed(ReadMessage(content) ?? $"HTTP {(int)response.StatusCode}");

            // A 200 with a non-zero status is still a refusal — the envelope carries
            // the verdict, the HTTP code only says the request was understood.
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Number
                && status.TryGetInt32(out var statusValue)
                && statusValue == 0)
            {
                return CashOpOutcome.Ok();
            }

            return CashOpOutcome.Failed(ReadMessage(content));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CashOperationService] Error creating cash expense: {ex.Message}");
            return CashOpOutcome.Failed(ex.Message);
        }
    }

    /// <summary>Pulls the human-readable reason out of a refusal body. The shared
    /// middleware writes the usual envelope; a binding failure writes a bare string;
    /// a proxy may write neither, in which case the body is passed through as-is.</summary>
    private static string? ReadMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // "body" carries the detail for BadRequestString; "message" is the
                // envelope's own generic word ("error") when it does.
                if (doc.RootElement.TryGetProperty("body", out var detail)
                    && detail.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(detail.GetString()))
                    return detail.GetString();
                if (doc.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                    return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON at all (a proxy's HTML error page, for instance).
        }
        return body.Trim();
    }
}
