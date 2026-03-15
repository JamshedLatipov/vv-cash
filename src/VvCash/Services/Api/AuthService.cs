using System;
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
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                // Fallback or handle error. According to user request we take it from settings.
                // Assuming settings must be configured before login.
                return false;
            }

            // Ensure trailing slash for proper relative path combination, or format correctly
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }

            var request = new { email, password };
            var loginUrl = $"{baseUrl}authorization/login/";
            var response = await _httpClient.PostAsJsonAsync(loginUrl, request);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                // Assuming status 200 means success according to swagger schema
                if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 200)
                {
                    // For a proper implementation, we'd want to extract and save the JWT tokens from the body
                    // var body = root.GetProperty("body");
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            // Log exception here
            return false;
        }
    }
}
