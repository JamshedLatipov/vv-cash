using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

/// <summary>Reads GET /documents/payment/categories/. Needs the
/// <c>documents.PaymentCategoryList</c> permission on the register's role.</summary>
public class PaymentCategoryService : IPaymentCategoryService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public PaymentCategoryService(HttpClient httpClient, ISettingsService settingsService)
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

    public async Task<List<PaymentCategory>> GetPaymentCategoriesAsync()
    {
        try
        {
            var url = $"{GetBaseUrl()}documents/payment/categories/";
            var res = await _httpClient.GetFromJsonAsync<PaymentCategoryListResponse>(url);
            if (res == null || res.Status != 0 || res.Body == null)
                return new List<PaymentCategory>();
            return res.Body;
        }
        catch (Exception ex)
        {
            // The settings screen opens from the login screen, i.e. possibly with no
            // backend configured yet and certainly with no guarantee of a network.
            // An empty list leaves the dropdown empty; it must not stop the screen.
            Debug.WriteLine($"[PaymentCategoryService] Error loading payment categories: {ex.Message}");
            return new List<PaymentCategory>();
        }
    }
}
