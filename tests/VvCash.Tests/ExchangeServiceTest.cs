using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class ExchangeServiceTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public System.DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public System.Collections.Generic.List<VvCash.Models.PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public event System.EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private static ExchangeService Build(StubHttpMessageHandler handler)
        => new ExchangeService(new HttpClient(handler), new FakeSettings());

    [Fact]
    public async Task CreateExchangeAsync_PostsToTheExchangeEndpoint()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"status":0,"body":{"difference":50,"return_document_number":"7","expense_document_number":"8"}}"""));
        var svc = Build(handler);

        var outcome = await svc.CreateExchangeAsync("doc1", new ExchangeRequest { SelectedDate = "2026-06-06" });

        Assert.NotNull(outcome.Body);
        Assert.Equal(50m, outcome.Body!.Difference);
        Assert.Equal("7", outcome.Body.ReturnDocumentNumber);
        Assert.Equal("8", outcome.Body.ExpenseDocumentNumber);
        Assert.Contains("documents/exchange/doc1/", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateExchangeAsync_WindowExpired_SurfacesTheServersOwnReason()
    {
        // Exactly the shape this endpoint refuses with: a bare JSON string, not the
        // envelope the success path uses. Parsing the body before checking the status
        // throws on it, which is what used to turn every refusal into a nameless
        // failure the cashier could not act on.
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.BadRequest, "\"exchange period of 14 days has expired for this sale\""));
        var svc = Build(handler);

        var outcome = await svc.CreateExchangeAsync("doc1", new ExchangeRequest { SelectedDate = "2026-06-06" });

        // The goods are already in the customer's hands by the time this call
        // returns, and nothing offline can undo that. A rejection must therefore
        // never read as success — no body is the only safe result.
        Assert.Null(outcome.Body);
        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal("exchange period of 14 days has expired for this sale", outcome.Message);
    }

    [Fact]
    public async Task CreateExchangeAsync_AlreadyProcessed_IsTellableApartFromADeadNetwork()
    {
        // 409 means the exchange is already booked — pressing submit again would only
        // be refused again. A transport failure is the opposite: nothing may have
        // reached the server at all. The two must never look the same.
        var duplicate = await Build(new StubHttpMessageHandler(_ =>
                (HttpStatusCode.Conflict, "\"duplicate document: this exchange was already processed\"")))
            .CreateExchangeAsync("doc1", new ExchangeRequest());

        Assert.Null(duplicate.Body);
        Assert.Equal(409, duplicate.StatusCode);
        Assert.Contains("already processed", duplicate.Message);
    }

    [Fact]
    public async Task CreateExchangeAsync_ServerUnreachable_HasNoBodyAndNoStatus()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var svc = Build(handler);

        var outcome = await svc.CreateExchangeAsync("doc1", new ExchangeRequest { SelectedDate = "2026-06-06" });

        Assert.Null(outcome.Body);
        Assert.Null(outcome.StatusCode); // nothing answered, so nothing to report but silence
        Assert.Null(outcome.Message);
    }
}
