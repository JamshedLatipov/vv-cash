using System;
using System.Collections.Generic;
using System.Net.Http;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;
using Xunit;

namespace VvCash.Tests;

/// <summary>Covers Part 0b: ClearSession is the one place AuthToken/AuthTokenExpiresAt get
/// wiped outside of LoginAsync itself — moved here from PosViewModel, which used to reach
/// into ISettingsService directly and duplicate this logic without even holding an
/// IAuthService.</summary>
public class AuthServiceTest
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
        public bool ReturnOpenCashDrawer { get; set; }
        public bool ReturnPrintReceipt { get; set; }
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public int SaveCallCount { get; private set; }
        public event EventHandler? SettingsChanged;
        public void Save()
        {
            SaveCallCount++;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Fact]
    public void ClearSession_WipesTokenAndExpiry_AndPersists()
    {
        var settings = new FakeSettings
        {
            AuthToken = "some-token",
            AuthTokenExpiresAt = DateTime.UtcNow.AddHours(5)
        };
        var service = new AuthService(new HttpClient(), settings);

        service.ClearSession();

        Assert.Equal(string.Empty, settings.AuthToken);
        Assert.Null(settings.AuthTokenExpiresAt);
        Assert.Equal(1, settings.SaveCallCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task LoginAsync_SuccessEnvelopeWithNoToken_IsNotTreatedAsSignedIn()
    {
        // status 0 with no access_token is a malformed success. Reporting it as a login
        // let the register through to the POS screen with an empty AuthToken, so
        // AuthHeaderHandler sent no Authorization header at all and every later call
        // came back 401 — presenting as "the server keeps revoking my session" rather
        // than as the failed login it was.
        var handler = new StubHttpMessageHandler(_ =>
            (System.Net.HttpStatusCode.OK, """{"message":"ok","status":0}"""));
        var settings = new FakeSettings();
        var service = new AuthService(new HttpClient(handler), settings);

        var ok = await service.LoginAsync("cashier@example.test", "hunter2", rememberMe: true);

        Assert.False(ok);
        Assert.Equal(string.Empty, settings.AuthToken);
    }

    [Fact]
    public async System.Threading.Tasks.Task LoginAsync_SuccessEnvelopeWithToken_SignsIn()
    {
        var handler = new StubHttpMessageHandler(_ =>
            (System.Net.HttpStatusCode.OK, """{"message":"ok","status":0,"access_token":"tok-1"}"""));
        var settings = new FakeSettings();
        var service = new AuthService(new HttpClient(handler), settings);

        var ok = await service.LoginAsync("cashier@example.test", "hunter2", rememberMe: false);

        Assert.True(ok);
        Assert.Equal("tok-1", settings.AuthToken);
        Assert.Null(settings.AuthTokenExpiresAt); // rememberMe false
    }

    [Fact]
    public void ClearSession_WhenAlreadyClear_IsStillSafeToCall()
    {
        var settings = new FakeSettings();
        var service = new AuthService(new HttpClient(), settings);

        service.ClearSession();

        Assert.Equal(string.Empty, settings.AuthToken);
        Assert.Null(settings.AuthTokenExpiresAt);
        Assert.Equal(1, settings.SaveCallCount);
    }
}
