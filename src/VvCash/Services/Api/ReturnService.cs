using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public class ReturnService : IReturnService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ReturnService(HttpClient httpClient, ISettingsService settingsService)
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

    public async Task<ExpenseListResponse> GetSalesAsync(int page = 1)
    {
        var url = $"{GetBaseUrl()}documents/expense/?page={page}";
        var res = await _httpClient.GetFromJsonAsync<ExpenseListResponse>(url);
        return res ?? new ExpenseListResponse();
    }

    public async Task<ReturnDetailBody> GetReturnableLinesAsync(string expenseId)
    {
        var url = $"{GetBaseUrl()}documents/return/{expenseId}/";
        var res = await _httpClient.GetFromJsonAsync<ReturnDetailResponse>(url);
        if (res == null || res.Status != 0 || res.Body == null)
            throw new InvalidOperationException(res?.Message ?? "Failed to load returnable lines.");
        return res.Body;
    }

    public async Task<bool> CreateReturnAsync(string expenseId, ReturnRequest request)
    {
        var url = $"{GetBaseUrl()}documents/return/{expenseId}/";
        var response = await _httpClient.PostAsJsonAsync(url, request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return false;
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.TryGetProperty("status", out var s) && s.GetInt32() == 0;
    }
}
