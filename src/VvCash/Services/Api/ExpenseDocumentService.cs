using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services.Data;

namespace VvCash.Services.Api;

public class ExpenseDocumentService : IExpenseDocumentService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IOfflineStorageService _offlineStorageService;

    public event EventHandler<int>? UnsyncedDocumentsCountChanged;

    public ExpenseDocumentService(HttpClient httpClient, ISettingsService settingsService, IOfflineStorageService offlineStorageService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _offlineStorageService = offlineStorageService;
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

    public async Task<int> GetUnsyncedDocumentsCountAsync()
    {
        var docs = await _offlineStorageService.GetUnsyncedDocumentsAsync();
        return docs.Count();
    }

    private void NotifyUnsyncedCountChanged(int count)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UnsyncedDocumentsCountChanged?.Invoke(this, count);
        });
    }

    private async Task SaveOfflineAsync(DocumentRequest request)
    {
        var payload = JsonSerializer.Serialize(request);
        await _offlineStorageService.SaveUnsyncedDocumentAsync(request.DocumentHash, payload);
        var count = await GetUnsyncedDocumentsCountAsync();
        NotifyUnsyncedCountChanged(count);
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

            // If we get here, the API returned a non-success status or the status property wasn't 0
            Console.WriteLine("[ExpenseDocumentService] Saving document offline due to API failure status.");
            await SaveOfflineAsync(request);
            return true; // Still return true so the user can continue checkout locally
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExpenseDocumentService] Error creating expense document, saving offline: {ex.Message}");
            Debug.WriteLine($"[ExpenseDocumentService] Error creating expense document, saving offline: {ex.Message}");
            await SaveOfflineAsync(request);
            return true; // Return true to allow checkout to proceed offline
        }
    }

    public async Task SyncOfflineDocumentsAsync()
    {
        Console.WriteLine("[ExpenseDocumentService] SyncOfflineDocumentsAsync called.");
        Debug.WriteLine("[ExpenseDocumentService] SyncOfflineDocumentsAsync called.");

        try
        {
            var docs = await _offlineStorageService.GetUnsyncedDocumentsAsync();
            var docList = docs.ToList();
            if (!docList.Any()) return;

            var url = $"{GetBaseUrl()}documents/expense/create/";
            bool anySuccess = false;

            foreach (var doc in docList)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<DocumentRequest>(doc.Value);
                    if (request != null)
                    {
                        var response = await _httpClient.PostAsJsonAsync(url, request);
                        if (response.IsSuccessStatusCode)
                        {
                            var responseContent = await response.Content.ReadAsStringAsync();
                            using var jsonDoc = JsonDocument.Parse(responseContent);
                            var root = jsonDoc.RootElement;
                            if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                            {
                                await _offlineStorageService.DeleteUnsyncedDocumentAsync(doc.Key);
                                anySuccess = true;
                                Console.WriteLine($"[ExpenseDocumentService] Successfully synced document {doc.Key}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ExpenseDocumentService] Failed to sync document {doc.Key}: {ex.Message}");
                }
            }

            if (anySuccess)
            {
                var count = await GetUnsyncedDocumentsCountAsync();
                NotifyUnsyncedCountChanged(count);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExpenseDocumentService] Error during SyncOfflineDocumentsAsync: {ex.Message}");
        }
    }
}
