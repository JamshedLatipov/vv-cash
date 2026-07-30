using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

public class CashOperationServiceTest
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

    private static CashOperationService Build(StubHttpMessageHandler handler)
        => new CashOperationService(new HttpClient(handler), new FakeSettings());

    private static CashExpenseRequest Sample() => new()
    {
        Cash = "cash-1",
        Counterparty = "cp-1",
        Note = "Обмен по чеку 9",
        Details = new List<CashExpenseDetail> { new() { PaymentCategory = "cat-1", Amount = 80m } },
    };

    [Fact]
    public async Task CreateCashExpenseAsync_PostsTheServersCashOpShape()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, """{"status":0,"message":"success"}"""));
        var svc = Build(handler);

        var res = await svc.CreateCashExpenseAsync(Sample());

        Assert.True(res.Success);
        Assert.Equal("https://example.test/api/v1/documents/money/expense/create/",
            handler.LastRequest!.RequestUri!.ToString());
        var body = handler.LastRequestBody!;
        Assert.Contains("\"operation_type\":\"expense\"", body);
        Assert.Contains("\"cash\":\"cash-1\"", body);
        Assert.Contains("\"counterparty\":\"cp-1\"", body);
        Assert.Contains("\"payment_category\":\"cat-1\"", body);
        Assert.Contains("\"amount\":80", body);
    }

    [Fact]
    public async Task CreateCashExpenseAsync_HttpRefusal_CarriesTheServersReason()
    {
        // The reason is the whole point: this runs with a return already booked, and
        // "the till has no such payment category" and "you are not a seller of this
        // cash" call for completely different reactions.
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.BadRequest, """{"status":-1,"message":"error","body":"payment_category must be a uuid"}"""));
        var svc = Build(handler);

        var res = await svc.CreateCashExpenseAsync(Sample());

        Assert.False(res.Success);
        Assert.Equal("payment_category must be a uuid", res.Message);
    }

    [Fact]
    public async Task CreateCashExpenseAsync_200WithNonZeroStatus_IsStillARefusal()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, """{"status":1,"message":"cannot save money expense"}"""));
        var svc = Build(handler);

        var res = await svc.CreateCashExpenseAsync(Sample());

        Assert.False(res.Success);
        Assert.Equal("cannot save money expense", res.Message);
    }

    [Fact]
    public async Task CreateCashExpenseAsync_NetworkFailure_DoesNotThrow()
    {
        // Throwing here would leave the caller unable to say which legs are booked.
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("network down"));
        var svc = Build(handler);

        var res = await svc.CreateCashExpenseAsync(Sample());

        Assert.False(res.Success);
        Assert.Contains("network down", res.Message);
    }
}
