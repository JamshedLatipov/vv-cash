using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace VvCash.Services.Api;

public class AuthService : IAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public AuthService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<bool> LoginAsync(string email, string password)
    {
        try
        {
            var baseUrl = _settingsService.BackendUrl;
            Console.WriteLine($"[AuthService] Attempting login. BaseUrl configured: '{baseUrl}'");
            Debug.WriteLine($"[AuthService] Attempting login. BaseUrl configured: '{baseUrl}'");

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Console.WriteLine("[AuthService] Error: BackendUrl is null or empty. Ensure it is configured in settings.");
                Debug.WriteLine("[AuthService] Error: BackendUrl is null or empty. Ensure it is configured in settings.");
                return false;
            }

            // Ensure trailing slash for proper relative path combination
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }

            var request = new { email, password };
            var loginUrl = $"{baseUrl}authorization/login/";

            Console.WriteLine($"[AuthService] Sending POST request to: {loginUrl}");
            Console.WriteLine($"[AuthService] Request payload: Email='{email}', Password length={password.Length}");
            Debug.WriteLine($"[AuthService] Sending POST request to: {loginUrl}");

            var response = await _httpClient.PostAsJsonAsync(loginUrl, request);

            Console.WriteLine($"[AuthService] Received response. StatusCode: {response.StatusCode} ({(int)response.StatusCode})");
            Debug.WriteLine($"[AuthService] Received response. StatusCode: {response.StatusCode} ({(int)response.StatusCode})");

            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[AuthService] Response Body: {responseContent}");
            Debug.WriteLine($"[AuthService] Response Body: {responseContent}");

            if (response.IsSuccessStatusCode)
            {
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                // Assuming status 200 means success according to swagger schema
                if (root.TryGetProperty("status", out var statusElement))
                {
                    Console.WriteLine($"[AuthService] Found 'status' property in response: {statusElement.GetInt32()}");
                    if (statusElement.GetInt32() == 200)
                    {
                        Console.WriteLine("[AuthService] Login successful.");
                        return true;
                    }
                }
                else
                {
                    Console.WriteLine("[AuthService] Warning: 'status' property not found in JSON response.");
                }
            }
            else
            {
                Console.WriteLine($"[AuthService] Login failed due to non-success status code: {response.StatusCode}");
            }

            return false;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[AuthService] Network error during login: {ex.Message}");
            Debug.WriteLine($"[AuthService] Network error during login: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[AuthService] Inner Exception: {ex.InnerException.Message}");
            }
            return false;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[AuthService] Error parsing JSON response: {ex.Message}");
            Debug.WriteLine($"[AuthService] Error parsing JSON response: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AuthService] Unexpected error during login: {ex.GetType().Name} - {ex.Message}");
            Console.WriteLine($"[AuthService] StackTrace: {ex.StackTrace}");
            Debug.WriteLine($"[AuthService] Unexpected error during login: {ex.Message}");
            return false;
        }
    }
}
