using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
    public event EventHandler? SessionRevoked;

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

    /// <summary>SyncOfflineDocumentsAsync's loop runs on a background thread (invoked from
    /// PosViewModel's background sync loop, off the UI SynchronizationContext), so this
    /// mirrors NotifyUnsyncedCountChanged's own marshal to the UI thread — a subscriber
    /// (PosViewModel.IsSessionRevoked) mutates UI-bound state and must not be touched from
    /// a background thread.</summary>
    private void NotifySessionRevoked()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SessionRevoked?.Invoke(this, EventArgs.Empty);
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
        var outcome = await CreateExpenseDocumentDetailedAsync(request);
        // Unchanged contract: queued counts as success, so checkout continues offline.
        return outcome.Posted || outcome.Queued;
    }

    public async Task<ExpenseDocumentOutcome> CreateExpenseDocumentDetailedAsync(DocumentRequest request)
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
                    return ExpenseDocumentOutcome.Sent(ReadDocumentNumber(root));
                }
            }

            // The server answered. Whether queueing is the right response depends
            // entirely on WHAT it answered — see IsWorthRetrying.
            if (IsFinalRefusal(response.StatusCode, responseContent))
            {
                Console.WriteLine(
                    $"[ExpenseDocumentService] Server rejected the document ({(int)response.StatusCode}); not queueing.");
                return ExpenseDocumentOutcome.Refused(ReadErrorMessage(responseContent));
            }

            Console.WriteLine("[ExpenseDocumentService] Saving document offline due to a retryable API failure.");
            await SaveOfflineAsync(request);
            return ExpenseDocumentOutcome.Enqueued(); // Still a success so the user can continue checkout locally
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExpenseDocumentService] Error creating expense document, saving offline: {ex.Message}");
            Debug.WriteLine($"[ExpenseDocumentService] Error creating expense document, saving offline: {ex.Message}");
            await SaveOfflineAsync(request);
            return ExpenseDocumentOutcome.Enqueued(); // Queued so checkout can proceed offline
        }
    }

    /// <summary>Whether the server refused this document on its merits, i.e. whether
    /// replaying it later is pointless.
    ///
    /// Two shapes mean the same thing here. A 4xx is the obvious one. The other is this
    /// API's own convention: HTTP 200 carrying a non-zero envelope status — a product
    /// that no longer exists, a shift already closed, a body the serializer rejected.
    /// Both are the server having understood the request and said no, and replaying
    /// either produces the identical answer every time. That is precisely what used to
    /// happen, forever, because the replay loop only ever removes a document on status 0.
    ///
    /// Excluded are the 4xx codes that describe the moment rather than the document:
    /// 401 (the session is dead, but signing in again revives it — the replay loop has
    /// its own handling for that one), 408 and 429 (the server is asking to be asked
    /// again). 5xx likewise: the server broke, the document did not.
    ///
    /// A 2xx whose envelope this cannot read at all is deliberately NOT a refusal.
    /// Nothing was established about the document, and between losing a sale and
    /// retrying one the server may already hold, the retry is the recoverable mistake —
    /// document_hash is what makes the server treat the replay as the same sale.</summary>
    private static bool IsFinalRefusal(HttpStatusCode status, string responseContent)
    {
        if (status is HttpStatusCode.Unauthorized
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests) return false;

        if ((int)status >= 400 && (int)status < 500) return true;
        if ((int)status >= 500) return false;

        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var envelope)
                   && envelope.ValueKind == JsonValueKind.Number
                   && envelope.GetInt32() != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The server's own explanation, for the rejected-document record. Falls
    /// back to the raw body: an unrecognised envelope is still worth keeping verbatim,
    /// since it is all the back office will have to go on.</summary>
    private static string ReadErrorMessage(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not JSON at all (an HTML error page, a proxy banner). Keep it as-is.
        }
        return responseContent;
    }

    /// <summary>Digs the sale's number out of the success envelope
    /// (serializers.DocumentExpenseResult). Absent on older servers, hence the empty
    /// fallback rather than a throw — the number is only ever printed, never acted on.</summary>
    private static string ReadDocumentNumber(JsonElement root)
    {
        if (root.TryGetProperty("body", out var body)
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("document_number", out var number)
            && number.ValueKind == JsonValueKind.String)
        {
            return number.GetString() ?? string.Empty;
        }
        return string.Empty;
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

                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            // The shift session was rejected server-side. Every other
                            // queued document would fail the exact same way, so stop
                            // hammering the server instead of looping through the rest —
                            // break, not continue: this document (and the ones after it)
                            // stay queued untouched, to be retried once the cashier signs
                            // in again. Per design this must never force a logout
                            // mid-receipt, so only a banner is raised here.
                            Console.WriteLine($"[ExpenseDocumentService] Sync got 401 Unauthorized on document {doc.Key} — shift session revoked, stopping sync.");
                            NotifySessionRevoked();
                            break;
                        }

                        var responseContent = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            using var jsonDoc = JsonDocument.Parse(responseContent);
                            var root = jsonDoc.RootElement;
                            if (root.TryGetProperty("status", out var statusElement) && statusElement.GetInt32() == 0)
                            {
                                await _offlineStorageService.DeleteUnsyncedDocumentAsync(doc.Key);
                                anySuccess = true;
                                Console.WriteLine($"[ExpenseDocumentService] Successfully synced document {doc.Key}");
                                continue;
                            }
                        }

                        // Not a 401 (handled above) and not a success: if replaying it
                        // cannot ever work, take it out of the rotation rather than
                        // carrying it to the end of time. Unlike the 401 branch this
                        // does NOT stop the loop — a document the server refuses says
                        // nothing about the ones queued behind it.
                        if (IsFinalRefusal(response.StatusCode, responseContent))
                        {
                            var reason = ReadErrorMessage(responseContent);
                            Console.WriteLine(
                                $"[ExpenseDocumentService] Server rejected queued document {doc.Key} ({(int)response.StatusCode}): {reason}. Taking it out of the retry rotation.");
                            await _offlineStorageService.MarkDocumentRejectedAsync(doc.Key, reason);
                            anySuccess = true; // the queue did shrink; the badge must follow
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
