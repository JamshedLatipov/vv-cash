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
        Console.WriteLine("[ShiftService] OpenShiftAsync called.");
        Debug.WriteLine("[ShiftService] OpenShiftAsync called.");
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/open/";
            Console.WriteLine($"[ShiftService] POST to {url}");
            Debug.WriteLine($"[ShiftService] POST to {url}");
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var response = await _httpClient.SendAsync(request);

            Console.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            Debug.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ShiftService] Response content: {responseContent}");
                Debug.WriteLine($"[ShiftService] Response content: {responseContent}");
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
        Console.WriteLine("[ShiftService] CloseShiftAsync called.");
        Debug.WriteLine("[ShiftService] CloseShiftAsync called.");
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/close/";
            Console.WriteLine($"[ShiftService] POST to {url}");
            Debug.WriteLine($"[ShiftService] POST to {url}");
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var response = await _httpClient.SendAsync(request);

            Console.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            Debug.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ShiftService] Response content: {responseContent}");
                Debug.WriteLine($"[ShiftService] Response content: {responseContent}");
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
        Console.WriteLine("[ShiftService] GetShiftStateAsync called.");
        Debug.WriteLine("[ShiftService] GetShiftStateAsync called.");
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/state/";
            Console.WriteLine($"[ShiftService] GET to {url}");
            Debug.WriteLine($"[ShiftService] GET to {url}");
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request);

            Console.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            Debug.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ShiftService] Response content: {responseContent}");
                Debug.WriteLine($"[ShiftService] Response content: {responseContent}");
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement))
                    {
                        if (bodyElement.ValueKind == JsonValueKind.Null) return false;
                        if (bodyElement.TryGetProperty("id", out var idElement))
                        {
                            return !string.IsNullOrEmpty(idElement.GetString());
                        }
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
