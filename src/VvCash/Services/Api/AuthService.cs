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

    public async Task<bool> LoginAsync(string email, string password, bool rememberMe)
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

            // Neither the credentials going out nor the body coming back is logged: the
            // request carries the cashier's password and the response carries the bearer
            // token this register authenticates every later call with. A POS terminal's
            // console output is not a private place — it is redirected to a file on some
            // installs and read over the shoulder on all of them. The status code is
            // enough to diagnose a failed login.
            Console.WriteLine($"[AuthService] Sending POST request to: {loginUrl}");
            Debug.WriteLine($"[AuthService] Sending POST request to: {loginUrl}");

            var response = await _httpClient.PostAsJsonAsync(loginUrl, request);

            Console.WriteLine($"[AuthService] Received response. StatusCode: {response.StatusCode} ({(int)response.StatusCode})");
            Debug.WriteLine($"[AuthService] Received response. StatusCode: {response.StatusCode} ({(int)response.StatusCode})");

            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                // Assuming status 200 means success according to swagger schema
                if (root.TryGetProperty("status", out var statusElement))
                {
                    Console.WriteLine($"[AuthService] Envelope status: {statusElement.GetInt32()}");
                    if (statusElement.GetInt32() == 0)
                    {
                        // The token is what makes this a login. A success envelope that
                        // carries none is malformed, and reporting it as signed in put
                        // the register on the POS screen with an empty AuthToken: every
                        // later call went out with no Authorization header and came back
                        // 401, which reads as "the server keeps revoking my session"
                        // rather than as the failed login it actually is.
                        var token = root.TryGetProperty("access_token", out var authTokenElement)
                            ? authTokenElement.GetString()
                            : null;

                        if (string.IsNullOrWhiteSpace(token))
                        {
                            Console.WriteLine("[AuthService] Login rejected: success envelope carried no access_token.");
                            return false;
                        }

                        Console.WriteLine("[AuthService] Login successful.");

                        _settingsService.AuthToken = token;

                        // The token's real lifetime is the shift now, not a fixed
                        // duration: PosViewModel wipes it on a successful shift close
                        // (see DoCloseShiftAsync), so nothing here needs to expire it
                        // mid-shift. AuthTokenExpiresAt exists purely to answer one
                        // question the next time the app launches — should the register
                        // skip the login screen and resume straight in? — which is
                        // exactly what "remember me" means. rememberMe == false stamps
                        // null so that check always fails and a fresh login is required;
                        // rememberMe == true stamps MaxShiftHours out as a backstop
                        // against a register whose shift never actually gets closed
                        // (crash, power loss, forgotten till) staying auto-authenticated
                        // forever — not an expiry a cashier should ever actually hit.
                        _settingsService.AuthTokenExpiresAt = rememberMe
                            ? DateTime.UtcNow.AddHours(Constants.AuthConstants.MaxShiftHours)
                            : null;
                        _settingsService.Save();

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

    public void ClearSession()
    {
        _settingsService.AuthToken = string.Empty;
        _settingsService.AuthTokenExpiresAt = null;
        _settingsService.Save();
    }
}
