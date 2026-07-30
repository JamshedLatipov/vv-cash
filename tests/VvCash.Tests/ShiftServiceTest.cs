using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

/// <summary>Covers the escape-hatch fix's Task 2 half at the ShiftService level: a 401 from
/// GetShiftStateAsync/OpenShiftAsync must raise SessionRevoked (proving the server actually
/// rejected the token), while a request that never reached the server — thrown
/// HttpRequestException, standing in for "offline" — must not, since offline operation is
/// sacred and a register with no signal must never be treated as logged out. PosViewModelTest
/// covers the other half: what PosViewModel does once (or if) that event fires.</summary>
public class ShiftServiceTest
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
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSessionContext : ISessionContext
    {
        public string? WarehouseId { get; set; }
        public string? CashId { get; set; }
    }

    private static ShiftService CreateService(StubHttpMessageHandler handler, out FakeSessionContext session)
    {
        session = new FakeSessionContext();
        return new ShiftService(new HttpClient(handler), new FakeSettings(), session);
    }

    // -----------------------------------------------------------------------------
    // GetShiftStateAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task GetShiftStateAsync_401_RaisesSessionRevoked_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.Unauthorized, """{"message":"unauthorized","status":1}"""));
        var svc = CreateService(handler, out _);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(1, revokedCount);
    }

    [Fact]
    public async Task GetShiftStateAsync_NetworkUnreachable_DoesNotRaiseSessionRevoked_ReturnsNull()
    {
        // Simulates "offline": the request never reaches the server, so there is nothing to
        // conclude about the token's validity. Offline operation must never be conflated with
        // a revoked session.
        var handler = new StubHttpMessageHandler(req => throw new HttpRequestException("network down"));
        var svc = CreateService(handler, out _);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public async Task GetShiftStateAsync_Success_DoesNotRaiseSessionRevoked_ReturnsShiftId()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.OK, """{"status":0,"body":{"id":"shift-1","warehouse_id":"wh-1"}}"""));
        var svc = CreateService(handler, out var session);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("shift-1", result);
        Assert.Equal("wh-1", session.WarehouseId);
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public async Task GetShiftStateAsync_Success_LearnsTheCashId()
    {
        // The till payout is the one call that makes the client name its own cash
        // (every other endpoint reads it off the token), and the shift reply is where
        // that id comes from. Without it the exchange screen has to refuse.
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.OK, """{"status":0,"body":{"id":"shift-1","cash":"cash-7"}}"""));
        var svc = CreateService(handler, out var session);

        var result = await svc.GetShiftStateAsync();

        Assert.Equal("shift-1", result);
        Assert.Equal("cash-7", session.CashId);
    }

    [Fact]
    public void ExtractCashId_AcceptsFlatString_FlatIdField_AndNestedObject()
    {
        Assert.Equal("c1", ShiftService.ExtractCashId(Body("""{"id":"s","cash":"c1"}""")));
        Assert.Equal("c2", ShiftService.ExtractCashId(Body("""{"id":"s","cash_id":"c2"}""")));
        Assert.Equal("c3", ShiftService.ExtractCashId(Body("""{"id":"s","cash":{"id":"c3"}}""")));
        Assert.Null(ShiftService.ExtractCashId(Body("""{"id":"s"}""")));
    }

    // Clone so the element stays valid after the JsonDocument is disposed.
    private static System.Text.Json.JsonElement Body(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task GetShiftStateAsync_OtherServerError_DoesNotRaiseSessionRevoked_ReturnsNull()
    {
        // A 500 (or any non-401 failure) is a server problem, not a rejected session — must
        // not be treated the same as a 401.
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.InternalServerError, """{"message":"boom"}"""));
        var svc = CreateService(handler, out _);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.GetShiftStateAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(0, revokedCount);
    }

    // -----------------------------------------------------------------------------
    // OpenShiftAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task OpenShiftAsync_401_RaisesSessionRevoked_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.Unauthorized, """{"message":"unauthorized","status":1}"""));
        var svc = CreateService(handler, out _);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.OpenShiftAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(1, revokedCount);
    }

    [Fact]
    public async Task OpenShiftAsync_NetworkUnreachable_DoesNotRaiseSessionRevoked_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(req => throw new HttpRequestException("network down"));
        var svc = CreateService(handler, out _);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.OpenShiftAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(0, revokedCount);
    }

    [Fact]
    public async Task OpenShiftAsync_Success_DoesNotRaiseSessionRevoked_ReturnsShiftId()
    {
        var handler = new StubHttpMessageHandler(req =>
            (HttpStatusCode.OK, """{"status":0,"body":{"id":"shift-2","warehouse_id":"wh-2"}}"""));
        var svc = CreateService(handler, out var session);
        var revokedCount = 0;
        svc.SessionRevoked += (s, e) => revokedCount++;

        var result = await svc.OpenShiftAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("shift-2", result);
        Assert.Equal("wh-2", session.WarehouseId);
        Assert.Equal(0, revokedCount);
    }
}
