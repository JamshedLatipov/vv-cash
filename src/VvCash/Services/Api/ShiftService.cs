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

    public event EventHandler? SessionRevoked;

    public ShiftService(HttpClient httpClient, ISettingsService settingsService, ISessionContext session)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _session = session;
    }

    /// <summary>Mirrors ExpenseDocumentService.NotifySessionRevoked: both call sites below
    /// currently only ever run on the UI thread (OpenShiftAsync from a UI-triggered command,
    /// GetShiftStateAsync from PosViewModel's constructor-kicked InitializeAsync — see that
    /// class's own remarks on why that's still the UI thread), but posting rather than
    /// invoking directly keeps this safe if a future caller ever awaits either method from a
    /// background thread, and matches the one existing precedent for this event shape.</summary>
    private void NotifySessionRevoked()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SessionRevoked?.Invoke(this, EventArgs.Empty);
        });
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

    /// <summary>Extracts the cash id from the same untyped cash-session body. The
    /// server serialises it as a flat "cash" string; "cash_id" and a nested object are
    /// accepted for the same reason ExtractWarehouseId accepts three shapes — this
    /// client outlives individual server versions.</summary>
    public static string? ExtractCashId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;

        if (body.TryGetProperty("cash_id", out var cid) && cid.ValueKind == JsonValueKind.String)
            return cid.GetString();

        if (body.TryGetProperty("cash", out var c))
        {
            if (c.ValueKind == JsonValueKind.String) return c.GetString();
            if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("id", out var nid) && nid.ValueKind == JsonValueKind.String)
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

            // A response actually arrived, so this is the server saying the token is dead —
            // not the network being unreachable (that path never gets here; it throws and
            // lands in the catch below, which deliberately does NOT raise SessionRevoked).
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Console.WriteLine("[ShiftService] OpenShiftAsync got 401 Unauthorized — session revoked.");
                Debug.WriteLine("[ShiftService] OpenShiftAsync got 401 Unauthorized — session revoked.");
                NotifySessionRevoked();
                return null;
            }

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
                        var cash = ExtractCashId(bodyElement);
                        if (!string.IsNullOrEmpty(cash)) _session.CashId = cash;
                        return idElement.GetString();
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            // Network unreachable, DNS failure, timeout, etc. — the server was never asked,
            // so there is nothing to conclude about the session's validity. Deliberately does
            // NOT raise SessionRevoked: doing so here would log a cashier out of a register
            // that simply has no signal, which offline operation must never do.
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
                            var cash = ExtractCashId(bodyElement);
                            if (!string.IsNullOrEmpty(cash)) _session.CashId = cash;
                            return idElement.GetString();
                        }
                    }
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Same distinction as OpenShiftAsync: a response arrived, so this is the
                // server rejecting the token, not the network being unreachable.
                Console.WriteLine("[ShiftService] GetShiftStateAsync got 401 Unauthorized — session revoked.");
                Debug.WriteLine("[ShiftService] GetShiftStateAsync got 401 Unauthorized — session revoked.");
                NotifySessionRevoked();
            }
            return null;
        }
        catch (Exception ex)
        {
            // Network unreachable, DNS failure, timeout, etc. — see OpenShiftAsync's own
            // remarks on why this must never raise SessionRevoked.
            Debug.WriteLine($"[ShiftService] Error getting shift state: {ex.Message}");
            return null;
        }
    }
}
