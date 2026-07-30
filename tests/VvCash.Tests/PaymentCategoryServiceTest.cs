using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class PaymentCategoryServiceTest
{
    private sealed class FakeSettings : ISettingsService
    {
        public string BackendUrl { get; set; } = "https://example.test/api/v1/";
        public string CashRegisterToken { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<VvCash.Models.PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; } = true;
        public bool ReturnPrintReceipt { get; set; } = true;
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save() { }
    }

    private static PaymentCategoryService Build(StubHttpMessageHandler handler)
        => new PaymentCategoryService(new HttpClient(handler), new FakeSettings());

    [Fact]
    public async Task GetPaymentCategoriesAsync_ParsesTheEnvelope()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"status":0,"message":"success","body":[{"id":"c1","name":"Аренда"},{"id":"c2","name":"Обмен"}]}"""));
        var svc = Build(handler);

        var res = await svc.GetPaymentCategoriesAsync();

        Assert.Equal(2, res.Count);
        Assert.Equal("c2", res[1].Id);
        Assert.Equal("Обмен", res[1].Name);
        Assert.Equal("https://example.test/api/v1/documents/payment/categories/",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetPaymentCategoriesAsync_Unreachable_ReturnsEmptyRatherThanThrowing()
    {
        // The settings screen opens from the login screen — possibly offline, possibly
        // before a backend has been configured at all. It must still open.
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("network down"));
        var svc = Build(handler);

        Assert.Empty(await svc.GetPaymentCategoriesAsync());
    }

    [Fact]
    public async Task GetPaymentCategoriesAsync_Forbidden_ReturnsEmpty()
    {
        // What a register whose role lacks documents.PaymentCategoryList actually sees.
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.Forbidden, """{"status":-1,"message":"forbidden"}"""));
        var svc = Build(handler);

        Assert.Empty(await svc.GetPaymentCategoriesAsync());
    }
}
