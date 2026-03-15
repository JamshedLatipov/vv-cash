using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace VvCash.Services.Api;

public class AuthService : IAuthService
{
    private readonly HttpClient _httpClient;

    public AuthService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<bool> LoginAsync(string email, string password)
    {
        try
        {
            var request = new { email, password };
            var response = await _httpClient.PostAsJsonAsync("http://market.proffi.io/authorization/login/", request);

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
