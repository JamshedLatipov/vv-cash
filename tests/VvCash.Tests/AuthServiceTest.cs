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
        public string PhoneFormatId { get; set; } = string.Empty;
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
