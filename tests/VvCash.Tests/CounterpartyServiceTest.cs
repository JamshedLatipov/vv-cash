using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

/// <summary>Контракт поиска контрагентов на границе с сервером: что именно
/// означает null, а что — пустой список. Окно поиска строит на этом различии
/// пустое состояние, но само оно живёт на фейке, поэтому пин нужен здесь.</summary>
public class CounterpartyServiceTest
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
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save() { }
    }

    private static CounterpartyService Build(StubHttpMessageHandler handler)
        => new CounterpartyService(new HttpClient(handler), new FakeSettings());

    /// <summary>Пустой список на ошибке сервера означал бы «такого клиента нет»
    /// и приглашал завести дубль — с новой дисконтной картой поверх живого
    /// клиента, который на самом деле в базе есть.</summary>
    [Fact]
    public async Task SearchCounterpartiesAsync_ServerError_ReturnsNullNotEmptyList()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.InternalServerError, """{"detail":"boom"}"""));
        var svc = Build(handler);

        var res = await svc.SearchCounterpartiesAsync("Иванов");

        Assert.Null(res);
    }

    [Fact]
    public async Task SearchCounterpartiesAsync_ArrayBody_ReturnsTheList()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """[{"id":"c-1","full_name":"Иванов Иван"}]"""));
        var svc = Build(handler);

        var res = await svc.SearchCounterpartiesAsync("Иванов");

        Assert.NotNull(res);
        Assert.Single(res);
        Assert.Equal("c-1", res[0].Id);
        Assert.Equal("Иванов Иван", res[0].FullName);
    }

    [Fact]
    public async Task SearchCounterpartiesAsync_StatusBodyEnvelope_ReturnsTheListFromBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"status":0,"body":[{"id":"c-2","full_name":"Петров Пётр"}]}"""));
        var svc = Build(handler);

        var res = await svc.SearchCounterpartiesAsync("Петров");

        Assert.NotNull(res);
        Assert.Single(res);
        Assert.Equal("c-2", res[0].Id);
        Assert.Equal("Петров Пётр", res[0].FullName);
    }

    /// <summary>Реальный «ничего не найдено»: сервер отвечает через
    /// response.EmptyList, а тот поля "status" не несёт, поэтому ответ не
    /// подходит ни под голый массив, ни под конверт. Именно на этой ветке
    /// держится всё пустое состояние окна поиска — вернуть отсюда null значило
    /// бы, что «Клиент не найден» не покажется никогда.</summary>
    [Fact]
    public async Task SearchCounterpartiesAsync_EmptyListEnvelope_ReturnsEmptyNotNull()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (HttpStatusCode.OK, """{"body":[],"page_count":0,"total_items":0,"item_per_page":20}"""));
        var svc = Build(handler);

        var res = await svc.SearchCounterpartiesAsync("Такогонет");

        Assert.NotNull(res);
        Assert.Empty(res);
    }
}
