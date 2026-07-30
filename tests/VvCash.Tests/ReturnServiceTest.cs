using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class ReturnServiceTest
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
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public event System.EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private static ReturnService Build(StubHttpMessageHandler handler)
        => new ReturnService(new HttpClient(handler), new FakeSettings());

    [Fact]
    public async Task GetSalesAsync_ParsesAndHitsPageParam()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"body":[{"id":"x","document_number":"9","to_pay":100}],"page_count":2,"total_items":15,"item_per_page":10}"""));
        var svc = Build(handler);

        var res = await svc.GetSalesAsync(2);

        Assert.Equal(2, res.PageCount);
        Assert.Equal("x", res.Body[0].Id);
        Assert.Contains("documents/expense/?page=2", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetReturnableLinesAsync_ReturnsBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"success","body":{"id":"d","details":[{"product":{"id":"p"},"quantity":2,"quantity_returned":0,"after_discount":50}]},"status":0}"""));
        var svc = Build(handler);

        var body = await svc.GetReturnableLinesAsync("doc1");

        Assert.Equal("d", body.Id);
        Assert.Single(body.Details);
        Assert.Contains("documents/return/doc1/", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateReturnAsync_TrueOnStatusZero()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"success","body":{},"status":0}"""));
        var svc = Build(handler);

        var ok = await svc.CreateReturnAsync("doc1", new ReturnRequest { SelectedDate = "2026-06-06" });

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("documents/return/doc1/", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateReturnAsync_FalseOnNonZeroStatus()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"message":"error","body":"nope","status":-1}"""));
        var svc = Build(handler);

        var ok = await svc.CreateReturnAsync("doc1", new ReturnRequest { SelectedDate = "2026-06-06" });

        Assert.False(ok);
    }
}
