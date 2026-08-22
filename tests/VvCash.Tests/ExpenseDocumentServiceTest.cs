using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

/// <summary>Covers Task 22's 401 handling in SyncOfflineDocumentsAsync directly against the
/// real ExpenseDocumentService (via StubHttpMessageHandler, the same fake SyncServiceTest.cs
/// already uses for this class's sibling), rather than only through PosViewModel's fake —
/// that fake's SyncOfflineDocumentsAsync is a no-op, so it can prove PosViewModel reacts
/// correctly to the event but not that the service itself actually stops the loop and leaves
/// queued documents untouched. This file proves that half.</summary>
public class ExpenseDocumentServiceTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Backed by an ordered list (not a Dictionary) specifically so
    /// GetUnsyncedDocumentsAsync hands back documents in a deterministic, known order —
    /// the tests below depend on which document the stub HTTP responder sees first.</summary>
    private sealed class FakeStorage : IOfflineStorageService
    {
        private readonly List<KeyValuePair<string, string>> _docs;
        public List<string> DeletedKeys { get; } = new();
        public List<string> SavedKeys { get; } = new();
        public List<(string Hash, string Reason)> RejectedKeys { get; } = new();

        public FakeStorage(IEnumerable<KeyValuePair<string, string>> docs) => _docs = docs.ToList();

        public Task<IEnumerable<KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync()
            => Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(
                _docs.Where(d => !DeletedKeys.Contains(d.Key)
                                 && RejectedKeys.All(r => r.Hash != d.Key)).ToList());

        public Task DeleteUnsyncedDocumentAsync(string hash)
        {
            DeletedKeys.Add(hash);
            return Task.CompletedTask;
        }

        public Task MarkDocumentRejectedAsync(string hash, string reason)
        {
            RejectedKeys.Add((hash, reason));
            return Task.CompletedTask;
        }

        public Task SaveUnsyncedDocumentAsync(string hash, string payload)
        {
            SavedKeys.Add(hash);
            _docs.Add(new KeyValuePair<string, string>(hash, payload));
            return Task.CompletedTask;
        }

        public Task SaveProductsAsync(IEnumerable<Product> products) => Task.CompletedTask;
        public Task<IEnumerable<Product>> GetAllProductsAsync() => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task<Product?> GetProductByBarcodeAsync(string barcode) => Task.FromResult<Product?>(null);
        public Task<IEnumerable<Product>> SearchProductsAsync(string query) => Task.FromResult<IEnumerable<Product>>(Array.Empty<Product>());
        public Task SaveCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());
        public Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories) => Task.CompletedTask;
        public Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync() => Task.FromResult<IEnumerable<Category>>(Array.Empty<Category>());
        public Task SavePromotionsAsync(IEnumerable<Promotion> promotions) => Task.CompletedTask;
        public Task<IEnumerable<Promotion>> GetPromotionsAsync() => Task.FromResult<IEnumerable<Promotion>>(Array.Empty<Promotion>());
        public Task ClearPromotionsAsync() => Task.CompletedTask;
        public Task SaveMoneyPolicyAsync(MoneyPolicy policy) => Task.CompletedTask;
        public Task<MoneyPolicy> GetMoneyPolicyAsync() => Task.FromResult(MoneyPolicy.Default);
        public Task SaveCashFeaturesAsync(CashFeatures features) => Task.CompletedTask;
        public Task<CashFeatures> GetCashFeaturesAsync() => Task.FromResult(CashFeatures.Default);
        public Task SetLastSyncVersionAsync(int version) => Task.CompletedTask;
        public Task<int> GetLastSyncVersionAsync() => Task.FromResult(0);
        public Task ClearCategoriesAsync() => Task.CompletedTask;
        public Task ClearProductsAsync() => Task.CompletedTask;
        public Task SaveParkedSaleAsync(ParkedSale sale) => Task.CompletedTask;
        public Task<IEnumerable<ParkedSale>> GetParkedSalesAsync() => Task.FromResult<IEnumerable<ParkedSale>>(Array.Empty<ParkedSale>());
        public Task<ParkedSale?> GetParkedSaleAsync(string id) => Task.FromResult<ParkedSale?>(null);
        public Task DeleteParkedSaleAsync(string id) => Task.CompletedTask;
        public Task SaveSellersAsync(IEnumerable<SellerInfo> sellers) => Task.CompletedTask;
        public Task<IEnumerable<SellerInfo>> GetSellersAsync() => Task.FromResult<IEnumerable<SellerInfo>>(Array.Empty<SellerInfo>());
        public Task InitializeAsync() => Task.CompletedTask;
    }

    private static string Payload(string hash) =>
        JsonSerializer.Serialize(new DocumentRequest { DocumentHash = hash, ShiftId = "shift-1" });

    private static DocumentRequest Request(string hash) =>
        new() { DocumentHash = hash, ShiftId = "shift-1" };

    [Fact]
    public async Task Create_ServerRejectsOnTheMerits_IsNotQueuedAndIsReportedAsFailed()
    {
        // HTTP 200 with a non-zero envelope status is the server saying "I understood
        // this and I will not take it" (a product that no longer exists, a closed shift).
        // Queueing that told the cashier the sale went through and left the document
        // retrying forever, since the replay path only ever deletes on status 0.
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"invalid request","status":1}"""));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.False(ok);
        Assert.Empty(storage.SavedKeys);
    }

    [Fact]
    public async Task Create_ServerAnswers400_IsNotQueuedAndIsReportedAsFailed()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.BadRequest, """{"message":"bad request","status":1}"""));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.False(ok);
        Assert.Empty(storage.SavedKeys);
    }

    [Fact]
    public async Task Create_ForbiddenFromTheAuthMiddleware_IsQueuedNotDropped()
    {
        // This backend answers 403 — not 401 — for an expired or invalid bearer token:
        // middlewares/site_authentication.go calls redirectToAccessDenied, which writes
        // {"status":"error","message":"forbidden"}. Treating that as a refusal on the
        // merits throws away a sale the cashier has already taken money for, and tells
        // them a retry is pointless when signing in again is the actual fix.
        //
        // Note the shape: "status" here is the STRING "error". The application's own
        // refusals carry a NUMERIC status (-1, see response.Response in the backend), and
        // that is what tells the two apart.
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.Forbidden, """{"status":"error","message":"forbidden"}"""));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.True(ok);
        Assert.Equal(new[] { "doc1" }, storage.SavedKeys);
    }

    [Fact]
    public async Task Create_PaymentRequiredFromBilling_IsQueuedNotDropped()
    {
        // middlewares/billing_access.go answers 402 when the billing lookup itself errors.
        // Nothing about the document is established by that.
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.PaymentRequired, """{"status":"error","message":"billing"}"""));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.True(ok);
        Assert.Equal(new[] { "doc1" }, storage.SavedKeys);
    }

    [Fact]
    public async Task Create_BadRequestWithoutTheApiEnvelope_IsQueuedNotDropped()
    {
        // A 4xx that is not this API's own envelope did not come from the application
        // layer — a proxy, a gateway, an HTML error page. Nothing was established about
        // the document, so it must survive.
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.BadRequest, "<html><body>Bad Request</body></html>"));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.True(ok);
        Assert.Equal(new[] { "doc1" }, storage.SavedKeys);
    }

    [Fact]
    public async Task Sync_ForbiddenOnFirstDocument_StopsLoop_LeavesEverythingQueued_RaisesSessionRevoked()
    {
        // The 403 is what a dead session actually looks like against this backend, so it
        // has to behave exactly as the 401 branch does: stop, keep everything, raise the
        // banner. Marking the queue rejected here would take an entire offline stretch of
        // already-paid sales out of the rotation within one sync interval.
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return (HttpStatusCode.Forbidden, """{"status":"error","message":"forbidden"}""");
        });
        var storage = new FakeStorage(new[]
        {
            new KeyValuePair<string, string>("doc1", Payload("doc1")),
            new KeyValuePair<string, string>("doc2", Payload("doc2")),
        });
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var sessionRevokedCount = 0;
        svc.SessionRevoked += (s, e) => sessionRevokedCount++;

        await svc.SyncOfflineDocumentsAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, requestCount);
        Assert.Empty(storage.DeletedKeys);
        Assert.Empty(storage.RejectedKeys);
        Assert.Equal(1, sessionRevokedCount);
        Assert.Equal(2, (await storage.GetUnsyncedDocumentsAsync()).Count());
    }

    [Fact]
    public async Task Create_NetworkUnreachable_IsQueuedAndCheckoutContinues()
    {
        // The case the offline queue exists for. Nothing was learned about the document
        // itself, so it must survive to be replayed.
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("no route to host"));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.True(ok);
        Assert.Equal(new[] { "doc1" }, storage.SavedKeys);
    }

    [Fact]
    public async Task Create_ServerAnswers500_IsQueuedBecauseTheServerMayRecover()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.InternalServerError, """{"message":"boom","status":1}"""));
        var storage = new FakeStorage(Array.Empty<KeyValuePair<string, string>>());
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var ok = await svc.CreateExpenseDocumentAsync(Request("doc1"));

        Assert.True(ok);
        Assert.Equal(new[] { "doc1" }, storage.SavedKeys);
    }

    [Fact]
    public async Task Sync_ServerRejectsAQueuedDocumentOnTheMerits_ItLeavesTheRotationAndTheRestStillGoOut()
    {
        var seenHashes = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(body);
            var hash = doc.RootElement.GetProperty("document_hash").GetString()!;
            seenHashes.Add(hash);

            return hash == "doc1"
                ? (HttpStatusCode.OK, """{"message":"invalid request","status":1}""")
                : (HttpStatusCode.OK, """{"message":"success","status":0}""");
        });
        var storage = new FakeStorage(new[]
        {
            new KeyValuePair<string, string>("doc1", Payload("doc1")),
            new KeyValuePair<string, string>("doc2", Payload("doc2")),
        });
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        await svc.SyncOfflineDocumentsAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // A rejection is not a reason to stop the loop the way a 401 is — it says
        // nothing about the documents behind it.
        Assert.Equal(new[] { "doc1", "doc2" }, seenHashes);
        Assert.Equal("doc1", Assert.Single(storage.RejectedKeys).Hash);
        // Kept, not deleted: it is still the only record of what the register booked.
        Assert.Equal(new[] { "doc2" }, storage.DeletedKeys);
        // And it is genuinely out of the rotation now.
        Assert.Empty(await storage.GetUnsyncedDocumentsAsync());
    }

    [Fact]
    public async Task SyncOfflineDocumentsAsync_401OnFirstDocument_StopsLoop_LeavesBothDocumentsQueued_RaisesSessionRevoked()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            requestCount++;
            return (HttpStatusCode.Unauthorized, """{"message":"unauthorized","status":1}""");
        });
        var storage = new FakeStorage(new[]
        {
            new KeyValuePair<string, string>("doc1", Payload("doc1")),
            new KeyValuePair<string, string>("doc2", Payload("doc2")),
        });
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var sessionRevokedCount = 0;
        svc.SessionRevoked += (s, e) => sessionRevokedCount++;
        var unsyncedCountChangedCount = 0;
        svc.UnsyncedDocumentsCountChanged += (s, count) => unsyncedCountChangedCount++;

        await svc.SyncOfflineDocumentsAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The loop must genuinely stop, not just skip the failing document: only one HTTP
        // call happened even though two documents were queued.
        Assert.Equal(1, requestCount);
        Assert.Empty(storage.DeletedKeys); // both documents survive untouched
        Assert.Equal(1, sessionRevokedCount);
        // anySuccess never went true (the very first attempt hit 401), so the count-changed
        // notification — which would tell the UI the queue shrank — must not fire either.
        Assert.Equal(0, unsyncedCountChangedCount);
    }

    [Fact]
    public async Task SyncOfflineDocumentsAsync_401OnSecondDocument_FirstStaysSynced_ThirdNeverAttempted()
    {
        var seenHashes = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(body);
            var hash = doc.RootElement.GetProperty("document_hash").GetString()!;
            seenHashes.Add(hash);

            if (hash == "doc1")
                return (HttpStatusCode.OK, """{"message":"success","status":0}""");
            if (hash == "doc2")
                return (HttpStatusCode.Unauthorized, """{"message":"unauthorized","status":1}""");

            throw new InvalidOperationException($"doc3 must never be attempted, but got a request for {hash}");
        });
        var storage = new FakeStorage(new[]
        {
            new KeyValuePair<string, string>("doc1", Payload("doc1")),
            new KeyValuePair<string, string>("doc2", Payload("doc2")),
            new KeyValuePair<string, string>("doc3", Payload("doc3")),
        });
        var svc = new ExpenseDocumentService(new HttpClient(handler), new FakeSettings(), storage);

        var sessionRevokedCount = 0;
        svc.SessionRevoked += (s, e) => sessionRevokedCount++;
        var unsyncedCountChangedCount = 0;
        svc.UnsyncedDocumentsCountChanged += (s, count) => unsyncedCountChangedCount++;

        await svc.SyncOfflineDocumentsAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "doc1", "doc2" }, seenHashes); // doc3's request never went out
        Assert.Equal(new[] { "doc1" }, storage.DeletedKeys); // doc1's own success is not undone
        Assert.Equal(1, sessionRevokedCount);
        // doc1 did succeed before the 401, so the queue genuinely shrank by one — the UI
        // must still hear about that.
        Assert.Equal(1, unsyncedCountChangedCount);
    }
}
