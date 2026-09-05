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
    public event EventHandler<DocumentRejection>? DocumentRejected;

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

    /// <summary>Same UI-thread marshal as the two notifiers above, for the same reason:
    /// SyncOfflineDocumentsAsync runs off the UI SynchronizationContext and the subscriber
    /// (PosViewModel) raises a modal from this.</summary>
    private void NotifyDocumentRejected(string hash, string reason)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DocumentRejected?.Invoke(this, new DocumentRejection(hash, reason));
        });
    }

    /// <summary>The optimistic checkout path — see the interface for why it exists.
    /// Deliberately has no HTTP call at all, not even an attempted one: a call that could
    /// hang is exactly what this is here to keep off the cashier's path. Pushing it to
    /// the server is <see cref="SyncOfflineDocumentsAsync"/>'s job, kicked by the caller
    /// once the receipt is out.</summary>
    public async Task<ExpenseDocumentOutcome> QueueExpenseDocumentAsync(DocumentRequest request)
    {
        await SaveOfflineAsync(request);
        return ExpenseDocumentOutcome.Enqueued();
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
            // entirely on WHAT it answered — see IsFinalRefusal.
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
    /// Excluded are the codes that describe the moment rather than the document: 408 and
    /// 429 (the server is asking to be asked again), and 401/403 — see below. 5xx
    /// likewise: the server broke, the document did not.
    ///
    /// 403 is the important one, and this client had it backwards. It is what an expired
    /// or invalid bearer token produces here: middlewares/site_authentication.go calls
    /// redirectToAccessDenied, and this API emits 401 only from the login and refresh
    /// endpoints — never from an authenticated route. The same 403 also comes out of a
    /// tenant-database blip in getCashFromToken, and 402 out of a billing lookup that
    /// errored. None of those says anything about the document.
    ///
    /// Which is why the HTTP class alone is not enough to conclude anything, and the
    /// envelope decides. The application's own refusals are response.Response, whose
    /// status is an int (-1 for an error, 0 for success). The middleware's are
    /// gin.H{"status": "error"} — a STRING. Requiring a NUMERIC non-zero status is
    /// therefore exactly the line between "the application considered this document and
    /// refused it" and "something in front of the application turned the request away".
    ///
    /// Everything else — a 4xx that is not this envelope at all (a proxy, a gateway, an
    /// HTML error page), or a 2xx whose body cannot be read — is deliberately NOT a
    /// refusal. Nothing was established about the document, and between losing a sale
    /// and retrying one the server may already hold, the retry is the recoverable
    /// mistake: document_hash is what makes the server treat the replay as the same sale.</summary>
    private static bool IsFinalRefusal(HttpStatusCode status, string responseContent)
    {
        if (status is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.PaymentRequired
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests) return false;

        if ((int)status >= 500) return false;

        return CarriesRefusalEnvelope(responseContent);
    }

    /// <summary>Whether the body is this API's own error envelope: an object whose
    /// "status" is a number other than zero. A string "status" is the middleware's shape,
    /// not the application's — see IsFinalRefusal.</summary>
    private static bool CarriesRefusalEnvelope(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var envelope)
                   && envelope.ValueKind == JsonValueKind.Number
                   && envelope.TryGetInt32(out var code)
                   && code != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The status codes that mean this register's session is no longer accepted.
    /// 403 is the one this backend actually sends (see IsFinalRefusal); 401 is kept
    /// because the login and refresh endpoints do use it and a future server change
    /// might extend that.</summary>
    private static bool IsSessionRejected(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

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

                        if (IsSessionRejected(response.StatusCode))
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
                            // Marking it is what stops the retrying; telling somebody is
                            // what stops it being lost. With checkout no longer waiting
                            // for the server, this event is the only moment a refused
                            // sale is ever mentioned to the person who took the money.
                            NotifyDocumentRejected(doc.Key, reason);
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
