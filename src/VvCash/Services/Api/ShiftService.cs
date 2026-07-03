using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http.Json;
using VvCash.Services;

namespace VvCash.Services.Api;

public class ShiftService : IShiftService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly ISessionContext _session;

    public ShiftService(HttpClient httpClient, ISettingsService settingsService, ISessionContext session)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _session = session;
    }

    /// <summary>Extracts warehouse id from the untyped cash-session body.
    /// Tries "warehouse_id", then "warehouse" (flat string or nested object with "id").</summary>
    public static string? ExtractWarehouseId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;

        if (body.TryGetProperty("warehouse_id", out var wid) && wid.ValueKind == JsonValueKind.String)
            return wid.GetString();

        if (body.TryGetProperty("warehouse", out var w))
        {
            if (w.ValueKind == JsonValueKind.String) return w.GetString();
            if (w.ValueKind == JsonValueKind.Object && w.TryGetProperty("id", out var nid) && nid.ValueKind == JsonValueKind.String)
                return nid.GetString();
        }
        return null;
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

    public async Task<string?> OpenShiftAsync()
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
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[ShiftService] Response content: {responseContent}");
            Debug.WriteLine($"[ShiftService] Response content: {responseContent}");

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                {
                    if (root.TryGetProperty("body", out var bodyElement) && bodyElement.TryGetProperty("id", out var idElement))
                    {
                        var wh = ExtractWarehouseId(bodyElement);
                        if (!string.IsNullOrEmpty(wh)) _session.WarehouseId = wh;
                        return idElement.GetString();
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShiftService] Error opening shift: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> CloseShiftAsync(string shiftId)
    {
        Console.WriteLine($"[ShiftService] CloseShiftAsync called with {shiftId}.");
        Debug.WriteLine($"[ShiftService] CloseShiftAsync called with {shiftId}.");
        try
        {
            var url = $"{GetBaseUrl()}cashes/shift/close/";
            Console.WriteLine($"[ShiftService] POST to {url}");
            Debug.WriteLine($"[ShiftService] POST to {url}");
            url = $"{url}?shift={shiftId}";
            Console.WriteLine($"[ShiftService] Final POST url: {url}");
            var response = await _httpClient.PostAsync(url, null);

            Console.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            Debug.WriteLine($"[ShiftService] Response status: {response.StatusCode}");
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[ShiftService] Response content: {responseContent}");
            Debug.WriteLine($"[ShiftService] Response content: {responseContent}");

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
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

    public async Task<string?> GetShiftStateAsync()
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
                        if (bodyElement.ValueKind == JsonValueKind.Null) return null;
                        if (bodyElement.TryGetProperty("id", out var idElement))
                        {
                            var wh = ExtractWarehouseId(bodyElement);
                            if (!string.IsNullOrEmpty(wh)) _session.WarehouseId = wh;
                            return idElement.GetString();
                        }
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShiftService] Error getting shift state: {ex.Message}");
            return null;
        }
    }
}
