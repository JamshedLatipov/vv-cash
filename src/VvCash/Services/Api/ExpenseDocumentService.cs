using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public class ExpenseDocumentService : IExpenseDocumentService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ExpenseDocumentService(HttpClient httpClient, ISettingsService settingsService)
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

    public async Task<bool> CreateExpenseDocumentAsync(DocumentRequest request)
    {
        Console.WriteLine("[ExpenseDocumentService] CreateExpenseDocumentAsync called.");
        Debug.WriteLine("[ExpenseDocumentService] CreateExpenseDocumentAsync called.");
        try
        {
            var url = $"{GetBaseUrl()}documents/expense/create/";
            Console.WriteLine($"[ExpenseDocumentService] POST to {url}");
            Debug.WriteLine($"[ExpenseDocumentService] POST to {url}");

            var response = await _httpClient.PostAsJsonAsync(url, request);

            Console.WriteLine($"[ExpenseDocumentService] Response status: {response.StatusCode}");
            Debug.WriteLine($"[ExpenseDocumentService] Response status: {response.StatusCode}");

            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[ExpenseDocumentService] Response content: {responseContent}");
            Debug.WriteLine($"[ExpenseDocumentService] Response content: {responseContent}");

            if (response.IsSuccessStatusCode)
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
            Console.WriteLine($"[ExpenseDocumentService] Error creating expense document: {ex.Message}");
            Debug.WriteLine($"[ExpenseDocumentService] Error creating expense document: {ex.Message}");
            return false;
        }
    }
}
