using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

/// <summary>Пересборка состава принтеров по SettingsChanged, пока печать идёт.
/// Кнопка пробной печати делает связку «поменял настройку → сразу печатаю»
/// обычным сценарием, а не редким.</summary>
public class CompositePrinterServiceTest
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
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PrinterConfig Lan(string address) => new()
    {
        Name = address,
        ConnectionType = PrinterConnectionType.LAN,
        ConnectionString = address,
        IsEnabled = true
    };

    [Fact]
    public async Task PrintingSurvivesASettingsChangeMidFlight()
    {
        // Ни один из адресов не отвечает, поэтому печать честно провалится —
        // проверяется не результат, а то, что метод не падает на изменившейся
        // под ним коллекции. До правки это InvalidOperationException из Select
        // по списку, который в этот момент чистят.
        var settings = new FakeSettings { Printers = { Lan("127.0.0.1:9101") } };
        var composite = new CompositePrinterService(settings);

        var printing = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m);
            }
        });

        var reconfiguring = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                settings.Printers = new List<PrinterConfig> { Lan($"127.0.0.1:{9102 + (i % 3)}") };
                settings.Save();
            }
        });

        await Task.WhenAll(printing, reconfiguring);
    }

    [Fact]
    public async Task NoPrintersConfigured_ReportsFailureRatherThanThrowing()
    {
        var composite = new CompositePrinterService(new FakeSettings());

        Assert.False(await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m));
        Assert.False(await composite.OpenCashDrawerAsync());
    }

    [Fact]
    public void EachPrinterGetsTheCodePageFromItsOwnConfig()
    {
        var settings = new FakeSettings
        {
            Printers =
            {
                new() { Name = "a", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.1:9100", IsEnabled = true, CodePageId = "CP1251" },
                new() { Name = "b", ConnectionType = PrinterConnectionType.LAN,
                        ConnectionString = "10.0.0.2:9100", IsEnabled = true, CodePageId = "" }
            }
        };

        var composite = new CompositePrinterService(settings);

        Assert.Same(EscPosCodePages.Cp1251, composite.Printers[0].CodePage);
        Assert.Same(EscPosCodePages.Default, composite.Printers[1].CodePage);
    }
}
