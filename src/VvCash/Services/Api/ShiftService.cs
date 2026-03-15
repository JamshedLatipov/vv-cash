using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VvCash.Services.Api;

public class ShiftService : IShiftService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ShiftService(HttpClient httpClient, ISettingsService settingsService)
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

    public async Task<bool> OpenShiftAsync()
    {
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/open/";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShiftService] Error opening shift: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CloseShiftAsync()
    {
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/close/";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShiftService] Error closing shift: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> GetShiftStateAsync()
    {
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/state/";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.TryGetProperty("is_active", out var isActiveElement))
                    {
                        return isActiveElement.GetBoolean();
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShiftService] Error getting shift state: {ex.Message}");
            return false;
        }
    }
}
