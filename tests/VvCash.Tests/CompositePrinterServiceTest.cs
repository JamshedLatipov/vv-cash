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

    /// <summary>Печатает мгновенно: ConnectionType нарочно вне диапазона enum, поэтому
    /// SendAsync уходит в свою ветку default: и возвращается без единого байта
    /// ввода-вывода — адрес в этой ветке не читается вовсе. address существует не
    /// ради маршрута, а чтобы разные поколения принтеров были различимы под
    /// отладчиком. Почему сам приём с ConnectionType оправдан здесь — в комментарии
    /// теста ниже.</summary>
    private static PrinterConfig Fast(string address) => new()
    {
        Name = address,
        ConnectionType = (PrinterConnectionType)99,
        ConnectionString = address,
        IsEnabled = true
    };

    [Fact]
    public async Task PrintingSurvivesASettingsChangeMidFlight()
    {
        // Транспорт не сетевой — намеренно. Печать на закрытый порт loopback здесь
        // отказывает не сразу, а за ~2.2с, и цикл перенастройки успевает отработать
        // всё, пока печать стоит на первой попытке: окно перекрытия схлопывается в
        // ноль, и первая редакция была зелёной что до фикса, что после.
        // (PrinterConnectionType)99 нарочно вне диапазона enum — уводит SendAsync в
        // default: без единого байта ввода-вывода, и гонка ловится с первой попытки
        // за десятки миллисекунд. Симптом на .NET 10 — не классический
        // InvalidOperationException, а NullReferenceException из ListSelectIterator.Fill
        // (Clear() успевает обнулить элемент внутреннего массива между чтениями
        // List<T>.Select()); корень тот же — другое исключение тут не значит, что
        // тест сгнил.
        var settings = new FakeSettings { Printers = { Fast("x1") } };
        var composite = new CompositePrinterService(settings);

        var printing = Task.Run(async () =>
        {
            for (var i = 0; i < 20000; i++)
            {
                await composite.PrintPreReceiptAsync(Array.Empty<CartItem>(), 0m);
            }
        });

        var reconfiguring = Task.Run(() =>
        {
            for (var i = 0; i < 20000; i++)
            {
                settings.Printers = new List<PrinterConfig> { Fast($"x{2 + (i % 3)}") };
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

        // Индексы 0/1 совпадают с порядком конфигурации только потому, что
        // Where(IsEnabled) сохраняет исходный порядок; отключённый принтер между
        // ними сдвинул бы их.
        Assert.Same(EscPosCodePages.Cp1251, composite.Printers[0].CodePage);
        Assert.Same(EscPosCodePages.Default, composite.Printers[1].CodePage);
    }
}
