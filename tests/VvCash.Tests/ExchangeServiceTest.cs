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

        var body = await svc.CreateExchangeAsync("doc1", new ExchangeRequest { SelectedDate = "2026-06-06" });

        Assert.NotNull(body);
        Assert.Equal(50m, body!.Difference);
        Assert.Equal("7", body.ReturnDocumentNumber);
        Assert.Equal("8", body.ExpenseDocumentNumber);
        Assert.Contains("documents/exchange/doc1/", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateExchangeAsync_ReturnsNullOnServerRejection()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.BadRequest, """{"status":1,"message":"exchange window of 14 days has expired for this sale"}"""));
        var svc = Build(handler);

        var body = await svc.CreateExchangeAsync("doc1", new ExchangeRequest { SelectedDate = "2026-06-06" });

        // The goods are already in the customer's hands by the time this call
        // returns, and nothing offline can undo that. A rejection must therefore
        // never read as success — null is the only safe result.
        Assert.Null(body);
    }

    [Fact]
    public async Task CreateExchangeAsync_ReturnsNullWhenTheServerIsUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var svc = Build(handler);

        var body = await svc.CreateExchangeAsync("doc1", new ExchangeRequest { SelectedDate = "2026-06-06" });

        Assert.Null(body);
    }
}
