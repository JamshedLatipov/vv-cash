using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class QuoteServiceTest
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
        public string PhoneFormatId { get; set; } = string.Empty;
        public event System.EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private static QuoteService Build(StubHttpMessageHandler h)
        => new QuoteService(new HttpClient(h), new FakeSettings());

    private static QuoteRequest Req() => new()
    {
        WarehouseId = "w1",
        Lines = new() { new QuoteLineInput { ProductId = "p1", Quantity = 1, UnitPrice = 10 } }
    };

    [Fact]
    public async Task QuoteAsync_PostsToEndpoint_ParsesDirectResult()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"quote_id":"q1","subtotal":10,"discount_total":1,"total":9,"lines":[],"applied":[],"rejected":[]}"""));
        var svc = Build(handler);

        var r = await svc.QuoteAsync(Req(), CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("q1", r!.QuoteId);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("discounts/quote/", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"warehouse_id\":\"w1\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task QuoteAsync_UnwrapsEnvelope()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"status":0,"message":"ok","body":{"quote_id":"q2","discount_total":5,"lines":[],"applied":[],"rejected":[]}}"""));
        var svc = Build(handler);

        var r = await svc.QuoteAsync(Req(), CancellationToken.None);

        Assert.Equal("q2", r!.QuoteId);
        Assert.Equal(5m, r.DiscountTotal);
    }

    [Fact]
    public async Task QuoteAsync_ReturnsNullOnNon200()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.BadRequest, """{"message":"bad"}"""));
        var svc = Build(handler);

        Assert.Null(await svc.QuoteAsync(Req(), CancellationToken.None));
    }
}
